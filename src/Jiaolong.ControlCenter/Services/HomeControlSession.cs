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
    public static readonly TimeSpan ControlStateRefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly ControlCenterClient client;
    private readonly ControlCenterClient modeClient;
    private readonly SemaphoreSlim modeCommandGate = new(1, 1);
    private long modeReadbackVersion;
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

    public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken,
        CancellationToken queueCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (SetAdaptiveAutomationConfigurationCommand or UpdateAdaptiveAutomationContextCommand))
            Interlocked.Increment(ref manualCommandVersion);
        if (command is SetQuickSettingCommand { Setting: QuickSettingKind.Wifi or QuickSettingKind.Bluetooth } radioCommand)
            return await ExecuteRadioAsync(radioCommand, cancellationToken);

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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RestoreWarning?.Invoke("本次应用结果已返回，但自动恢复快照保存失败；已保留原文件，请检查存储后重试");
                }
                if (result.Error is not null)
                {
                    Notify(ErrorText(result.Error.Code));
                    await RefreshStateAsync(cancellationToken);
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
        catch
        {
            Status = HomeSessionStatus.Disconnected;
            Notify("硬件服务未连接，已保留当前页面状态");
            return new CommandResult(
                command.OperationId,
                CommandState.Rejected,
                null,
                RequiredUserAction.None,
                ServiceError.Create(ErrorCode.ServiceUnavailable, command.OperationId, true),
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
            if (result is { State: CommandState.Applied, Error: null } && result.VerifiedPerformanceMode == command.Mode)
            {
                HomeStateSnapshot? confirmed;
                lock (gate)
                {
                    confirmed = homeState is null ? null : homeState with
                    { Controls = homeState.Controls with { PerformanceMode = command.Mode } };
                    homeState = confirmed;
                    Interlocked.Increment(ref modeReadbackVersion);
                }
                if (confirmed is not null) StateChanged?.Invoke(confirmed);
            }
            else await RefreshStateAsync(token); // Compatibility with services lacking mode readback.
            try { await automaticRestore.RecordAppliedAsync(command, result, CancellationToken.None); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
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
            State?.Controls.PerformanceMode is { } mode)
        {
            try { await automaticRestore.RecordObservedModeAsync(mode, CancellationToken.None); }
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
                    preferences.Load().AdaptiveModeEnabled, cancellationToken, IsRestoreAllowed);
                if (report.Warning is not null) RestoreWarning?.Invoke(report.Warning);
                else if (report.Skipped > 0 && !report.BlockedPreviously && !preferences.Load().AdaptiveModeEnabled)
                    RestoreWarning?.Invoke("部分参数因当前供电或硬件能力暂未恢复；原配置已保留");
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
        return snapshot.Capabilities.Items.Any(item => item.Key == key && item.State == CapabilityState.Available);
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
        long readbackVersion = Interlocked.Read(ref modeReadbackVersion);
        var updated = await client.GetHomeStateAsync(cancellationToken);
        HomeStateSnapshot? previous;
        lock (gate)
        {
            previous = homeState;
            // A poll started before a newer firmware acknowledgement cannot undo that acknowledgement.
            if (readbackVersion != modeReadbackVersion && homeState?.Controls.PerformanceMode is { } latest)
                updated = updated with { Controls = updated.Controls with { PerformanceMode = latest } };
            homeState = updated;
        }
        if (ConfigurationRestorationSettled && updated.Capabilities.SupportState == DeviceSupportState.Ready &&
            updated.Controls.PerformanceMode is { } observedMode)
        {
            try { await automaticRestore.RecordObservedModeAsync(observedMode, cancellationToken,
                () => readbackVersion == Interlocked.Read(ref modeReadbackVersion) && State?.Controls.PerformanceMode == observedMode); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { AppRuntimeLog.Write($"Observed mode persistence: {error.Message}\n"); }
        }
        var previousStatus = Status;
        Status = updated.Capabilities.SupportState switch
        {
            DeviceSupportState.Ready => HomeSessionStatus.Connected,
            DeviceSupportState.RepairRequired => HomeSessionStatus.RepairRequired,
            _ => HomeSessionStatus.ReadOnly
        };
        if (previous is null || previousStatus != Status || !HomeStateEquivalence.HaveSameControls(previous, updated))
            StateChanged?.Invoke(updated);
        var shouldNotify = false;
        lock (gate)
        {
            if (Status is HomeSessionStatus.ReadOnly or HomeSessionStatus.RepairRequired)
            {
                shouldNotify = lastNotifiedUnavailableStatus != Status;
                lastNotifiedUnavailableStatus = Status;
            }
            else
            {
                lastNotifiedUnavailableStatus = null;
            }
        }
        if (shouldNotify)
            Notify(HomeSessionMessages.ReadOnlyMode);
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
        ErrorCode.ReadOnlySafeMode => HomeSessionMessages.ReadOnlyMode,
        ErrorCode.BiosUnsupported => "当前 BIOS 未验证，硬件控制未执行",
        ErrorCode.DependencyMissing => "硬件依赖缺失，已尝试修复但仍不可用",
        ErrorCode.ConflictDetected => "检测到其他控制程序，已暂停硬件控制",
        ErrorCode.DeviceMismatch => "当前设备不是已验证的适配基线",
        ErrorCode.ServiceUnavailable => "硬件服务暂不可用",
        _ => "当前硬件功能不可用，已尝试修复但仍未成功"
    };
}
