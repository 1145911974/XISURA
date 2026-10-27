using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private Guid? windowsScopeSessionId;
    private sealed record WindowsIndexOwnership(int LastWritten, bool Restored = false);
    private sealed record WindowsSchemeOwnership(Dictionary<WindowsCpuSetting, int> Baseline,
        Dictionary<WindowsCpuSetting, WindowsIndexOwnership> Writes);
    private sealed record WindowsPendingIndex(int? Before, int[] Targets);
    private sealed record WindowsPendingWrite(Guid OperationId, Guid InitialScheme,
        Dictionary<Guid, Dictionary<WindowsCpuSetting, WindowsPendingIndex>> Schemes)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, OwnedField?>? PriorFields { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? PriorScopeHash { get; init; }
    }
    private sealed record WindowsScopeSession(int SchemaVersion, Guid SessionId, Guid ServiceSessionId, long Epoch,
        string DeviceKey, string OwnerSid, Dictionary<Guid, WindowsSchemeOwnership> Schemes)
    {
        public WindowsPendingWrite? PendingWrite { get; init; }
    }
    private sealed record PreparedWindowsWrite(ClientControlSessionSnapshot Session, WindowsScopeSession? Prior, WindowsScopeSession Next);

    private AtomicJsonStore WindowsScopeStore(ClientControlSessionSnapshot session) => new(
        journal?.RootDirectory ?? throw new InvalidDataException("windowsScopeJournalMissing"),
        "client-windows-" + session.Handle.SessionId.ToString("N") + ".json");

    private async Task<WindowsScopeSession?> ReadWindowsScopesAsync(ClientControlSessionSnapshot session, bool required, CancellationToken token)
    {
        var store = WindowsScopeStore(session);
        if (!File.Exists(store.FilePath))
            return required ? throw new InvalidDataException("windowsScopeEvidenceMissing") : null;
        if (new FileInfo(store.FilePath).Length > 512 * 1024) throw new InvalidDataException("windowsScopeEvidenceTooLarge");
        var saved = await store.ReadAsync<WindowsScopeSession>(token);
        if (saved is not { SchemaVersion: 1, Schemes: not null } || saved.SessionId != session.Handle.SessionId ||
            saved.ServiceSessionId != session.ServiceSessionId || saved.Epoch != session.Handle.HardwareEpoch ||
            saved.DeviceKey != session.Handle.DeviceKey || saved.OwnerSid != session.OwnerSid || saved.Schemes.Count > 256 ||
            saved.Schemes.Any(pair => pair.Key == Guid.Empty || pair.Value is null || pair.Value.Baseline is null || pair.Value.Writes is null ||
                pair.Value.Baseline.Any(value => !ValidWindowsIndex(value.Key, value.Value)) ||
                pair.Value.Writes.Any(value => value.Value is null || !pair.Value.Baseline.ContainsKey(value.Key) || !ValidWindowsIndex(value.Key, value.Value.LastWritten))))
            throw new InvalidDataException("windowsScopeEvidenceInvalid");
        return saved;
    }

    private static bool ValidWindowsIndex(WindowsCpuSetting key, int value) => Enum.IsDefined(key) && value >= 0 &&
        (key is WindowsCpuSetting.AcBoostMode or WindowsCpuSetting.DcBoostMode ? value <= 6 :
         key is WindowsCpuSetting.AcMinActiveCoresPercent or WindowsCpuSetting.DcMinActiveCoresPercent ? value <= 100 : true);

    private static Dictionary<WindowsCpuSetting, int> WindowsValues(CpuTuningState? cpu)
    {
        var values = new Dictionary<WindowsCpuSetting, int>();
        void Add(WindowsCpuSetting key, int? value) { if (value is int number && ValidWindowsIndex(key, number)) values[key] = number; }
        Add(WindowsCpuSetting.AcMaxFrequencyMhz, cpu?.AcFrequency()); Add(WindowsCpuSetting.DcMaxFrequencyMhz, cpu?.DcFrequency());
        Add(WindowsCpuSetting.AcBoostMode, cpu?.AcBoostMode); Add(WindowsCpuSetting.DcBoostMode, cpu?.DcBoostMode);
        Add(WindowsCpuSetting.AcMinActiveCoresPercent, cpu?.AcMinActiveCoresPercent); Add(WindowsCpuSetting.DcMinActiveCoresPercent, cpu?.DcMinActiveCoresPercent);
        return values;
    }

    private static Dictionary<WindowsCpuSetting, int> WindowsValues(WindowsPowerSettingsSnapshot power) => WindowsValues(
        new CpuTuningState(null, null, null, null, null, null, power.ActiveSchemeId, null, null)
        { AcMaxFrequencyMhz = power.AcMaxFrequencyMhz, DcMaxFrequencyMhz = power.DcMaxFrequencyMhz,
          AcBoostMode = power.AcBoostMode, DcBoostMode = power.DcBoostMode,
          AcMinActiveCoresPercent = power.AcMinActiveCoresPercent, DcMinActiveCoresPercent = power.DcMinActiveCoresPercent });

    private async Task<Dictionary<WindowsCpuSetting, int>> ReadWindowsValuesAsync(Guid scheme, CancellationToken token)
    {
        EnsureControlHardwareAccess();
        var exact = await provider.ReadWindowsPowerSchemeAsync(scheme, token);
        EnsureControlHardwareAccess();
        if (exact is not null)
        {
            if (exact.ActiveSchemeId != scheme) throw new InvalidDataException("windowsScopeReadIdentityMismatch");
            return WindowsValues(exact);
        }
        var controls = await ReadControlBaselineForExecutionAsync(token);
        if (controls.CpuTuning?.WindowsPowerSchemeId != scheme) throw new InvalidDataException("windowsScopeUnreadable");
        return WindowsValues(controls.CpuTuning);
    }

    private static bool HasWindowsSettings(CpuTuningPlan plan) => plan.WindowsPowerSchemeId is not null || plan.MaxFrequencyMhz is not null ||
        plan.AcMaxFrequencyMhz is not null || plan.DcMaxFrequencyMhz is not null || plan.BoostEnabled is not null || plan.BoostPolicy is not null ||
        plan.AcMinActiveCoresPercent is not null || plan.DcMinActiveCoresPercent is not null;

    private async Task<PreparedWindowsWrite?> PrepareWindowsWriteAsync(HardwareCommand command, CancellationToken token,
        Dictionary<string, OwnedField?>? priorFields = null)
    {
        var session = Volatile.Read(ref activeClientSession);
        if (session is null || session.Phase != ClientControlSessionPhase.Active || Volatile.Read(ref clientSessionClosing) != 0) return null;
        CpuTuningPlan[] plans = command switch { SetCpuTuningCommand cpu => [cpu.Plan], SetCpuTuningBatchCommand batch => batch.Plans, _ => [] };
        if (!plans.Any(HasWindowsSettings)) return null;
        var prior = await ReadWindowsScopesAsync(session, windowsScopeSessionId == session.Handle.SessionId, token);
        var next = prior is null ? new WindowsScopeSession(1, session.Handle.SessionId, session.ServiceSessionId, session.Handle.HardwareEpoch,
            session.Handle.DeviceKey, session.OwnerSid, new()) : JsonSerializer.Deserialize<WindowsScopeSession>(JsonSerializer.Serialize(prior))!;
        var live = await ReadControlBaselineForExecutionAsync(token);
        Guid scheme = live.CpuTuning?.WindowsPowerSchemeId ?? throw new InvalidDataException("windowsActiveSchemeUnknown");
        Guid operationId = Execution.CurrentAdmission?.OperationId ?? command.OperationId;
        var attempt = new WindowsPendingWrite(operationId, scheme, new())
        { PriorFields = priorFields, PriorScopeHash = priorFields is null ? null : WindowsScopeHash(prior) };
        var virtualValues = new Dictionary<Guid, Dictionary<WindowsCpuSetting, int>>();
        foreach (var plan in plans)
        {
            scheme = plan.WindowsPowerSchemeId ?? scheme;
            if (!HasWindowsSettings(plan)) continue;
            if (!virtualValues.TryGetValue(scheme, out var values))
            {
                virtualValues[scheme] = values = await ReadWindowsValuesAsync(scheme, token);
                attempt.Schemes[scheme] = values.ToDictionary(pair => pair.Key, pair => new WindowsPendingIndex(pair.Value, []));
            }
            if (!next.Schemes.TryGetValue(scheme, out var owned))
            {
                var baseline = scheme == session.Baseline.CpuTuning?.WindowsPowerSchemeId ? WindowsValues(session.Baseline.CpuTuning) : new(values);
                next.Schemes[scheme] = owned = new(baseline, new());
            }
            int Current(WindowsCpuSetting key) => values.TryGetValue(key, out int number) ? number : throw new InvalidDataException("windowsIndexUnreadable");
            void Set(WindowsCpuSetting key, int value)
            {
                if (!owned.Baseline.ContainsKey(key) || !ValidWindowsIndex(key, value)) throw new InvalidDataException("windowsIndexBaselineOrTargetInvalid");
                owned.Writes[key] = new(value); values[key] = value;
                var marker = attempt.Schemes[scheme][key];
                attempt.Schemes[scheme][key] = marker with { Targets = [.. marker.Targets, value] };
            }
            ApplyWindowsPlan(plan, Current, Set);
        }
        next = next with { PendingWrite = attempt };
        await WindowsScopeStore(session).WriteAsync(next, token);
        windowsScopeSessionId = session.Handle.SessionId;
        if (controlExecutionState?.CpuWriteBatch?.OperationId == operationId)
            Volatile.Write(ref controlExecutionState, controlExecutionState with { WindowsWriteProofHash = WindowsPendingProofHash(attempt) });
        await PersistExecutionAsync(Execution.CurrentAdmission?.OperationId ?? command.OperationId, true, token);
        return new(session, prior, next);
    }

    private sealed record WindowsRollbackConfirmation(WindowsScopeSession Attempt, WindowsScopeSession? Prior);

    private async Task<CommandResult> CompleteWindowsWriteAsync(PreparedWindowsWrite? prepared, CommandResult result, CancellationToken token)
    {
        if (prepared is null || result.Mutation != HardwareMutation.NoMutation && result.State != CommandState.RolledBack) return result;
        try
        {
            if (result.State == CommandState.RolledBack)
            {
                await ConfirmWindowsRollbackAsync(prepared, token);
                var proof = new WindowsRollbackConfirmation(prepared.Next, prepared.Prior);
                var archive = new AtomicJsonStore(Path.Combine(journal!.RootDirectory, "confirmed-windows-rollbacks"), prepared.Next.PendingWrite!.OperationId.ToString("N") + ".json");
                if (File.Exists(archive.FilePath))
                {
                    if (new FileInfo(archive.FilePath).Length > 2 * 1024 * 1024 || !JsonElement.DeepEquals(
                        JsonSerializer.SerializeToElement(await archive.ReadAsync<WindowsRollbackConfirmation>(token)), JsonSerializer.SerializeToElement(proof)))
                        throw new InvalidDataException("windowsRollbackConfirmationChanged");
                }
                else await archive.WriteAsync(proof, token);
            }
            // Keep the first capture; confirmed rollback retires only this attempt's ownership.
            var rollback = RewoundWindowsScopes(prepared.Next, prepared.Prior);
            await WindowsScopeStore(prepared.Session).WriteAsync(rollback, token);
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return result with { State = CommandState.RecoveryRequired, Mutation = HardwareMutation.Unknown,
                Error = ServiceError.Create(ErrorCode.RollbackFailed, result.OperationId, false) with
                    { Details = new() { ["stage"] = "WindowsRollbackConfirmation", ["cause"] = error.Message } } };
        }
    }

    private async Task ConfirmWindowsRollbackAsync(PreparedWindowsWrite prepared, CancellationToken token)
    {
        var attempt = prepared.Next.PendingWrite ?? throw new InvalidDataException("windowsRollbackAttemptMissing");
        for (int observation = 0; observation < 2; observation++)
        {
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(prepared.Next),
                JsonSerializer.SerializeToElement(await ReadWindowsScopesAsync(prepared.Session, true, token))))
                throw new InvalidDataException("windowsRollbackScopeChanged");
            var controls = await ReadControlBaselineForExecutionAsync(token);
            if (controls.CpuTuning?.WindowsPowerSchemeId != attempt.InitialScheme)
                throw new InvalidDataException("windowsRollbackSelectionMismatch");
            foreach (var scheme in attempt.Schemes)
            {
                var values = await ReadWindowsValuesAsync(scheme.Key, token);
                foreach (var index in scheme.Value.Where(pair => pair.Value.Targets.Length > 0))
                    if (!values.TryGetValue(index.Key, out int current) || current != index.Value.Before)
                        throw new InvalidDataException("windowsRollbackIndexMismatch");
            }
        }
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(prepared.Next),
            JsonSerializer.SerializeToElement(await ReadWindowsScopesAsync(prepared.Session, true, token))))
            throw new InvalidDataException("windowsRollbackScopeChanged");
    }

    private static bool IsWindowsIndexField(string field) => field is "acMaxFrequencyMhz" or "dcMaxFrequencyMhz" or "isBoostEnabled" or
        "acMinActiveCoresPercent" or "dcMinActiveCoresPercent";

    private async Task<bool> WindowsScopesMatchAsync(WindowsScopeSession? scopes, bool restored, CancellationToken token)
    {
        if (scopes is null) return true;
        foreach (var scheme in scopes.Schemes)
        {
            Dictionary<WindowsCpuSetting, int> actual;
            try { actual = await ReadWindowsValuesAsync(scheme.Key, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { return false; }
            foreach (var field in scheme.Value.Writes)
                if (!actual.TryGetValue(field.Key, out int value) || (restored ? value != scheme.Value.Baseline[field.Key] :
                    value != field.Value.LastWritten && value != scheme.Value.Baseline[field.Key])) return false;
        }
        return true;
    }

    private async Task<bool> RestoreWindowsScopesAsync(ClientControlSessionSnapshot session, WindowsScopeSession? scopes, CancellationToken token)
    {
        if (scopes is null) return true;
        bool success = true;
        foreach (var scheme in scopes.Schemes)
        {
            foreach (var field in scheme.Value.Writes.ToArray())
            {
                Dictionary<WindowsCpuSetting, int> actual;
                try { actual = await ReadWindowsValuesAsync(scheme.Key, token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { success = false; continue; }
                int target = scheme.Value.Baseline[field.Key];
                if (!actual.TryGetValue(field.Key, out int current)) { success = false; continue; }
                if (current != target)
                {
                    if (field.Value.Restored || current != field.Value.LastWritten) { success = false; continue; }
                    string capabilityKey = field.Key is WindowsCpuSetting.AcBoostMode or WindowsCpuSetting.DcBoostMode ? "isBoostEnabled" :
                        char.ToLowerInvariant(field.Key.ToString()[0]) + field.Key.ToString()[1..];
                    if (!PresetValidation.IsVerifiedWritable(State.Capabilities.Fields.FirstOrDefault(item => item.Key == capabilityKey), Execution.HardwareEpoch))
                    { success = false; continue; }
                    CommandResult result;
                    try
                    {
                        result = await TrackHardwareDispatchAsync(() => provider.RestoreWindowsPowerSettingAsync(Guid.NewGuid(), scheme.Key,
                            field.Key, field.Value.LastWritten, target, token));
                        actual = await ReadWindowsValuesAsync(scheme.Key, token);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    { success = false; continue; }
                    if (result is not { State: CommandState.Applied, Error: null } || actual.GetValueOrDefault(field.Key, -1) != target)
                    { success = false; continue; }
                }
                scheme.Value.Writes[field.Key] = field.Value with { Restored = true };
                await WindowsScopeStore(session).WriteAsync(scopes, token);
            }
        }
        return success && await WindowsScopesMatchAsync(scopes, true, token);
    }
}
