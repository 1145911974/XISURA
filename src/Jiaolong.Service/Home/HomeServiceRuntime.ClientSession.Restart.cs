using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Ipc;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private sealed record PriorClientClose(Guid OperationId, Guid SessionId, ClientIdentity Client)
    {
        public bool KeepChanges { get; init; }
        public bool Matches(ClientControlSessionSnapshot session) => session.Handle.SessionId == SessionId && MatchesSessionProcess(session, Client);
    }

    private static bool PriorProcessMatches(ClientControlSessionSnapshot session, ClientProcessState state, PriorClientClose? explicitClose) =>
        explicitClose is null ? state == ClientProcessState.Exited : state == ClientProcessState.Running && explicitClose.Matches(session);

    internal Task<CommandResult?> RestorePriorClientSessionAsync(IClientProcessProbe probe, CancellationToken token) =>
        Execution.WithHardwareGateAsync(() => InspectPriorClientSessionAsync(probe, token, restore: true), token);

    // Inspection alone never grants restoration authority; the internal entry opts into the bounded executor.
    private async Task<CommandResult?> InspectPriorClientSessionAsync(IClientProcessProbe probe, CancellationToken token, bool restore = false,
        PriorClientClose? explicitClose = null)
    {
        Guid operationId = explicitClose?.OperationId ?? Guid.NewGuid();
        long epoch = Execution.HardwareEpoch, lifecycle = powerLifecycle.Version;
        bool Fresh() => !IsSuspended && Execution.HardwareEpoch == epoch && powerLifecycle.Version == lifecycle;
        CommandResult Retain(string reason)
        {
            var result = SessionRefused(operationId, ErrorCode.RollbackFailed, true);
            return result with { Error = result.Error! with { Details = new() { ["clientSessionRecovery"] = reason } } };
        }

        try
        {
            if (!Fresh()) return Retain("ServiceEpochChanged");
            var cachedSession = Volatile.Read(ref activeClientSession);
            if (cachedSession is not null && SessionEpochMatches(cachedSession)) return null;
            var store = clientSessions ?? new ClientControlSessionStore(journal?.RootDirectory ?? MachinePaths.BaseDirectory);
            var session = await store.ReadAsync(token);
            if (session is null)
            {
                if (cachedSession is not null) return Retain("SessionCaptureMissing");
                if (journal is null) return null;
                var orphan = await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token);
                return HasPriorRecovery(orphan) ? Retain("SessionCaptureMissing") : null;
            }
            if (cachedSession is not null && (!ClientControlSessionStore.SameCapture(cachedSession, session) || cachedSession.Phase != session.Phase))
                return Retain("OriginalRecordsChanged");
            if (explicitClose is not null && !explicitClose.Matches(session))
                return SessionRefused(operationId, ErrorCode.UnauthorizedClient);
            if (session.ServiceSessionId == SessionId) return Retain("ServiceEpochChanged");
            if (session.Phase == ClientControlSessionPhase.Completed)
            {
                await VerifyCompletedLegacyEmptyPlanAsync(session, token);
                return explicitClose is null ? null : SessionCaptured(operationId, session.Handle, true);
            }
            if (session.Phase == ClientControlSessionPhase.Preparing) return Retain("SessionNotConfigured");
            var processState = probe.Observe(session.ProcessId, session.ProcessCreationFileTimeUtc);
            if (explicitClose is null && processState == ClientProcessState.Running) return null;
            if (!PriorProcessMatches(session, processState, explicitClose)) return Retain("ClientProcessUnknown");
            // A durable normal-exit decision only releases ownership; no hardware continuity is needed to replay it.
            if (restore && session.Phase is ClientControlSessionPhase.Active or ClientControlSessionPhase.Closing && journal is not null)
            {
                var retentionStore = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
                if (File.Exists(retentionStore.FilePath))
                {
                    if (new FileInfo(retentionStore.FilePath).Length > 2 * 1024 * 1024) return Retain("PreviousExitRecoveryInvalid");
                    var retention = await retentionStore.ReadAsync<ExitPlan>(token);
                    if (retention?.KeepChanges == true && retention.SessionId == session.Handle.SessionId)
                    {
                        clientSessions ??= store;
                        return await CompleteRetainedExitAsync(operationId, session, retention, token);
                    }
                }
            }
            if (explicitClose?.KeepChanges == true) return Retain("ExitRetentionRequiresCurrentSession");
            var boot = readSystemBootTime is null ? null : await readSystemBootTime(token);
            if (boot is null || boot <= DateTimeOffset.UnixEpoch || boot != session.BootStartedAtUtc)
                return Retain("BootEvidenceMissingOrChanged");
            var power = readSystemPowerContinuity is null ? null : await readSystemPowerContinuity(token);
            if (!PowerContinuityEvidence.Valid(power) || power != session.PowerContinuity)
                return Retain("PowerContinuityMissingOrChanged");
            if (!Fresh()) return Retain("ServiceEpochChanged");
            var hardware = await provider.DiagnoseAsync(token);
            if (!Fresh()) return Retain("ServiceEpochChanged");
            if (hardware.SupportState != DeviceSupportState.Ready || hardware.Identity != session.Identity ||
                await provider.ReadDeviceRecoveryKeyAsync(hardware.Identity, token) != session.Handle.DeviceKey)
                return Retain("DeviceIdentityMismatch");
            if (!Fresh()) return Retain("ServiceEpochChanged");
            if (journal is null || !File.Exists(Path.Combine(journal.RootDirectory, "control-execution.json")))
                return Retain("OriginalJournalMissing");
            var cached = await journal.ReadAsync(token);
            var saved = await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token);
            if (!SamePriorJournal(cached, saved)) return Retain("OriginalRecordsChanged");
            var recoveryStore = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
            bool hasExitPlan = File.Exists(recoveryStore.FilePath);
            ExitPlan? persistedPlan = null;
            if (restore && hasExitPlan)
            {
                if (new FileInfo(recoveryStore.FilePath).Length > 2 * 1024 * 1024)
                    return Retain("PreviousExitRecoveryInvalid");
                persistedPlan = await recoveryStore.ReadAsync<ExitPlan>(token);
            }
            bool idleFanRelease = restore && persistedPlan is { } idlePlan && ValidExitPlan(idlePlan) &&
                idlePlan.SessionId == session.Handle.SessionId && PriorExitTargetsMatch(session, idlePlan) &&
                await VerifyIdleFanReleaseAsync(session, idlePlan.OperationId, null, token, saved);
            if (!PriorScopeMatches(session, saved) && !idleFanRelease)
                return restore ? await CompleteLegacyEmptySessionAsync(operationId, session, saved, probe, explicitClose, token)
                    : Retain("OriginalScopeMismatch");
            if (restore) await journal.VerifyFanFirmwareOnlyHistoryAsync(session, saved, token);
            bool closingPlanUsesWindows = session.Phase == ClientControlSessionPhase.Closing &&
                persistedPlan is { } candidate && ValidExitPlan(candidate) && candidate.SessionId == session.Handle.SessionId &&
                candidate.WindowsScopeSessionId is not null;
            var windows = saved.Current!.WindowsScopeSessionId is not null || closingPlanUsesWindows
                ? await ReadWindowsScopesAsync(session, true, token) : null;
            var windowsRollback = restore ? await ReadPriorWindowsRollbackAsync(session, saved, windows, token) : null;
            var pendingFan = restore ? PriorPendingFan(session, saved) : null;
            bool pendingGpu = restore && PriorPendingGpuFields(session, saved).Length > 0;
            var pendingMode = restore ? PriorPendingMode(session, saved) : null;
            bool pendingCpu = restore && PriorPendingCpuFields(session, saved).Length > 0;
            bool pendingLighting = restore && PriorPendingLighting(session, saved) is not null;
            ExitPlan? archivedPlan = null;
            if (restore && session.Phase == ClientControlSessionPhase.Active && hasExitPlan)
            {
                archivedPlan = persistedPlan;
                if (archivedPlan is null || archivedPlan.SessionId == session.Handle.SessionId)
                    return Retain("PreviousExitRecoveryInvalid");
                await VerifyArchivedPriorExitPlanAsync(session, archivedPlan, token);
            }
            ExitPlan? resumePlan = restore && session.Phase == ClientControlSessionPhase.Closing && hasExitPlan
                ? await ReadPriorExitPlanAsync(session, saved, windows, token) : null;
            string? liveIssue;
            if (session.Phase == ClientControlSessionPhase.Closing && hasExitPlan) liveIssue = "PartialExitRecoveryVerificationRequired";
            else if (windowsRollback is not null) liveIssue = await VerifyPriorWindowsRollbackAsync(session, saved, windows, windowsRollback, token);
            else if (saved.Unresolved.Length == 0 || pendingFan is not null || pendingGpu || pendingMode is not null || pendingCpu || pendingLighting)
                liveIssue = await VerifyPriorLiveStateAsync(session, saved, windows, token,
                    nativeFanRecovery: restore && CanRestorePriorNativeFan(session, saved), nativeGpuRecovery: pendingGpu,
                    pendingMode: pendingMode, nativeCpuRecovery: pendingCpu, lightingRecovery: pendingLighting,
                    ignoreArchivedPlan: archivedPlan is not null);
            else liveIssue = null;
            if (resumePlan is not null) liveIssue = await VerifyPriorExitLiveStateAsync(session, resumePlan, windows, token, saved);
            if (readSystemBootTime is null || await readSystemBootTime(token) != boot)
                return Retain("BootEvidenceMissingOrChanged");
            if (readSystemPowerContinuity is null || await readSystemPowerContinuity(token) != power)
                return Retain("PowerContinuityMissingOrChanged");
            var finalHardware = await provider.DiagnoseAsync(token);
            if (finalHardware.SupportState != DeviceSupportState.Ready || finalHardware.Identity != session.Identity ||
                await provider.ReadDeviceRecoveryKeyAsync(finalHardware.Identity, token) != session.Handle.DeviceKey)
                return Retain("DeviceIdentityMismatch");
            var finalSession = await store.ReadAsync(token);
            if (finalSession is null || !ClientControlSessionStore.SameCapture(session, finalSession) || finalSession.Phase != session.Phase ||
                !File.Exists(Path.Combine(journal.RootDirectory, "control-execution.json")) ||
                !SamePriorJournal(saved, await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token)))
                return Retain("OriginalRecordsChanged");
            if (resumePlan is not null && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(resumePlan),
                JsonSerializer.SerializeToElement(await ReadPriorExitPlanAsync(session, saved, windows, token))))
                return Retain("OriginalRecordsChanged");
            if (!Fresh()) return Retain("ServiceEpochChanged");
            if (!PriorProcessMatches(session, probe.Observe(session.ProcessId, session.ProcessCreationFileTimeUtc), explicitClose))
                return Retain("ClientProcessUnknown");
            if (liveIssue is not null) return Retain(liveIssue);
            if (restore && (saved.Unresolved.Length == 0 || resumePlan is not null || windowsRollback is not null || pendingFan is not null || pendingGpu || pendingMode is not null || pendingCpu || pendingLighting))
            {
                if (archivedPlan is not null)
                {
                    var currentPlan = await recoveryStore.ReadAsync<ExitPlan>(token);
                    if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(archivedPlan), JsonSerializer.SerializeToElement(currentPlan)))
                        return Retain("OriginalRecordsChanged");
                    await VerifyArchivedPriorExitPlanAsync(session, archivedPlan, token);
                    // Keep A authoritative until the new B plan replaces it; never leave an empty publication window.
                }
                return await RunPriorSessionExitAsync(operationId, session, saved, windows, finalHardware, probe, token, resumePlan, windowsRollback, archivedPlan, explicitClose);
            }
            // Verified observations do not adopt an old epoch or authorize restoration.
            return Retain(saved.Unresolved.Length > 0 ? "UnresolvedOperationVerificationRequired"
                : "OriginalOwnershipVerifiedRecoveryRequired");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return Retain("OriginalRecordsInvalid"); }
    }

    private static bool HasPriorRecovery(ControlJournalSnapshot saved) => saved.Unresolved.Length > 0 ||
        saved.Current is { } current && (current.Fields is null || current.Fields.Count > 0 ||
            current.NativeModeOwnerClientSessionId is not null || current.WindowsScopeSessionId is not null) ||
        saved.FanEcRecovery is { } fan && !ControlExecutionJournal.FanCheckpointComplete(fan.Checkpoint) ||
        saved.FanFirmwareRecovery is { } firmware && !ControlExecutionJournal.FanFirmwareComplete(firmware);

    private static bool SamePriorJournal(ControlJournalSnapshot left, ControlJournalSnapshot right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));

    private static bool PriorScopeMatches(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        bool OwnerMatches(string? owner) => owner == session.OwnerSid || owner == AutomaticCatalogOwner(session.OwnerSid);
        bool StateMatches(ControlExecutionState state) => state.HardwareEpoch == session.Handle.HardwareEpoch &&
            state.Pages is not null && state.Fields is not null &&
            state.Fields.Values.All(field => field is not null && field.ClientSessionId == session.Handle.SessionId) &&
            (state.NativeModeOwnerClientSessionId is null || state.NativeModeOwnerClientSessionId == session.Handle.SessionId) &&
            (state.WindowsScopeSessionId is null || state.WindowsScopeSessionId == session.Handle.SessionId);
        if (saved.Identity != session.Identity || saved.Current is not { } current || !StateMatches(current) ||
            saved.RecoveryScope is not { } scope || scope.ServiceSessionId != session.ServiceSessionId ||
            scope.Fields.Count != current.Fields.Count || scope.Fields.Any(pair => !current.Fields.ContainsKey(pair.Key) ||
                !OwnerMatches(pair.Value.OwnerSid) || pair.Value.HardwareEpoch != session.Handle.HardwareEpoch) ||
            current.NativeModeOwnerClientSessionId is not null && scope.NativeModeEpoch != session.Handle.HardwareEpoch ||
            scope.FanOwnerClientSessionId is not null && scope.FanOwnerClientSessionId != session.Handle.SessionId ||
            saved.Unresolved.Any(item => item.Identity != session.Identity || !OwnerMatches(item.OwnerSid) || !StateMatches(item.State)))
            return false;
        var fanScope = new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch, session.Handle.SessionId, session.OwnerSid, session.Identity);
        return scope.FanOwnerClientSessionId is null ||
            (saved.FanEcRecovery is null || saved.FanEcRecovery.Scope == fanScope) &&
            (saved.FanFirmwareRecovery is null || saved.FanFirmwareRecovery.Scope == fanScope);
    }
}
