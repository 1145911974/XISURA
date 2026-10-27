using System.Security.Cryptography;
using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static string WindowsScopeHash(WindowsScopeSession? scope) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope)));

    private static WindowsScopeSession RewoundWindowsScopes(WindowsScopeSession attempt, WindowsScopeSession? prior) =>
        attempt with { PendingWrite = prior?.PendingWrite, Schemes = attempt.Schemes.ToDictionary(pair => pair.Key,
            pair => pair.Value with { Writes = prior?.Schemes.GetValueOrDefault(pair.Key)?.Writes ?? new() }) };

    private static bool SameRollbackEvidence<T>(T left, T right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));

    private async Task<WindowsRollbackConfirmation?> ReadPriorWindowsRollbackAsync(ClientControlSessionSnapshot session,
        ControlJournalSnapshot saved, WindowsScopeSession? windows, CancellationToken token)
    {
        if (saved.Unresolved is not [var pending]) return null;
        var store = new AtomicJsonStore(Path.Combine(journal!.RootDirectory, "confirmed-windows-rollbacks"), pending.OperationId.ToString("N") + ".json");
        if (!File.Exists(store.FilePath)) return null;
        if (new FileInfo(store.FilePath).Length > 2 * 1024 * 1024) throw new InvalidDataException("windowsRollbackArchiveTooLarge");
        var archive = await store.ReadAsync<WindowsRollbackConfirmation>(token);
        if (archive?.Attempt is not { Schemes: not null } attempt || VerifiedCpuWindowsProof(saved, attempt) is not { PriorFields: { } fields } proof ||
            proof.PriorScopeHash != WindowsScopeHash(archive.Prior) || attempt.SessionId != session.Handle.SessionId ||
            attempt.ServiceSessionId != session.ServiceSessionId || attempt.Epoch != session.Handle.HardwareEpoch ||
            attempt.DeviceKey != session.Handle.DeviceKey || attempt.OwnerSid != session.OwnerSid ||
            PriorPendingCpuFields(session, saved).Length == 0 ||
            saved.Current!.CpuWriteBatch!.Plans.SelectMany(CpuFields).Any(field => field != "windowsPowerSchemeId" && !IsWindowsIndexField(field)) ||
            !fields.Keys.Order().SequenceEqual(PendingCpuOwnedFields(saved).Select(pair => pair.Key).Order()) ||
            !fields.Keys.Order().SequenceEqual(saved.Current.CpuWriteBatch.Plans.SelectMany(CpuFields).Distinct().Select(field => "performance." + field).Order()) ||
            fields.Values.Any(field => field is not null && (field.ClientSessionId != session.Handle.SessionId || field.OwnerSid != session.OwnerSid ||
                field.Epoch != session.Handle.HardwareEpoch || field.OperationId == Guid.Empty || field.OperationId == pending.OperationId ||
                field.State is not (ApplyState.Confirmed or ApplyState.SubmittedUnverified))) ||
            !SameRollbackEvidence(windows, attempt) && !SameRollbackEvidence(windows, RewoundWindowsScopes(attempt, archive.Prior)))
            throw new InvalidDataException("windowsRollbackArchiveUnverified");
        return archive;
    }

    private static ControlJournalSnapshot ProjectConfirmedWindowsRollback(ControlJournalSnapshot saved, WindowsRollbackConfirmation archive)
    {
        var fields = new Dictionary<string, FieldExecutionState>(saved.Current!.Fields);
        var owners = new Dictionary<string, ControlFieldRecoveryScope>(saved.RecoveryScope!.Fields);
        foreach (var pair in archive.Attempt.PendingWrite!.PriorFields!)
        {
            if (pair.Value is not { } previous) { fields.Remove(pair.Key); owners.Remove(pair.Key); continue; }
            fields[pair.Key] = new(previous.OperationId, previous.Origin, previous.Baseline, previous.LastWritten, previous.State)
            {
                ClientSessionId = previous.ClientSessionId, BaselineDetails = previous.BaselineDetails, LastWrittenDetails = previous.LastDetails,
                WriteBefore = previous.WriteBefore, WriteBeforeDetails = previous.WriteBeforeDetails, WriteTargetDetails = previous.WriteTargetDetails
            };
            owners[pair.Key] = new(previous.OwnerSid, previous.Epoch, previous.Scheme, previous.Generation);
        }
        return saved with { Current = saved.Current with { Fields = fields, Pages = [], CpuWriteBatch = null, WindowsWriteProofHash = null, PendingStep = null },
            Unresolved = [], RecoveryScope = saved.RecoveryScope with { Fields = owners } };
    }

    private async Task<string?> VerifyPriorWindowsRollbackAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved,
        WindowsScopeSession? windows, WindowsRollbackConfirmation archive, CancellationToken token)
    {
        var proof = archive.Attempt.PendingWrite!;
        for (int observation = 0; observation < 2; observation++)
        {
            if (!SameRollbackEvidence(windows, await ReadWindowsScopesAsync(session, true, token)) ||
                !SameRollbackEvidence(archive, await ReadPriorWindowsRollbackAsync(session, saved, windows, token)))
                return "WindowsRollbackRecordsChanged";
            var actual = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
            if (actual.CpuTuning?.WindowsPowerSchemeId != proof.InitialScheme) return "WindowsRollbackSelectionChanged";
            foreach (var scheme in proof.Schemes)
            {
                var values = await ReadWindowsValuesAsync(scheme.Key, token);
                foreach (var index in scheme.Value.Where(pair => pair.Value.Targets.Length > 0))
                    if (values.GetValueOrDefault(index.Key, -1) != index.Value.Before) return "WindowsRollbackValuesChanged";
            }
        }
        // Original ownership, unrelated fields and every inactive index must also remain independently verifiable.
        return await VerifyPriorLiveStateAsync(session, ProjectConfirmedWindowsRollback(saved, archive),
            RewoundWindowsScopes(archive.Attempt, archive.Prior), token);
    }

    private Task<bool> ReconcilePriorWindowsRollbackAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved,
        WindowsScopeSession? windows, WindowsRollbackConfirmation archive, CancellationToken token) =>
        Execution.WithRecoveryAdmissionAsync(saved.Unresolved.Single().OperationId, session.OwnerSid, async () =>
        {
            await VerifyPriorRestoreWriteAsync();
            if (await VerifyPriorWindowsRollbackAsync(session, saved, windows, archive, token) is not null) return false;
            if (!SamePriorJournal(saved, await new ControlExecutionJournal(journal!.RootDirectory).ReadAsync(token)))
                throw new InvalidDataException("windowsRollbackOriginalJournalChanged");
            await VerifyPriorRestoreWriteAsync();
            await WindowsScopeStore(session).WriteAsync(RewoundWindowsScopes(archive.Attempt, archive.Prior), token);
            var previous = new Dictionary<string, OwnedField>(ownedFields);
            var priorState = controlExecutionState;
            try
            {
                foreach (var pair in archive.Attempt.PendingWrite!.PriorFields!)
                    if (pair.Value is { } owner) ownedFields[pair.Key] = owner; else ownedFields.Remove(pair.Key);
                Volatile.Write(ref controlExecutionState, ProjectConfirmedWindowsRollback(saved, archive).Current);
                var pending = saved.Unresolved.Single();
                await PersistExecutionAsync(pending.OperationId, false, token, commandKind: pending.CommandKind);
                return true;
            }
            catch
            {
                ownedFields.Clear();
                foreach (var pair in previous) ownedFields[pair.Key] = pair.Value;
                Volatile.Write(ref controlExecutionState, priorState);
                throw;
            }
        });
}
