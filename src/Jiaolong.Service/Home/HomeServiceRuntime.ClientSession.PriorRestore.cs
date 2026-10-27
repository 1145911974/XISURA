using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    // This scope is service-internal and never makes SessionEpochMatches accept an old client lease.
    private sealed class PriorRestoreScope(ClientControlSessionSnapshot session, long currentEpoch, long lifecycle,
        IClientProcessProbe probe, CancellationToken token, PriorClientClose? explicitClose)
    {
        private int active = 1;
        public ClientControlSessionSnapshot Session { get; } = session;
        public long CurrentEpoch { get; } = currentEpoch;
        public long Lifecycle { get; } = lifecycle;
        public IClientProcessProbe Probe { get; } = probe;
        public PriorClientClose? ExplicitClose { get; } = explicitClose;
        public CancellationToken Token { get; } = token;
        public bool Active => Volatile.Read(ref active) != 0;
        public void Revoke() => Interlocked.Exchange(ref active, 0);
    }
    private readonly AsyncLocal<PriorRestoreScope?> priorRestore = new();
    private Guid JournalServiceSessionId => CheckedPriorRestore()?.Session.ServiceSessionId ?? SessionId;
    private long JournalHardwareEpoch => CheckedPriorRestore()?.Session.Handle.HardwareEpoch ?? Execution.HardwareEpoch;

    private PriorRestoreScope? CheckedPriorRestore()
    {
        var scope = priorRestore.Value;
        if (scope is not null && (!scope.Active || IsSuspended || scope.CurrentEpoch != Execution.HardwareEpoch || scope.Lifecycle != powerLifecycle.Version))
            throw new IOException("priorSessionRestoreScopeExpired");
        return scope;
    }

    private async Task VerifyPriorRestoreWriteAsync()
    {
        var scope = CheckedPriorRestore();
        if (scope is null) return;
        var token = scope.Token;
        token.ThrowIfCancellationRequested();
        var session = scope.Session;
        if (!PriorProcessMatches(session, scope.Probe.Observe(session.ProcessId, session.ProcessCreationFileTimeUtc), scope.ExplicitClose) ||
            readSystemBootTime is null || await readSystemBootTime(token) != session.BootStartedAtUtc ||
            readSystemPowerContinuity is null || await readSystemPowerContinuity(token) != session.PowerContinuity ||
            await provider.ReadDeviceRecoveryKeyAsync(session.Identity, token) != session.Handle.DeviceKey)
            throw new InvalidDataException("priorSessionRestoreEvidenceChanged");
        var capture = await clientSessions!.ReadAsync(token);
        if (capture?.Phase != ClientControlSessionPhase.Closing || !ClientControlSessionStore.SameCapture(session, capture) ||
            !File.Exists(Path.Combine(journal!.RootDirectory, "control-execution.json")) ||
            !SamePriorJournal(await journal.ReadAsync(token), await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token)))
            throw new InvalidDataException("priorSessionRestoreOriginalRecordsChanged");
        CheckedPriorRestore();
    }

    private async Task<CommandResult> RunPriorSessionExitAsync(Guid operationId, ClientControlSessionSnapshot session,
        ControlJournalSnapshot saved, WindowsScopeSession? windows, HomeHardwareState hardware, IClientProcessProbe probe,
        CancellationToken token, ExitPlan? resumePlan = null, WindowsRollbackConfirmation? windowsRollback = null,
        ExitPlan? archivedPlan = null, PriorClientClose? explicitClose = null)
    {
        if (priorRestore.Value is not null || journal is null || saved.RecoveryScope is null || saved.Current is null ||
            !PriorRestoreRuntimeMatches(session, saved))
            throw new InvalidDataException("priorSessionRestoreRuntimeNotEmpty");
        var scope = new PriorRestoreScope(session, Execution.HardwareEpoch, powerLifecycle.Version, probe, token, explicitClose);
        priorRestore.Value = scope;
        hardwareDispatched.Value = new(scope.Lifecycle);
        try
        {
            CheckedPriorRestore();
            clientSessions ??= new(journal.RootDirectory);
            Interlocked.Exchange(ref clientSessionClosing, 1);
            var closing = await clientSessions.WriteAsync(session with { Phase = ClientControlSessionPhase.Closing }, token, requireExisting: true);
            Volatile.Write(ref activeClientSession, closing);
            Volatile.Write(ref state, StampCapabilities(hardware));
            ownedFields.Clear();
            foreach (var pair in saved.Current.Fields)
            {
                var field = pair.Value;
                var owner = saved.RecoveryScope.Fields[pair.Key];
                ownedFields[pair.Key] = new(field.OwnerOperationId, field.Origin, field.Baseline, field.LastWritten,
                    field.WriteState, owner.HardwareEpoch, owner.WindowsPowerSchemeId, owner.Generation, field.BaselineDetails, field.LastWrittenDetails)
                    {
                        OwnerSid = owner.OwnerSid, ClientSessionId = field.ClientSessionId,
                        WriteBefore = field.WriteBefore, WriteBeforeDetails = field.WriteBeforeDetails, WriteTargetDetails = field.WriteTargetDetails
                    };
            }
            Volatile.Write(ref controlExecutionState, saved.Current);
            priorSessionFieldsLoaded = true;
            lastSubmittedModeClientSessionId = saved.Current.NativeModeOwnerClientSessionId;
            lastSubmittedMode = saved.RecoveryScope.NativeModeTarget;
            lastModeEpoch = saved.RecoveryScope.NativeModeEpoch;
            windowsScopeSessionId = saved.Current.WindowsScopeSessionId;
            fanOwnerClientSessionId = saved.RecoveryScope.FanOwnerClientSessionId;
            if (resumePlan is null && saved.Unresolved.Length > 0)
            {
                bool restoredPending = windowsRollback is not null
                    ? await ReconcilePriorWindowsRollbackAsync(closing, saved, windows, windowsRollback, token)
                    : PriorPendingLighting(closing, saved) is not null ? await ReconcilePriorPendingLightingAsync(closing, saved, token)
                    : PriorPendingFan(closing, saved) is not null
                    ? await RestorePriorPendingFanAsync(closing, saved, token)
                    : PriorPendingGpuFields(closing, saved).Length > 0 ? await RecoverPriorManualGpuAsync(closing, saved, token)
                    : PriorPendingCpuFields(closing, saved).Length > 0 ? await RestorePriorPendingCpuAsync(closing, saved, windows, token)
                    : await ReconcilePriorNativeModeAsync(closing, saved, token);
                if (!restoredPending)
                    return SessionRefused(operationId, ErrorCode.RollbackFailed, true) with
                    { Mutation = hardwareDispatched.Value?.Started == true ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
                saved = await journal.ReadAsync(token);
                if (windowsRollback is not null) windows = await ReadWindowsScopesAsync(closing, true, token);
            }
            var plan = resumePlan ?? BuildExitPlan(closing, operationId, windows, ControlExecutionJournal.IsFanFirmwareOnlyRecovery(closing, saved),
                SessionFanMapping(closing, saved));
            var store = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
            if (resumePlan is null)
            {
                if (File.Exists(store.FilePath))
                {
                    var current = await store.ReadAsync<ExitPlan>(token);
                    if (archivedPlan is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(archivedPlan), JsonSerializer.SerializeToElement(current)))
                        throw new InvalidDataException("priorSessionExitPlanAlreadyExists");
                    await VerifyArchivedPriorExitPlanAsync(closing, archivedPlan, token);
                }
                await store.WriteAsync(plan, token);
            }
            if (resumePlan is not null)
            {
                await VerifyPriorRestoreWriteAsync();
                var live = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
                bool idleFanRelease = await VerifyIdleFanReleaseAsync(closing, plan.OperationId, live, token, saved);
                if (!idleFanRelease && saved.Unresolved.Any(item => item.OperationId != plan.OperationId))
                    throw new InvalidDataException("idleFanReleaseEvidenceChanged");
                if (idleFanRelease)
                {
                    // Only the shutdown release marker is redundant; the original End stays authoritative.
                    foreach (var item in saved.Unresolved.Where(item => item.OperationId != plan.OperationId))
                        await journal.ResolveNoMutationAsync(item.OperationId, saved.Current, token, saved.RecoveryScope);
                    Volatile.Write(ref controlExecutionState, saved.Current! with { Pages = [] });
                }
            }
            return await Execution.WithRecoveryAdmissionAsync(plan.OperationId, closing.OwnerSid,
                () => RunExitPlanAsync(operationId, closing, plan, store, windows, token));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
        {
            var result = SessionRefused(operationId, ErrorCode.RollbackFailed, true);
            return result with { Mutation = hardwareDispatched.Value?.Started == true ? HardwareMutation.Unknown : HardwareMutation.NoMutation,
                Error = result.Error! with { Details = new() { ["clientSessionRecovery"] = "PriorRestoreInterrupted" } } };
        }
        finally
        {
            scope.Revoke();
            priorRestore.Value = null;
            hardwareDispatched.Value = null;
        }
    }

    private bool PriorRestoreRuntimeMatches(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        if (ownedFields.Count == 0 && controlExecutionState is null) return true;
        return activeClientSession is { Phase: ClientControlSessionPhase.Closing } current &&
            ClientControlSessionStore.SameCapture(session, current) &&
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(SnapshotFields()), JsonSerializer.SerializeToElement(saved.Current!.Fields)) &&
            ownedFields.All(pair => saved.RecoveryScope!.Fields.TryGetValue(pair.Key, out var scope) &&
                scope == new ControlFieldRecoveryScope(pair.Value.OwnerSid, pair.Value.Epoch, pair.Value.Scheme, pair.Value.Generation)) &&
            lastSubmittedModeClientSessionId == saved.Current.NativeModeOwnerClientSessionId &&
            lastSubmittedMode == saved.RecoveryScope!.NativeModeTarget && lastModeEpoch == saved.RecoveryScope.NativeModeEpoch &&
            windowsScopeSessionId == saved.Current.WindowsScopeSessionId && fanOwnerClientSessionId == saved.RecoveryScope.FanOwnerClientSessionId;
    }
}
