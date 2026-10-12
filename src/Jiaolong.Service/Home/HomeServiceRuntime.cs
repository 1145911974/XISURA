using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed record HomeHardwareState(
    CapabilitySnapshot Capabilities,
    DeviceSupportState SupportState,
    string? Reason,
    HardwareIdentity Identity,
    HardwareSnapshot? Telemetry)
{
    public HomeControlState Controls { get; init; } = new(null, null, Array.Empty<QuickSettingStatus>());
}

public interface IHomeHardwareProvider
{
    // Notification path: update an in-memory flag only; never perform hardware I/O or wait for a lock.
    void SetBackgroundWorkPaused(bool paused) { }
    void SetBackgroundWorkState(PowerLifecycleState lifecycle) { }
    Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken);
    Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken);
    Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken);
    Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken);
    Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false) => ReadControlsAsync(token);
    Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken);
    Task<PerformanceModeEvidence?> ReadModeEvidenceAsync(CancellationToken token) => Task.FromResult<PerformanceModeEvidence?>(null);
    Task<CommandResult> RestoreCpuFieldAsync(Guid operationId, string field, CpuFieldRestorationValue baseline,
        CpuFieldRestorationValue expected, CancellationToken token) => Task.FromResult(new CommandResult(operationId,
            CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(ErrorCode.CapabilityUnavailable, operationId, false), false)
        { Mutation = HardwareMutation.NoMutation });
}

public sealed partial class HomeServiceRuntime(IHomeHardwareProvider provider, Jiaolong.Diagnostics.IDiagnosticEventWriter? diagnostics = null,
    ControlExecutionJournal? journal = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim fanGate = new(1, 1);
    private Guid? fanOwner;
    private HomeHardwareState? state;
    private bool repairAttempted;
    private string? latestModeOperation;
    private readonly object coordinatorGate = new();
    private ControlExecutionCoordinator? execution;
    private readonly AsyncLocal<WriteAttempt?> hardwareDispatched = new();
    private sealed class WriteAttempt(long lifecycleVersion)
    { public bool Started; public long LifecycleVersion { get; } = lifecycleVersion; }
    private readonly PowerLifecycleState powerLifecycle = new(provider);
    public bool IsSuspended => powerLifecycle.IsPaused;
    public Guid SessionId { get; } = Guid.NewGuid();
    internal static string AutomaticCatalogOwner(string? sid) => "adaptive:" + (sid ?? "service");

    public ControlExecutionCoordinator Execution
    {
        get
        {
            lock (coordinatorGate)
                return execution ??= new(ExecuteDirectAsync,
                    (command, token) => provider is AdaptiveAutomationHardwareProvider adaptive
                        ? adaptive.PrepareManualCommandAsync(command, token) : Task.CompletedTask,
                    async () => { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await GetHomeStateAsync(timeout.Token); },
                    command => provider is AdaptiveAutomationHardwareProvider adaptive && adaptive.IsAutomaticOperation(command.OperationId),
                    () => Volatile.Read(ref controlExecutionState));
        }
    }

    public HomeHardwareState State => Volatile.Read(ref state) ?? new(
        new CapabilitySnapshot(Array.Empty<CapabilityDescriptor>()),
        DeviceSupportState.Diagnosing,
        "notInitialized",
        new HardwareIdentity("未知", "未知", "未知", "未知"),
        null);
    public Task<ControlJournalSnapshot?> ReadControlJournalAsync(CancellationToken token) =>
        journal is null ? Task.FromResult<ControlJournalSnapshot?>(null) : ReadJournalCoreAsync(token);
    private async Task<ControlJournalSnapshot?> ReadJournalCoreAsync(CancellationToken token) => await journal!.ReadAsync(token);
    public async Task<object> ReadControlDiagnosticsAsync(CancellationToken token, string? requestingSid = null)
    {
        object? persisted;
        try
        {
            var saved = await ReadControlJournalAsync(token);
            persisted = saved is null ? null : new { saved.SchemaVersion, saved.Identity, saved.LastConfirmedMode,
                current = ExecutionSummary(saved.Current), unresolved = saved.Unresolved.Select(item => new
                    { item.OperationId, item.RecordedAtUtc, item.Identity, item.CommandKind, state = ExecutionSummary(item.State),
                      gpuRecovery = item.GpuRecovery is { } gpu ? new { gpu.CapturedAtUtc, gpu.SessionId, gpu.HardwareEpoch,
                          gpu.Adapter, gpu.VfOffsetsKhz, gpu.Pstate } : null,
                      belongsToRequester = requestingSid is not null && item.OwnerSid == requestingSid }) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { persisted = new { error = error.GetType().Name, reason = "ControlJournalUnreadableOriginalRetained" }; }
        return new { sessionId = SessionId, generation = Execution.Generation, hardwareEpoch = Execution.HardwareEpoch,
            executionState = ExecutionSummary(Volatile.Read(ref controlExecutionState)), journal = persisted,
            adaptive = provider is AdaptiveAutomationHardwareProvider adaptive ? await adaptive.ReadDiagnosticsAsync(token, requestingSid) : null };
    }
    private static object? ExecutionSummary(ControlExecutionState? value) => value is null ? null : new
        { value.Generation, value.HardwareEpoch, value.ConfigurationRevision, value.LogicalMode, value.NativeMode,
          value.NativeModeApplyState, value.Pages,
          fields = value.Fields.Select(pair => new { field = pair.Key, pair.Value.Origin, pair.Value.WriteState, pair.Value.OwnerOperationId }) };
    private Task PersistExecutionAsync(Guid operationId, bool pending, CancellationToken token, bool noMutation = false) =>
        journal is not null && noMutation
            ? journal.ResolveNoMutationAsync(operationId, Volatile.Read(ref controlExecutionState), token)
            : journal is not null && Volatile.Read(ref controlExecutionState) is { } executionState
            ? journal.SaveAsync(executionState, State.Identity, operationId, pending, token, Execution.CurrentAdmission?.Owner,
                Execution.CurrentAdmission?.CommandKind, noMutation) : Task.CompletedTask;

    public async Task<Guid[]> ReadUnresolvedOperationsAsync(CancellationToken token) =>
        journal is null ? [] : await journal.ReadUnresolvedOperationsAsync(token);

    public async Task<HomeHardwareState> InitializeAsync(CancellationToken cancellationToken)
    {
        var current = await GetStateAsync(cancellationToken);
        if (IsSuspended) return current;
        if (current.SupportState == DeviceSupportState.Ready) return current;
        return await ReinitializeAsync(cancellationToken);
    }

    public async Task<HomeHardwareState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state is not null) return state;
        if (IsSuspended) return State;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (IsSuspended) return State;
            return Volatile.Read(ref state) ?? await DiagnoseAndStoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsSuspended ? Task.FromResult(SuspendedTelemetry(State.Telemetry)) : provider.ReadTelemetryAsync(cancellationToken);
    }

    private static HardwareSnapshot SuspendedTelemetry(HardwareSnapshot? telemetry) => telemetry is not null
        ? telemetry with { HardwareState = "suspended-last-known" }
        : new(DateTimeOffset.MinValue, "suspended-unavailable", null, null, null, null);

    private HomeStateSnapshot SuspendedHomeState()
    {
        var current = State;
        return new(current.Capabilities, current.Telemetry is null ? null : SuspendedTelemetry(current.Telemetry), current.Controls);
    }

    public async Task<HomeStateSnapshot> GetHomeStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSuspended) return SuspendedHomeState();
        var current = await GetStateAsync(cancellationToken);
        if (IsSuspended) return SuspendedHomeState();
        var telemetry = await provider.ReadTelemetryAsync(cancellationToken);
        if (IsSuspended) return SuspendedHomeState();
        var controls = await provider.ReadControlsAsync(cancellationToken);
        if (IsSuspended) return SuspendedHomeState();
        await ObserveExternalModeAsync(controls, cancellationToken);
        if (IsSuspended) return SuspendedHomeState();
        if (Volatile.Read(ref controlExecutionState) is { } executionState) controls = controls with { ExecutionState = executionState };
        if (diagnostics is not null && controls.ModeEvidence is { } observed &&
            current.Controls.ModeEvidence is { } previous && observed.RawValue != previous.RawValue)
        {
            await WriteDiagnosticSafelyAsync(new Jiaolong.Diagnostics.DiagnosticEvent
            {
                TimestampUtc = observed.CapturedAtUtc, EventName = "mode.observed",
                OperationId = SessionId, CorrelationId = SessionId,
                Fields = new Dictionary<string, string>
                {
                    ["observationContext"] = "PollingUnattributed", ["rawMode"] = observed.RawResponseHex,
                    ["latestApplicationModeOperationId"] = Volatile.Read(ref latestModeOperation) ?? "none",
                    ["beforeRawMode"] = previous.RawResponseHex, ["modeSource"] = observed.Source,
                    ["parsedMode"] = observed.ParsedMode?.ToString() ?? "unreadable",
                    ["verificationLevel"] = observed.VerificationLevel,
                    ["acFrequencyLimitMhz"] = controls.CpuTuning?.AcMaxFrequencyMhz?.ToString() ?? "unreadable",
                    ["dcFrequencyLimitMhz"] = controls.CpuTuning?.DcMaxFrequencyMhz?.ToString() ?? "unreadable",
                    ["oemSplWatts"] = controls.CpuTuning?.SplWatts?.ToString() ?? "unreadable",
                    ["oemSpptWatts"] = controls.CpuTuning?.SpptWatts?.ToString() ?? "unreadable",
                    ["beforeAcFrequencyLimitMhz"] = current.Controls.CpuTuning?.AcMaxFrequencyMhz?.ToString() ?? "unreadable",
                    ["beforeDcFrequencyLimitMhz"] = current.Controls.CpuTuning?.DcMaxFrequencyMhz?.ToString() ?? "unreadable",
                    ["beforeOemSplWatts"] = current.Controls.CpuTuning?.SplWatts?.ToString() ?? "unreadable",
                    ["beforeOemSpptWatts"] = current.Controls.CpuTuning?.SpptWatts?.ToString() ?? "unreadable",
                    ["beforeObservationUtc"] = previous.CapturedAtUtc.ToString("O")
                }
            }, cancellationToken);
        }
        var refreshed = current with { Telemetry = telemetry, Controls = controls };
        Volatile.Write(ref state, refreshed);
        return new(refreshed.Capabilities, refreshed.Telemetry, refreshed.Controls);
    }

    private async ValueTask WriteDiagnosticSafelyAsync(Jiaolong.Diagnostics.DiagnosticEvent value, CancellationToken token)
    {
        if (diagnostics is null) return;
        try { await diagnostics.WriteAsync(value, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken, Guid connectionId = default, string? userSid = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Execution.FindOperation(command, userSid ?? "service") is { } replay) return await replay;
        if (ControlExecutionCoordinator.IsManaged(command) && journal is not null)
        {
            try
            {
                var saved = await journal.ReadAsync(cancellationToken);
                if (saved.Unresolved.Any(item => item.OperationId == command.OperationId))
                    return Rejected(command, ErrorCode.IdempotencyConflict) with { State = CommandState.RecoveryRequired, Mutation = HardwareMutation.NoMutation };
                if (ControlExecutionJournal.RequiresGpuRecoveryCheck(command) &&
                    saved.Unresolved.Any(ControlExecutionJournal.HasPendingGpuRecovery))
                    return Rejected(command, ErrorCode.RollbackFailed) with
                    {
                        State = CommandState.RecoveryRequired, Mutation = HardwareMutation.NoMutation,
                        Error = ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false) with
                        { Details = new() { ["reason"] = "gpuPersistedRecoveryRequired", ["writeAttempted"] = bool.FalseString } }
                    };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return Rejected(command, ErrorCode.InternalFailure) with
                { Mutation = HardwareMutation.NoMutation, Error = ServiceError.Create(ErrorCode.InternalFailure, command.OperationId, false) with
                    { Details = new() { ["reason"] = "controlJournalUnreadable", ["errorType"] = error.GetType().Name,
                        ["writeAttempted"] = bool.FalseString } } };
            }
        }
        var timing = System.Diagnostics.Stopwatch.StartNew();
        var result = await Execution.ExecuteAsync(command, cancellationToken, connectionId, userSid ?? "service");
        if (command is ApplyControlRequestCommand or ApplyTurboBranchCommand or ReleaseTurboBranchCommand or PublishPresetCatalogCommand &&
            result.Error is not null && result.Mutation == HardwareMutation.NoMutation)
            await RecordModeTimingAsync(command, result, timing.ElapsedMilliseconds, 0);
        return result;
    }

    private async Task<CommandResult> ExecuteDirectAsync(HardwareCommand command, CancellationToken cancellationToken, Guid connectionId)
    {
        if (IsSuspended) return Rejected(command, ErrorCode.CancelledBeforeApply);
        hardwareDispatched.Value = new(powerLifecycle.Version);
        if (!ControlExecutionCoordinator.IsManaged(command))
        {
            try { return await ExecuteHardwareDirectAsync(command, cancellationToken, connectionId); }
            catch (OperationCanceledException) when (hardwareDispatched.Value?.Started != true)
            { return Rejected(command, ErrorCode.CancelledBeforeApply); }
        }
        CommandResult result;
        var priorOwnership = new Dictionary<string, OwnedField>(ownedFields, StringComparer.OrdinalIgnoreCase);
        var priorState = Volatile.Read(ref controlExecutionState);
        try
        {
            result = command switch
            {
                ApplyTurboBranchCommand branch => await ApplyTurboBranchAsync(branch, cancellationToken, connectionId),
                ReleaseTurboBranchCommand release => await ReleaseTurboBranchAsync(release, cancellationToken, connectionId),
                ApplyControlRequestCommand request => await ExecuteControlRequestAsync(request, cancellationToken, connectionId),
                _ => await ExecuteTrackedCommandAsync(command, cancellationToken, connectionId)
            };
            if (ControlExecutionCoordinator.IsManaged(command))
                await PersistExecutionAsync(command.OperationId, false, CancellationToken.None, result.Mutation == HardwareMutation.NoMutation);
        }
        catch (OperationCanceledException interruption) when (hardwareDispatched.Value?.Started != true)
        {
            ownedFields.Clear();
            foreach (var item in priorOwnership) ownedFields.Add(item.Key, item.Value);
            Volatile.Write(ref controlExecutionState, priorState);
            try { await PersistExecutionAsync(command.OperationId, false, CancellationToken.None, noMutation: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return Rejected(command, ErrorCode.InternalFailure) with { ExecutionState = priorState };
            }
            var rejected = Rejected(command, ErrorCode.CancelledBeforeApply) with { ExecutionState = priorState };
            if (interruption.Message == "hardwareLifecycleChangedBeforeDispatch")
            { rejected.Error!.Details["stage"] = "LifecycleGuard"; rejected.Error.Details["cause"] = interruption.Message; }
            return rejected;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // A persisted pending marker is retained if post-write persistence fails.
            bool pending = hardwareDispatched.Value?.Started == true;
            if (!pending)
            {
                ownedFields.Clear();
                foreach (var item in priorOwnership) ownedFields.Add(item.Key, item.Value);
                Volatile.Write(ref controlExecutionState, priorState);
            }
            if (pending && Volatile.Read(ref controlExecutionState) is { } current)
            {
                foreach (var key in ownedFields.Where(pair => pair.Value.OperationId == command.OperationId).Select(pair => pair.Key).ToArray())
                    ownedFields[key] = ownedFields[key] with { State = ApplyState.Unknown };
                Volatile.Write(ref controlExecutionState, current with
                { NativeModeApplyState = ApplyState.Unknown,
                  Fields = current.Fields.ToDictionary(pair => pair.Key, pair => pair.Value.OwnerOperationId == command.OperationId
                    ? pair.Value with { WriteState = ApplyState.Unknown } : pair.Value) });
            }
            var rejected = Rejected(command, ErrorCode.InternalFailure) with
            { State = pending ? CommandState.RecoveryRequired : CommandState.Rejected,
              Mutation = pending ? HardwareMutation.Unknown : HardwareMutation.NoMutation,
              ExecutionState = Volatile.Read(ref controlExecutionState) };
            if (error.Message == "hardwareLifecycleChangedAfterDispatch")
            { rejected.Error!.Details["stage"] = "LifecycleGuard"; rejected.Error.Details["cause"] = error.Message; }
            return rejected;
        }
        return result;
    }

    private async Task<CommandResult> ExecuteHardwareDirectAsync(HardwareCommand command, CancellationToken cancellationToken, Guid connectionId)
    {
        EnsureControlHardwareAccess();
        if (command is not (SetFanControlCommand or ReleaseFanControlCommand or SetStrongCoolingCommand))
            return await ExecuteCoreAsync(command, cancellationToken);

        await fanGate.WaitAsync(cancellationToken);
        var previousOwner = fanOwner;
        try
        {
            if (command is SetFanControlCommand { PreserveStrongCooling: true } or ReleaseFanControlCommand { PreserveStrongCooling: true } &&
                (await provider.ReadControlsAsync(cancellationToken)).StrongCooling != false)
                return Rejected(command, ErrorCode.CommandInProgress);
            // Claim before dispatch: cancellation can arrive after a partial EC write.
            EnsureControlHardwareAccess();
            fanOwner = connectionId;
            var result = await ExecuteCoreAsync(command, cancellationToken);
            if (result.State == CommandState.Applied)
            {
                if (command is ReleaseFanControlCommand or SetStrongCoolingCommand { Enabled: false }) fanOwner = null;
            }
            else if (result.State != CommandState.RecoveryRequired) fanOwner = previousOwner;
            return result;
        }
        finally { fanGate.Release(); }
    }

    public Task<CommandResult?> ReleaseClientFanAsync(Guid connectionId)
    {
        var command = new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.SessionEnded);
        return Execution.WithHardwareGateAsync(() => ReleaseClientFanCoreAsync(connectionId, command), lifecycleCommand: command);
    }

    private async Task<CommandResult?> ReleaseClientFanCoreAsync(Guid connectionId, ReleaseFanControlCommand command)
    {
        if (connectionId == Guid.Empty) return null;
        await fanGate.WaitAsync();
        try
        {
            if (fanOwner != connectionId) return null;
            // Bypass capability refresh: losing telemetry must not prevent EC cleanup.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await ReleaseLifecycleFanCoreAsync(command, deadline.Token);
            if (result.State == CommandState.Applied) fanOwner = null;
            return result;
        }
        finally { fanGate.Release(); }
    }

    private async Task<CommandResult> ExecuteCoreAsync(HardwareCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureControlHardwareAccess();
        // Retain wire compatibility while rejecting the removed operation before any driver access.
        if (command is SetGpuVoltageBoostCommand) return Rejected(command, ErrorCode.CapabilityUnavailable);
        var current = await GetStateAsync(cancellationToken);
        if (!CanExecute(command, current))
        {
            if (!repairAttempted) { EnsureControlHardwareAccess(); current = await ReinitializeAsync(cancellationToken); }
            if (!CanExecute(command, current)) return Rejected(command, ErrorFor(current));
        }

        var commandClock = System.Diagnostics.Stopwatch.StartNew();
        if (Execution.CurrentAdmission is { } admitted && ControlExecutionCoordinator.IsManaged(command) && !Execution.IsCurrent(admitted.Generation))
            return Rejected(command, ErrorCode.CancelledBeforeApply);
        if (command is SetPerformanceModeCommand) Volatile.Write(ref latestModeOperation, command.OperationId.ToString("N"));
        var result = await ExecuteProviderAsync(command, cancellationToken);
        long applyMs = commandClock.ElapsedMilliseconds;
        if (result.Error is null || !result.Error.IsRetryable || result.Mutation != HardwareMutation.NoMutation)
        {
            await RefreshCommandStateAsync(command, result, cancellationToken);
            await RecordModeTimingAsync(command, result, applyMs, commandClock.ElapsedMilliseconds - applyMs);
            if (command is SetPerformanceModeCommand && result.Mutation != HardwareMutation.NoMutation)
                _ = SampleModeTimelineAsync(command.OperationId);
            return result;
        }

        EnsureControlHardwareAccess();
        current = await ReinitializeAsync(cancellationToken);
        if (Execution.CurrentAdmission is { } retryAdmission && ControlExecutionCoordinator.IsManaged(command) && !Execution.IsCurrent(retryAdmission.Generation))
            return Rejected(command, ErrorCode.CancelledBeforeApply);
        if (!CanExecute(command, current)) return Rejected(command, ErrorFor(current));
        result = await ExecuteProviderAsync(command, cancellationToken);
        await RefreshCommandStateAsync(command, result, cancellationToken);
        return result;
    }
    internal long PreparePowerTransition(bool resume) => resume ? powerLifecycle.Version : powerLifecycle.Pause();

    public Task HandleLifecycleAsync(bool resume, CancellationToken token, long? notificationVersion = null)
    {
        long version = notificationVersion ?? powerLifecycle.Pause();
        if (!powerLifecycle.Hold(version)) return Task.CompletedTask;
        Execution.InvalidateHardware();
        var command = new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.SystemSuspend);
        return Execution.WithHardwareGateAsync(async () =>
        {
            if (powerLifecycle.Version != version) return false;
            if (State.Controls.ActiveFanControlPlan is not null || State.Controls.StrongCooling == true)
                await ReleaseLifecycleFanCoreAsync(command, token);
            if (resume)
            {
                if (powerLifecycle.Version != version) return false;
                HomeHardwareState refreshed;
                try
                {
                    using var recoveryReads = powerLifecycle.AllowRecoveryReads(version);
                    refreshed = await ReinitializeAsync(token);
                }
                catch (OperationCanceledException) when (powerLifecycle.Version != version)
                {
                    return false;
                }
                if (refreshed.SupportState is DeviceSupportState.RepairRequired or DeviceSupportState.Diagnosing)
                    throw new IOException("resumeHardwareDiscoveryFailed:" + (refreshed.Reason ?? "unknown"));
                powerLifecycle.Resume(version);
            }
            return true;
        }, token, command);
    }

    private async Task<CommandResult> ReleaseLifecycleFanCoreAsync(ReleaseFanControlCommand command, CancellationToken token)
    {
        if (journal is not null && (await journal.ReadAsync(token)).Unresolved.Any(item =>
                item.CommandKind == nameof(ReleaseFanControlCommand)))
            return Rejected(command, ErrorCode.IdempotencyConflict) with { State = CommandState.RecoveryRequired, Mutation = HardwareMutation.NoMutation };
        var prior = Volatile.Read(ref controlExecutionState);
        SetPendingLegacy(command, State.Controls.PerformanceMode);
        try { await PersistExecutionAsync(command.OperationId, true, token); }
        catch
        {
            Volatile.Write(ref controlExecutionState, prior);
            throw;
        }
        CommandResult result;
        try { result = await provider.ExecuteAsync(command, token); }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
        { result = Rejected(command, ErrorCode.HardwareWriteFailed) with { State = CommandState.RecoveryRequired, Mutation = HardwareMutation.Unknown }; }
        if (result.Mutation == HardwareMutation.NoMutation) Volatile.Write(ref controlExecutionState, prior);
        else
        {
            if (result.State == CommandState.Applied && result.Error is null)
            {
                fanOwner = null;
                foreach (string field in ownedFields.Keys.Where(key => key.StartsWith("Fan.", StringComparison.OrdinalIgnoreCase)).ToArray()) ownedFields.Remove(field);
            }
            Volatile.Write(ref controlExecutionState, controlExecutionState! with { Fields = SnapshotFields(),
                Pages = [new(ControlPageId.Fan, null, 0, result.Mutation == HardwareMutation.Unknown ? ApplyState.Unknown :
                    result.Error is null && result.State == CommandState.Applied ? ApplyState.SubmittedUnverified : ApplyState.Failed,
                    [], [], [], result.Error?.Code.ToString())] });
        }
        await PersistExecutionAsync(command.OperationId, false, CancellationToken.None, result.Mutation == HardwareMutation.NoMutation);
        return result;
    }

    private void EnsureControlHardwareAccess()
    {
        var attempt = hardwareDispatched.Value;
        if (!IsSuspended && (attempt is null || attempt.LifecycleVersion == powerLifecycle.Version)) return;
        if (attempt?.Started == true) throw new IOException("hardwareLifecycleChangedAfterDispatch");
        throw new OperationCanceledException("hardwareLifecycleChangedBeforeDispatch");
    }

    private async Task<HomeControlState> ReadControlBaselineForExecutionAsync(CancellationToken token, bool advanced = false)
    {
        EnsureControlHardwareAccess();
        var controls = await provider.ReadControlBaselineAsync(token, advanced);
        EnsureControlHardwareAccess();
        return controls;
    }

    private async Task<CommandResult> TrackHardwareDispatchAsync(Func<Task<CommandResult>> dispatch)
    {
        EnsureControlHardwareAccess();
        var attempt = hardwareDispatched.Value;
        bool previousStarted = attempt?.Started == true;
        if (attempt is not null) attempt.Started = true;
        var result = await dispatch();
        if (attempt is not null && result.Mutation == HardwareMutation.NoMutation) attempt.Started = previousStarted;
        return result;
    }

    private Task<CommandResult> ExecuteProviderAsync(HardwareCommand command, CancellationToken token) =>
        TrackHardwareDispatchAsync(async () =>
        {
            var admission = Execution.CurrentAdmission;
            using var gpuScope = journal is not null && admission is not null &&
                command is SetGpuVfCurveCommand or SetGpuCoreOffsetCommand or SetGpuMemoryOffsetCommand
                ? journal.BindGpuOperation(new(admission.OperationId, SessionId, Execution.HardwareEpoch, admission.Owner)) : null;
            return provider is AdaptiveAutomationHardwareProvider adaptive
                ? await adaptive.ExecuteCoordinatedAsync(command, token, admission?.Owner) : await provider.ExecuteAsync(command, token);
        });

    private async Task RecordModeTimingAsync(HardwareCommand command, CommandResult result, long applyMs, long refreshMs)
    {
        if (diagnostics is null) return;
        try
        {
            await diagnostics.WriteAsync(new Jiaolong.Diagnostics.DiagnosticEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                EventName = result.State == CommandState.Applied && result.Error is null
                    ? Jiaolong.Diagnostics.DiagnosticEventNames.CommandApplied : Jiaolong.Diagnostics.DiagnosticEventNames.CommandRejected,
                OperationId = command.OperationId,
                Operation = command.GetType().Name,
                CorrelationId = result.Error?.CorrelationId is { } correlation && correlation != Guid.Empty ? correlation : command.OperationId,
                ErrorCode = result.Error?.Code.ToWireValue(),
                ServiceVersion = typeof(HomeServiceRuntime).Assembly.GetName().Version?.ToString(),
                DurationMs = (int)Math.Min(int.MaxValue, applyMs + refreshMs),
                Fields = new Dictionary<string, string>(result.Error?.Details ?? [])
                {
                    ["applyMs"] = applyMs.ToString(), ["refreshMs"] = refreshMs.ToString(),
                    ["state"] = result.State.ToString(), ["mutation"] = result.Mutation.ToString(),
                    ["sessionId"] = SessionId.ToString("N"),
                    ["verificationLevel"] = result.ModeEvidence?.VerificationLevel ?? "NotAttempted",
                    ["rawMode"] = result.ModeEvidence?.RawResponseHex ?? "NotAttempted",
                    ["beforeRawMode"] = result.ModeEvidence?.BeforeRawResponseHex ?? "NotAttempted",
                    ["responseOffset"] = result.ModeEvidence?.ResponseOffset.ToString() ?? "NotAttempted",
                    ["modeSource"] = result.ModeEvidence?.Source ?? "NotAttempted",
                    ["requestedMode"] = (command as SetPerformanceModeCommand)?.Mode.ToString() ??
                        (command as SetCpuTuningBatchCommand)?.NativeMode?.ToString() ?? "none",
                    ["planCount"] = (command as SetCpuTuningBatchCommand)?.Plans.Length.ToString() ?? "1"
                }
            }, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private async Task RefreshCommandStateAsync(HardwareCommand command, CommandResult result, CancellationToken cancellationToken)
    {
        // Context heartbeats do not change hardware; probing drivers here stalls every scheduling tick.
        if (command is SetAdaptiveAutomationConfigurationCommand or UpdateAdaptiveAutomationContextCommand) return;
        if (result.Mutation == HardwareMutation.NoMutation) return;
        if (IsSuspended) return;
        if (command is SetPerformanceModeCommand mode && result is { State: CommandState.Applied, Error: null } &&
            (result.VerifiedPerformanceMode == mode.Mode ||
             result.ModeEvidence is { VerificationLevel: "InterfaceAcknowledged" } evidence &&
             evidence.RequestedMode == mode.Mode && evidence.ParsedMode == mode.Mode))
        {
            var current = await GetStateAsync(cancellationToken);
            Volatile.Write(ref state, current with
            { Controls = current.Controls with { PerformanceMode = mode.Mode, ModeEvidence = result.ModeEvidence } });
            return;
        }
        try
        {
            // Changed/unknown writes require current reads, never an identity/dependency re-probe.
            if (command is SetCpuTuningCommand or SetCpuTuningBatchCommand && !HasAdvanced(command))
            {
                var current = await GetStateAsync(cancellationToken);
                var controls = await ReadControlBaselineForExecutionAsync(cancellationToken);
                Volatile.Write(ref state, current with { Controls = controls });
                return;
            }
            await GetHomeStateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Keep the original transaction result; failed refresh is diagnostic evidence, not a retry.
            await RecordModeTimingAsync(command, result with
            {
                Error = result.Error ?? ServiceError.Create(ErrorCode.HardwareReadFailed, command.OperationId, false)
                    with { Details = new() { ["stage"] = "PostWriteRefresh", ["cause"] = error.GetType().Name } }
            }, 0, 0);
        }
    }

    private async Task SampleModeTimelineAsync(Guid operationId)
    {
        if (diagnostics is null) return;
        long hardwareEpoch = Execution.HardwareEpoch;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var atMs in new[] { 250, 1000, 3000 })
            {
                await Task.Delay((int)Math.Max(0, atMs - clock.ElapsedMilliseconds));
                if (IsSuspended || Execution.HardwareEpoch != hardwareEpoch) return;
                var evidence = await provider.ReadModeEvidenceAsync(CancellationToken.None);
                if (evidence is null) continue;
                await diagnostics.WriteAsync(new Jiaolong.Diagnostics.DiagnosticEvent
                {
                    TimestampUtc = evidence.CapturedAtUtc, EventName = "mode.observed",
                    OperationId = operationId, CorrelationId = operationId,
                    Fields = new Dictionary<string, string>(evidence.EffectiveState)
                    {
                        ["sampleTargetMs"] = atMs.ToString(), ["rawMode"] = evidence.RawResponseHex,
                        ["actualElapsedMs"] = clock.ElapsedMilliseconds.ToString(),
                        ["rawValue"] = evidence.RawValue?.ToString() ?? "unreadable",
                        ["parsedMode"] = evidence.ParsedMode?.ToString() ?? "unreadable",
                        ["modeSource"] = evidence.Source, ["responseOffset"] = evidence.ResponseOffset.ToString(),
                        ["verificationLevel"] = evidence.VerificationLevel
                    }
                }, CancellationToken.None);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    public async Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken)
    {
        Execution.InvalidateHardware();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!powerLifecycle.IsReadAllowed) throw new OperationCanceledException("hardwareAccessPaused");
            var current = Volatile.Read(ref state) is null
                ? await provider.DiagnoseAsync(cancellationToken)
                : await provider.ReinitializeAsync(cancellationToken);
            if (!powerLifecycle.IsReadAllowed) throw new OperationCanceledException("hardwareAccessPaused");
            repairAttempted = true;
            current = StampCapabilities(current);
            Volatile.Write(ref state, current);
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool CanExecute(HardwareCommand command, HomeHardwareState current)
    {
        var key = HomeCapabilityCatalog.RequiredCapability(command);
        return current.Capabilities.Items.Any(item =>
            string.Equals(item.Key, key, StringComparison.Ordinal) &&
            item.State == CapabilityState.Available);
    }

    private static ErrorCode ErrorFor(HomeHardwareState current) => current.Reason switch
    {
        "deviceMismatch" => ErrorCode.DeviceMismatch,
        "biosUnsupported" => ErrorCode.BiosUnsupported,
        "dependencyMissing" => ErrorCode.DependencyMissing,
        "conflictDetected" => ErrorCode.ConflictDetected,
        "readOnlySafeMode" => ErrorCode.ReadOnlySafeMode,
        "verifiedWritableEvidenceMissing" => ErrorCode.ReadOnlySafeMode,
        _ => ErrorCode.CapabilityUnavailable
    };

    private static CommandResult Rejected(HardwareCommand command, ErrorCode code) =>
        new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, command.OperationId, false), false)
        { Mutation = HardwareMutation.NoMutation };

    private async Task<HomeHardwareState> DiagnoseAndStoreAsync(CancellationToken cancellationToken)
    {
        var diagnosed = StampCapabilities(await provider.DiagnoseAsync(cancellationToken));
        Volatile.Write(ref state, diagnosed);
        return diagnosed;
    }

    private HomeHardwareState StampCapabilities(HomeHardwareState value) => value with
    { Capabilities = value.Capabilities with { Items = value.Capabilities.Items.Concat([new CapabilityDescriptor("controlPlan:v1.1", CapabilityState.Available, null)]).DistinctBy(item => item.Key).ToArray(),
        Fields = value.Capabilities.Fields.Select(field => field with { HardwareEpoch = Execution.HardwareEpoch }).ToArray() } };

}
