using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public static class HomeSessionMessages
{
    public const string ReadOnlyMode = "当前为安全只读模式";
}

public enum HomeSessionStatus
{
    Disconnected,
    Connecting,
    Connected,
    ReadOnly,
    RepairRequired
}

public sealed class HomeControlSession : IAsyncDisposable
{
    public static readonly TimeSpan ControlStateRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly ControlCenterClient client;
    private readonly ControlCenterClient modeClient;
    private readonly SemaphoreSlim modeCommandGate = new(1, 1);
    private long modeReadbackVersion;
    private long refreshSequence;
    private long appliedRefreshSequence;
    private readonly IInteractiveRadioController radioController;
    private readonly AppliedConfigurationRestore automaticRestore;
    private readonly UserPreferencesStore preferences = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim controlStateGate = new(1, 1);
    private readonly object gate = new();
    private Task? telemetryTask;
    private Task? controlStateTask;
    private Task? restoreTask;
    private volatile bool restoreScheduled;
    private CancellationTokenSource? restoreCancellation;
    private long manualCommandVersion;
    private long lastWakeTick;
    private HomeStateSnapshot? homeState;
    private HomeSessionStatus status = HomeSessionStatus.Disconnected;
    private HomeSessionStatus? lastNotifiedUnavailableStatus;
    private bool started;

    public HomeControlSession(
        ControlCenterClient? client = null,
        IInteractiveRadioController? radioController = null,
        AppliedConfigurationRestore? automaticRestore = null)
    {
        this.client = client ?? new ControlCenterClient();
        modeClient = this.client.CreatePeer();
        this.radioController = radioController ?? new WindowsInteractiveRadioController();
        this.automaticRestore = automaticRestore ?? new AppliedConfigurationRestore();
    }

    public event Action<HomeStateSnapshot>? StateChanged;
    public event Action<HardwareSnapshot>? TelemetryUpdated;
    public event Action<string>? NotificationRequested;
    public event Action<string>? RestoreWarning;
    public event Action? ConfigurationRestorationCompleted;

    public HomeSessionStatus Status
    {
        get { lock (gate) return status; }
        private set { lock (gate) status = value; }
    }

    public HomeStateSnapshot? State
    {
        get { lock (gate) return homeState; }
        private set { lock (gate) homeState = value; }
    }

    public bool ConfigurationRestorationSettled => restoreScheduled && Volatile.Read(ref restoreTask)?.IsCompleted == true;
    public bool IsServiceConnected => client.IsConnected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started) return;
        started = true;
        var initialVersion = Interlocked.Read(ref manualCommandVersion);
        await ConnectAndRefreshAsync(cancellationToken, notifyFailure: false);
        telemetryTask = ConsumeTelemetryAsync(lifetime.Token);
        controlStateTask = ConsumeControlStateAsync(lifetime.Token);
        ScheduleRestore(initialVersion);
    }

    public async Task<CommandResult> SendAdaptiveContextAsync(AdaptiveAutomationClientContext context, CancellationToken cancellationToken)
    {
        await controlStateGate.WaitAsync(cancellationToken);
        try
        {
            return await client.SendCommandAsync(new UpdateAdaptiveAutomationContextCommand(Guid.NewGuid(), context), cancellationToken);
        }
        finally { controlStateGate.Release(); }
    }

    public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(command, cancellationToken, CancellationToken.None);

    public async Task<CommandResult> PublishPresetsAsync(PagePresetEnvelope[] items, PageSelection[] selections,
        long expectedRevision, CancellationToken token)
    {
        await client.ConnectAsync(token);
        var operationId = Guid.NewGuid();
        if (client.NegotiatedProtocol.Minor < 1)
            return new(operationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.CapabilityUnavailable, operationId, false), false) { Mutation = HardwareMutation.NoMutation };
        return await client.SendCommandAsync(new PublishPresetCatalogCommand(operationId, expectedRevision, items)
            { Selections = selections }, token);
    }

    public async Task<CommandResult> ApplyControlRequestAsync(ControlModeId logicalMode, PerformanceMode nativeMode,
        PageSelection[] pages, ControlOrigin origin, CancellationToken token)
    {
        if (origin is ControlOrigin.Adaptive or ControlOrigin.StartupRestore or ControlOrigin.TurboBranch)
            throw new ArgumentOutOfRangeException(nameof(origin));
        long version = Interlocked.Increment(ref manualCommandVersion);
        await modeClient.ConnectAsync(token);
        Guid operationId = Guid.NewGuid();
        if (modeClient.NegotiatedProtocol.Minor < 1)
            return new(operationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.CapabilityUnavailable, operationId, false), false) { Mutation = HardwareMutation.NoMutation };
        var command = new ApplyControlRequestCommand(operationId, new(operationId, 0, origin, logicalMode, nativeMode, pages));
        var result = await modeClient.SendCommandAsync(command, token);
        if (version != Interlocked.Read(ref manualCommandVersion)) return result;
        HomeStateSnapshot? submitted;
        lock (gate)
        {
            if (version != Interlocked.Read(ref manualCommandVersion)) return result;
            submitted = homeState is null ? null : homeState with { Controls = homeState.Controls with
            {
                PerformanceMode = result.VerifiedPerformanceMode == nativeMode ? nativeMode : homeState.Controls.PerformanceMode,
                ExecutionState = result.ExecutionState ?? homeState.Controls.ExecutionState,
                ModeEvidence = result.ModeEvidence ?? homeState.Controls.ModeEvidence
            } };
            homeState = submitted;
            Interlocked.Increment(ref modeReadbackVersion);
        }
        if (submitted is not null) StateChanged?.Invoke(submitted);
        if (result.VerifiedPerformanceMode == nativeMode)
        {
            try { await automaticRestore.RecordAppliedAsync(new SetPerformanceModeCommand(operationId, nativeMode), result, CancellationToken.None,
                () => version == Interlocked.Read(ref manualCommandVersion)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            { if (version == Interlocked.Read(ref manualCommandVersion)) Notify("模式已确认，但本地恢复记录保存失败；请检查配置目录。"); }
        }
        if (result.Error is null || result.Mutation != HardwareMutation.NoMutation) await RefreshStateAsync(token);
        if (version == Interlocked.Read(ref manualCommandVersion) && result.Error is not null && result.Error.Code != ErrorCode.CancelledBeforeApply)
            Notify(ErrorText(result.Error));
        return result;
    }

    public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken,
        CancellationToken queueCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (SetAdaptiveAutomationConfigurationCommand or UpdateAdaptiveAutomationContextCommand or PublishPresetCatalogCommand))
            Interlocked.Increment(ref manualCommandVersion);
        if (command is SetQuickSettingCommand { Setting: QuickSettingKind.Wifi or QuickSettingKind.Bluetooth } radioCommand)
        {
            var radioResult = await ExecuteRadioAsync(radioCommand, cancellationToken);
            if (radioResult.Error is not null) Notify(ErrorText(radioResult.Error));
            return radioResult;
        }

        if (Status == HomeSessionStatus.Disconnected)
            await ConnectAndRefreshAsync(cancellationToken, notifyFailure: true);
        if (command is SetPerformanceModeCommand mode)
            return await ExecuteModeAsync(mode, cancellationToken);

        try
        {
            var commandClock = System.Diagnostics.Stopwatch.StartNew();
            using var queueCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, queueCancellationToken);
            await controlStateGate.WaitAsync(queueCancellation.Token);
            long queueMs = commandClock.ElapsedMilliseconds;
            try
            {
                queueCancellationToken.ThrowIfCancellationRequested();
                var result = await client.SendCommandAsync(command, cancellationToken);
                long replyMs = commandClock.ElapsedMilliseconds;
                try
                {
                    // A late UI cancellation must not lose an already-successful hardware application.
                    await automaticRestore.RecordAppliedAsync(command, result, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                {
                    RestoreWarning?.Invoke("本次应用结果已返回，但自动恢复快照保存失败；已保留原文件，请检查存储后重试");
                }
                if (result.Error is not null)
                {
                    Notify(ErrorText(result.Error));
                    if (result.Mutation != HardwareMutation.NoMutation) await RefreshStateAsync(cancellationToken);
                }
                else
                {
                    await RefreshStateAsync(cancellationToken);
                }

                if (command is SetPerformanceModeCommand or SetCpuTuningBatchCommand)
                    AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Mode command timing: {command.GetType().Name}; queueMs={queueMs}; serviceMs={replyMs - queueMs}; refreshMs={commandClock.ElapsedMilliseconds - replyMs}; state={result.State}\n");
                return result;
            }
            finally
            {
                controlStateGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || queueCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            bool disconnected = !client.IsConnected;
            if (disconnected) Status = HomeSessionStatus.Disconnected;
            Notify(disconnected ? "硬件服务未连接，已保留当前页面状态" : "本次操作未完成，服务连接仍有效；已保留当前页面状态");
            AppRuntimeLog.Write($"Command preparation/result processing failed: {error.GetType().Name}: {error.Message}\n");
            return new CommandResult(
                command.OperationId,
                CommandState.Rejected,
                null,
                RequiredUserAction.None,
                ServiceError.Create(disconnected ? ErrorCode.ServiceUnavailable : ErrorCode.ValidationFailed, command.OperationId, disconnected),
                false);
        }
    }

    private async Task<CommandResult> ExecuteModeAsync(SetPerformanceModeCommand command, CancellationToken token)
    {
        await modeCommandGate.WaitAsync(token);
        try
        {
            // A separate connection keeps a slow monitoring response out of the mode request's path.
            await modeClient.ConnectAsync(token);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await modeClient.SendCommandAsync(command, token);
            long replyMs = clock.ElapsedMilliseconds;
            if (result is { State: CommandState.Applied, Error: null } &&
                (result.VerifiedPerformanceMode == command.Mode ||
                 result.ModeEvidence is { VerificationLevel: "InterfaceAcknowledged" } modeEvidence &&
                 modeEvidence.RequestedMode == command.Mode && modeEvidence.ParsedMode == command.Mode))
            {
                HomeStateSnapshot? confirmed;
                lock (gate)
                {
                    confirmed = homeState is null ? null : homeState with
                    { Controls = homeState.Controls with { PerformanceMode = result.VerifiedPerformanceMode == command.Mode
                        ? command.Mode : homeState.Controls.PerformanceMode, ModeEvidence = result.ModeEvidence,
                        ExecutionState = result.ExecutionState ?? homeState.Controls.ExecutionState } };
                    homeState = confirmed;
                    Interlocked.Increment(ref modeReadbackVersion);
                }
                if (confirmed is not null) StateChanged?.Invoke(confirmed);
            }
            else
            {
                if (result.Error is not null) Notify(ErrorText(result.Error));
                await RefreshStateAsync(token); // Compatibility with services lacking mode readback.
            }
            try { await automaticRestore.RecordAppliedAsync(command, result, CancellationToken.None); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            { RestoreWarning?.Invoke("模式已返回，但自动恢复快照保存失败；请检查存储后重试"); }
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Mode command timing: {command.Mode}; serviceMs={replyMs}; totalMs={clock.ElapsedMilliseconds}; state={result.State}\n");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            Notify("模式切换未确认，请检查硬件服务连接");
            return new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.ServiceUnavailable, command.OperationId, true), false);
        }
        finally { modeCommandGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (ConfigurationRestorationSettled && Status == HomeSessionStatus.Connected &&
            State?.Controls.ExecutionState is { NativeModeApplyState: ApplyState.Confirmed } confirmed &&
            State.Controls.PerformanceMode is { } mode && confirmed.NativeMode == mode)
        {
            try { await automaticRestore.RecordObservedModeAsync(mode, CancellationToken.None, independentlyConfirmed: true); }
            catch (Exception error) { AppRuntimeLog.Write($"Shutdown mode persistence: {error.Message}\n"); }
        }
        lifetime.Cancel();
        if (restoreTask is not null)
        {
            try { await restoreTask; } catch { }
        }
        restoreCancellation?.Dispose();
        if (telemetryTask is not null)
        {
            try { await telemetryTask; } catch { }
        }
        if (controlStateTask is not null)
        {
            try { await controlStateTask; } catch { }
        }

        await client.DisposeAsync();
        await modeClient.DisposeAsync();
        modeCommandGate.Dispose();
        controlStateGate.Dispose();
        lifetime.Dispose();
    }

    public void SupersedeAutomaticRestore() => Interlocked.Increment(ref manualCommandVersion);

    public void HandlePowerEvent(int powerEvent)
    {
        if (!started || lifetime.IsCancellationRequested) return;
        if (powerEvent == 4) // PBT_APMSUSPEND
        {
            restoreCancellation?.Cancel();
            return;
        }
        // PBT_APMRESUMESUSPEND (7) is a second notification, not another restore.
        if (powerEvent != 18) return;
        long now = Environment.TickCount64;
        if (lastWakeTick != 0 && now - lastWakeTick < 2000) return;
        lastWakeTick = now;
        ScheduleRestore();
    }

    private void ScheduleRestore(long? requestedVersion = null)
    {
        restoreScheduled = false;
        restoreCancellation?.Cancel();
        var previousCancellation = restoreCancellation;
        var previousTask = restoreTask;
        restoreCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        restoreTask = RestoreWhenReadyAsync(previousTask, previousCancellation,
            requestedVersion ?? Interlocked.Read(ref manualCommandVersion), restoreCancellation.Token);
        restoreScheduled = true;
        _ = NotifyRestorationCompletedAsync(restoreTask);
    }

    private async Task NotifyRestorationCompletedAsync(Task scheduled)
    {
        await scheduled.ConfigureAwait(false);
        if (ReferenceEquals(Volatile.Read(ref restoreTask), scheduled)) ConfigurationRestorationCompleted?.Invoke();
    }

    private async Task RestoreWhenReadyAsync(Task? previous, CancellationTokenSource? previousCancellation,
        long version, CancellationToken cancellationToken)
    {
        try
        {
            if (previous is not null) { try { await previous; } catch { } }
            previousCancellation?.Dispose();
            if (!automaticRestore.HasSavedState) return;
            await Task.Delay(1000, cancellationToken);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (version != Interlocked.Read(ref manualCommandVersion)) return;
                if (Status != HomeSessionStatus.Connected)
                    await ConnectAndRefreshAsync(cancellationToken, notifyFailure: false);
                if (Status == HomeSessionStatus.Connected) break;
                await Task.Delay(1500, cancellationToken);
            }
            if (Status != HomeSessionStatus.Connected)
            {
                RestoreWarning?.Invoke("硬件服务尚未就绪，本次未自动恢复；保存的预设不受影响");
                return;
            }
            await controlStateGate.WaitAsync(cancellationToken);
            try
            {
                await RefreshStateAsync(cancellationToken);
                if (Status != HomeSessionStatus.Connected) return;
                var report = await automaticRestore.RestoreAsync(client.SendCommandAsync,
                    () => version == Interlocked.Read(ref manualCommandVersion),
                    State?.Controls.AdaptiveAutomation?.Enabled == true, cancellationToken,
                    command => State?.Controls.AdaptiveAutomation?.Enabled != true && command is SetPerformanceModeCommand && IsRestoreAllowed(command));
                if (report.Warning is not null) RestoreWarning?.Invoke(report.Warning);
                else if (report.Skipped > 0)
                    AppRuntimeLog.Write($"Legacy restore intentionally deferred: skipped={report.Skipped}; blockedPreviously={report.BlockedPreviously}; saved state retained");
                if (report.Applied > 0) await RefreshStateAsync(cancellationToken);
            }
            finally { controlStateGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            RestoreWarning?.Invoke("自动恢复未完成，已停止本次恢复；保存的预设不受影响");
        }
    }

    private bool IsRestoreAllowed(HardwareCommand command)
    {
        var snapshot = State;
        if (snapshot?.Capabilities.SupportState != DeviceSupportState.Ready) return false;
        if (snapshot.Telemetry?.AcPowerConnected != true &&
            command is SetCpuTuningCommand or SetGpuFrequencyLimitCommand or SetPerformanceModeCommand { Mode: PerformanceMode.Turbo })
            return false;
        string key = command switch
        {
            SetPerformanceModeCommand => "performanceMode",
            SetCpuTuningCommand => "cpuTuning",
            SetKeyboardLightingCommand or RestoreKeyboardLightingPreviewCommand => "keyboardLighting",
            SetLidLogoCommand or SetQuickSettingCommand { Setting: QuickSettingKind.LidLogo } => "quickSetting:lidLogo",
            _ => command.GetType().Name
        };
        return snapshot.Capabilities.IsAvailable(key);
    }

    private async Task ConnectAndRefreshAsync(CancellationToken cancellationToken, bool notifyFailure)
    {
        Status = HomeSessionStatus.Connecting;
        try
        {
            await controlStateGate.WaitAsync(cancellationToken);
            try
            {
                await client.ConnectAsync(cancellationToken);
                await RefreshStateAsync(cancellationToken);
            }
            finally
            {
                controlStateGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = HomeSessionStatus.Disconnected;
            throw;
        }
        catch (Exception error)
        {
            Status = HomeSessionStatus.Disconnected;
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Home session connect: {error}\n");
            if (notifyFailure) Notify("硬件服务未连接，请确认蛟龙服务正在运行");
        }
    }

    private async Task RefreshStateAsync(CancellationToken cancellationToken)
    {
        long sequence = Interlocked.Increment(ref refreshSequence);
        long readbackVersion = Interlocked.Read(ref modeReadbackVersion);
        var updated = await client.GetHomeStateAsync(cancellationToken);
        HomeStateSnapshot? previous;
        lock (gate)
        {
            if (sequence <= appliedRefreshSequence || readbackVersion != modeReadbackVersion) return;
            appliedRefreshSequence = sequence;
            previous = homeState;
            homeState = updated;
        }
        if (ConfigurationRestorationSettled && updated.Capabilities.SupportState == DeviceSupportState.Ready &&
            updated.Controls.ExecutionState is { NativeModeApplyState: ApplyState.Confirmed } confirmed &&
            updated.Controls.PerformanceMode is { } observedMode && confirmed.NativeMode == observedMode)
        {
            try { await automaticRestore.RecordObservedModeAsync(observedMode, cancellationToken,
                () => sequence == Interlocked.Read(ref appliedRefreshSequence) &&
                    readbackVersion == Interlocked.Read(ref modeReadbackVersion) && State?.Controls.PerformanceMode == observedMode,
                independentlyConfirmed: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { AppRuntimeLog.Write($"Observed mode persistence: {error.Message}\n"); }
        }
        lock (gate)
        {
            if (sequence != appliedRefreshSequence || readbackVersion != modeReadbackVersion) return;
            var previousStatus = Status;
            Status = updated.Capabilities.SupportState switch
            {
                DeviceSupportState.Ready => HomeSessionStatus.Connected,
                DeviceSupportState.RepairRequired => HomeSessionStatus.RepairRequired,
                _ => HomeSessionStatus.ReadOnly
            };
            if (previous is null || previousStatus != Status || !HomeStateEquivalence.HaveSameControls(previous, updated))
                StateChanged?.Invoke(updated);
            bool shouldNotify = false;
            if (Status is HomeSessionStatus.ReadOnly or HomeSessionStatus.RepairRequired)
            {
                shouldNotify = lastNotifiedUnavailableStatus != Status;
                lastNotifiedUnavailableStatus = Status;
            }
            else
            {
                lastNotifiedUnavailableStatus = null;
            }
            if (shouldNotify) Notify(HomeSessionMessages.ReadOnlyMode);
        }
    }

    private async Task ConsumeControlStateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ControlStateRefreshInterval, cancellationToken);
                if (Status == HomeSessionStatus.Disconnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    await ConnectAndRefreshAsync(cancellationToken, notifyFailure: false);
                    continue;
                }

                await controlStateGate.WaitAsync(cancellationToken);
                try { await RefreshStateAsync(cancellationToken); }
                finally { controlStateGate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Status = HomeSessionStatus.Disconnected;
                AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Home control refresh: {error}\n");
            }
        }
    }

    private async Task ConsumeTelemetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in client.SubscribeTelemetryAsync(cancellationToken))
                    TelemetryUpdated?.Invoke(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                Status = HomeSessionStatus.Disconnected;
                Notify("硬件遥测连接中断，正在重新连接");
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); } catch (OperationCanceledException) { return; }
                await ConnectAndRefreshAsync(cancellationToken, notifyFailure: false);
            }
        }
    }

    private void Notify(string text) => NotificationRequested?.Invoke(text);

    public void ReportOperationFailure(string message) => Notify(message);

    private async Task<CommandResult> ExecuteRadioAsync(
        SetQuickSettingCommand command,
        CancellationToken cancellationToken)
    {
        await controlStateGate.WaitAsync(cancellationToken);
        bool? previousEnabled = null;
        var writeAttempted = false;
        try
        {
            try
            {
                var before = await radioController.ReadAsync(cancellationToken);
                previousEnabled = command.Setting == QuickSettingKind.Wifi
                    ? before.WifiEnabled
                    : before.BluetoothEnabled;
                if (previousEnabled is not bool previous)
                    return RadioRejected(command, ErrorCode.CapabilityUnavailable);

                writeAttempted = true;
                var accepted = await radioController.SetAsync(command.Setting, command.Enabled, cancellationToken);

                var after = await radioController.ReadAsync(cancellationToken);
                var afterEnabled = command.Setting == QuickSettingKind.Wifi
                    ? after.WifiEnabled
                    : after.BluetoothEnabled;
                if (afterEnabled != command.Enabled)
                {
                    return await RestoreRadioAsync(
                        command,
                        previous,
                        accepted ? ErrorCode.ReadBackMismatch : ErrorCode.HardwareWriteFailed);
                }

                ApplyRadioState(after);
                return new CommandResult(command.OperationId, CommandState.Applied, State?.Telemetry,
                    RequiredUserAction.None, null, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (writeAttempted && previousEnabled is bool previous)
                    _ = await RestoreRadioAsync(command, previous, ErrorCode.HardwareWriteFailed);
                throw;
            }
            catch
            {
                return writeAttempted && previousEnabled is bool previous
                    ? await RestoreRadioAsync(command, previous, ErrorCode.HardwareWriteFailed)
                    : RadioRejected(command, ErrorCode.ServiceUnavailable);
            }
        }
        finally
        {
            controlStateGate.Release();
        }
    }

    private async Task<CommandResult> RestoreRadioAsync(
        SetQuickSettingCommand command,
        bool previousEnabled,
        ErrorCode cause)
    {
        try
        {
            using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _ = await radioController.SetAsync(command.Setting, previousEnabled, recoveryDeadline.Token);
            var restored = await radioController.ReadAsync(recoveryDeadline.Token);
            var restoredEnabled = command.Setting == QuickSettingKind.Wifi
                ? restored.WifiEnabled
                : restored.BluetoothEnabled;
            var verified = restoredEnabled == previousEnabled;
            ApplyRadioState(restored);
            return new CommandResult(
                command.OperationId,
                verified ? CommandState.RolledBack : CommandState.RecoveryRequired,
                State?.Telemetry,
                RequiredUserAction.None,
                ServiceError.Create(verified ? cause : ErrorCode.RollbackFailed, command.OperationId, false),
                false);
        }
        catch
        {
            ApplyRadioState(HomeRadioSnapshot.Unknown);
            return new CommandResult(
                command.OperationId,
                CommandState.RecoveryRequired,
                State?.Telemetry,
                RequiredUserAction.None,
                ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false),
                false);
        }
    }

    private void ApplyRadioState(HomeRadioSnapshot snapshot)
    {
        var current = State;
        if (current is null) return;

        var quickSettings = current.Controls.QuickSettings
            .Select(status => status.Setting switch
            {
                QuickSettingKind.Wifi => status with { Enabled = snapshot.WifiEnabled, Reason = snapshot.Reason },
                QuickSettingKind.Bluetooth => status with { Enabled = snapshot.BluetoothEnabled, Reason = snapshot.Reason },
                _ => status
            })
            .ToArray();
        var updated = current with { Controls = current.Controls with { QuickSettings = quickSettings } };
        State = updated;
        StateChanged?.Invoke(updated);
    }

    private static CommandResult RadioRejected(SetQuickSettingCommand command, ErrorCode code) =>
        new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
            ServiceError.Create(code, command.OperationId, false), false);

    private static string ErrorText(ErrorCode code) => code switch
    {
        ErrorCode.ReadOnlySafeMode or ErrorCode.BiosUnsupported or ErrorCode.DeviceMismatch or ErrorCode.CapabilityUnavailable => "当前设备未提供此功能所需的控制接口，本次设置未应用。其他功能仍可使用。",
        ErrorCode.DependencyMissing => "此功能所需的硬件驱动未就绪，本次设置未应用。可导出日志反馈。",
        ErrorCode.HardwareReadFailed => "无法读取此功能的当前设置，本次未写入。请重试或导出日志反馈。",
        ErrorCode.HardwareWriteFailed => "硬件未接受本次设置，请重试或导出日志反馈。",
        ErrorCode.ReadBackMismatch => "硬件读回值与目标不一致，已尝试恢复原设置。",
        ErrorCode.RollbackFailed => "硬件设置恢复未成功，请重启电脑并导出日志反馈。",
        ErrorCode.ValidationFailed => "此设置超出当前接口允许范围或未满足电源、温度条件，未应用。",
        ErrorCode.ConflictDetected => "检测到另一程序正在控制同一功能，本次未应用。请关闭冲突程序后重试。",
        ErrorCode.ServiceUnavailable => "硬件服务暂不可用",
        _ => "本次设置未完成，请重试或导出日志反馈。"
    };

    private static string ErrorText(ServiceError error)
    {
        if (error.Details.GetValueOrDefault("reason") is { } reason)
        {
            if (reason == "gpuPersistedRecoveryRequired")
                return "GPU 存在尚未验证恢复的设置，本次未写入。请导出诊断，完成恢复验证后再重试。";
            if (reason is "gpuRecoveryJournalUnavailable" or "gpuRecoveryAdmissionMissing" or "gpuRecoveryAdmissionMismatch" or
                "gpuRecoveryAdmissionRevoked" or "gpuRecoveryBaselineInvalid" or "gpuRecoveryIdentityChanged" or "gpuAdapterIdentityUnavailable")
                return "GPU 恢复记录或设备身份未能核实，本次未写入。请导出诊断反馈。";
            if (reason == "controlJournalUnreadable")
                return "控制记录暂不可读取，本次未写入。请导出诊断反馈。";
            if (reason == "gpuRecoveryPersistenceFailed" || reason.StartsWith("Control journal exceeds its size limit", StringComparison.Ordinal))
                return "GPU 写入前的恢复记录未能保存，本次未写入。请导出诊断反馈。";
        }
        if (error.Details.GetValueOrDefault("stage") != "Preflight" ||
            error.Details.GetValueOrDefault("cause") is not { } cause) return ErrorText(error.Code);
        var parts = cause.Split(':', 2);
        string field = parts.ElementAtOrDefault(1) switch
        {
            "temperatureLimitC" => "CPU 温度上限", "splWatts" => "CPU 持续功耗", "spptWatts" => "CPU 短时功耗",
            "acMaxFrequencyMhz" => "CPU 接电频率上限", "dcMaxFrequencyMhz" => "CPU 电池频率上限",
            "negativeCurveOptimizer" => "CPU 曲线优化", "pboScalar" => "PBO",
            "coreFrequencyLimitMhz" => "GPU 频率上限", "coreOffsetKhz" => "GPU 核心偏移",
            "memoryOffsetKhz" => "GPU 显存偏移", "vfOffsetsKhz" => "GPU 电压频率曲线",
            "strategy" => "风扇策略", "fixedRpm" => "风扇固定转速", _ => "预设字段"
        };
        return parts[0] switch
        {
            "conflictingAdvancedField" => $"此预设的{field}包含冲突目标，模式与预设均未应用。请在管理预设中检查该项。",
            "fieldNotWritable" or "nativeModeSideEffectUnverified" => $"此预设要求的{field}尚无可靠写入验证，模式与预设均未应用。可关闭对应页“随模式应用”后单独切换模式。",
            "fieldOutOfRange" => $"此预设的{field}超出当前接口范围，模式与预设均未应用。请检查该项。",
            "cpuExactBaselineUnreadable" or "curveOptimizerBaselineUnreadable" => $"无法读取{field}的可靠恢复原值，模式与预设均未应用。",
            _ => ErrorText(error.Code)
        };
    }
}
