using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class AutomationWorkspaceV2
{
    private HomeControlSession? automationService;
    private Func<IReadOnlyCollection<PresetKey>, CancellationToken, Task<AdaptiveAutomationPreset[]>>? servicePresetReader;
    private AdaptiveAutomationStatus? serviceAutomationStatus;
    private readonly DispatcherTimer serviceContextTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource serviceClientLifetime = new();
    private bool servicePublishPending = true;
    private bool followConfigurationPending;
    private bool servicePublishBusy;
    private readonly SemaphoreSlim serviceConfigurationGate = new(1, 1);
    private bool contextBusy;
    private bool wasServiceConnected;
    private int configurationRevision;
    private DateTimeOffset serviceRetryAfter;
    private DateTimeOffset lastContextSubmitted;
    private string? serviceSubmissionError;

    public void AttachService(HomeControlSession value)
    {
        automationService = value;
        serviceContextTimer.Tick += (_, _) => RefreshServiceConnection();
        serviceContextTimer.Start();
        Loaded += (_, _) => serviceContextTimer.Start();
        // Page visibility does not end scheduling; StopServiceClient owns the heartbeat lifetime.
    }

    public void SetServicePresetReader(Func<IReadOnlyCollection<PresetKey>, CancellationToken, Task<AdaptiveAutomationPreset[]>> reader)
    {
        servicePresetReader = reader;
        RequestServiceConfiguration();
    }
    public void RefreshFollowConfiguration()
    {
        followConfigurationPending = true;
        RequestServiceConfiguration();
    }

    private void RequestServiceConfiguration()
    {
        configurationRevision++;
        servicePublishPending = true;
        serviceRetryAfter = DateTimeOffset.MinValue;
        serviceSubmissionError = null;
        RefreshServiceConnection();
    }

    private void RefreshServiceConnection()
    {
        var connected = automationService?.Status == HomeSessionStatus.Connected;
        if (connected && !wasServiceConnected)
        {
            serviceAutomationStatus = automationService?.State?.Controls.AdaptiveAutomation;
            autoEnabled = serviceAutomationStatus?.Enabled == true;
            // Reconnection observes the persisted service switch; it never reenables stale UI preferences.
            servicePublishPending = followConfigurationPending;
        }
        wasServiceConnected = connected;
        if (!connected || serviceClientLifetime.IsCancellationRequested)
        {
            RefreshServiceStatus();
            return;
        }
        if (automationService!.ConfigurationRestorationSettled && servicePublishPending && !servicePublishBusy && DateTimeOffset.UtcNow >= serviceRetryAfter && servicePresetReader is not null)
            _ = PublishServiceConfigurationAsync();
        if (autoEnabled && !contextBusy && DateTimeOffset.UtcNow - lastContextSubmitted >= TimeSpan.FromSeconds(2))
            _ = SendServiceContextAsync();
    }

    private async Task<bool> PublishServiceConfigurationAsync()
    {
        servicePublishBusy = true;
        await serviceConfigurationGate.WaitAsync(serviceClientLifetime.Token);
        var revision = configurationRevision;
        var policy = activePolicy;
        var map = activeMap;
        var enabled = autoEnabled;
        var strategy = selection.Active;
        try
        {
            var configuration = await BuildServiceConfigurationAsync(strategy, policy, map, enabled, serviceClientLifetime.Token);
            if (revision != configurationRevision) return false;
            await ConfirmServiceConfigurationAsync(configuration, serviceClientLifetime.Token);
            if (revision != configurationRevision) return false;
            servicePublishPending = false;
            followConfigurationPending = false;
            serviceSubmissionError = null;
            return true;
        }
        catch (OperationCanceledException) when (serviceClientLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (revision == configurationRevision)
            {
                serviceSubmissionError = $"调度配置尚未接入服务：{exception.Message}";
                serviceRetryAfter = DateTimeOffset.UtcNow.AddSeconds(10);
            }
        }
        finally
        {
            servicePublishBusy = false;
            serviceConfigurationGate.Release();
            RefreshServiceStatus();
        }
        return false;
    }

    private async Task<AdaptiveAutomationConfiguration> BuildServiceConfigurationAsync(
        AdaptiveStrategyId strategy, AdaptiveTriggerPolicy policy, AdaptiveTargetMap map, bool enabled, CancellationToken cancellationToken)
    {
        policy.Validate();
        var advanced = policy.Advanced;
        var presets = (enabled || preferences.Load().LightingFollowPreset) && servicePresetReader is { } reader
            ? await reader(enabled ? new[] { map.AcOffice, map.AcGame, map.AcTurbo, map.DcOffice, map.DcGame } : [], cancellationToken)
            : [];
        return new AdaptiveAutomationConfiguration(enabled, (AdaptiveAutomationStrategyId)strategy,
            new(policy.GameCpuPercent, policy.GameGpuPercent, policy.GameSeconds, policy.TurboEnabled,
                policy.TurboCpuPercent, policy.TurboGpuPercent, policy.TurboSeconds,
                policy.OfficeCpuPercent, policy.OfficeGpuPercent, policy.OfficeSeconds,
                advanced.LowBatteryPercent, advanced.BatteryRecoveryPercent, advanced.RespectBatterySaver,
                advanced.CpuTemperatureCeiling, advanced.GpuTemperatureCeiling, advanced.CooldownSeconds,
                advanced.MinimumDwellSeconds, advanced.ApplicationSeconds, advanced.IdleReturnEnabled,
                advanced.IdleSeconds, advanced.ApplicationRules.Select(rule => new AdaptiveAutomationApplicationRule(
                    rule.Executable, rule.ForegroundOnly, (AdaptiveAutomationStageId)rule.Target) { RequireActivity = rule.RequireActivity }).ToArray())
            { TurboExitCpuPercent = policy.TurboExitCpuPercent, TurboExitGpuPercent = policy.TurboExitGpuPercent,
                TurboExitSeconds = policy.TurboExitSeconds, TurboMinimumDwellSeconds = policy.TurboMinimumDwellSeconds },
            new(map.AcOffice, map.AcGame, map.AcTurbo, map.DcOffice, map.DcGame), presets);
    }

    private async Task ConfirmServiceConfigurationAsync(AdaptiveAutomationConfiguration configuration, CancellationToken cancellationToken,
        AutomationConfigurationIntent intent = AutomationConfigurationIntent.Update)
    {
        var result = await automationService!.ExecuteAsync(
            new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration)
            { ExpectedRevision = automationService.State?.Controls.AdaptiveAutomation?.Revision ?? 0, Intent = intent }, cancellationToken);
        if (result.State != CommandState.Applied || result.Error is not null)
            throw new InvalidOperationException(result.Error?.Code.ToString() ?? "服务未确认调度配置");
        var actual = automationService.State?.Controls.AdaptiveAutomation;
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(configuration with { Revision = 0 })))[..16];
        if (actual?.OwnerConfigurationIdentity != identity || actual.Enabled != configuration.Enabled)
            throw new InvalidOperationException("调度配置读回不一致");
        serviceAutomationStatus = actual;
    }

    private async Task SendServiceContextAsync()
    {
        contextBusy = true;
        lastContextSubmitted = DateTimeOffset.UtcNow;
        try
        {
            var signals = AdaptiveRuntimeSignals.Read();
            await automationService!.SendAdaptiveContextAsync(new(DateTimeOffset.UtcNow,
                signals.Foreground ? signals.Executable : null, signals.IdleSeconds, signals.BatterySaver)
            {
                RunningExecutables = AdaptiveRuntimeSignals.ReadRunningExecutables(
                    activePolicy.Advanced.ApplicationRules.Where(rule => !rule.ForegroundOnly).Select(rule => rule.Executable))
            }, serviceClientLifetime.Token);
        }
        catch (OperationCanceledException) when (serviceClientLifetime.IsCancellationRequested) { }
        catch (Exception) { /* Context expiry is evaluated by the service; never reuse stale foreground data. */ }
        finally { contextBusy = false; }
    }

    private void RefreshServiceStatus()
    {
        if (serviceClientLifetime.IsCancellationRequested) return;
        bool connected = automationService?.Status == HomeSessionStatus.Connected;
        var unavailable = automationService?.Status switch
        {
            HomeSessionStatus.ReadOnly => (Status: "只读，未运行", Reason: "服务只读，自动调度未运行。"),
            HomeSessionStatus.RepairRequired => (Status: "服务需修复", Reason: "服务需修复，自动调度未运行。"),
            HomeSessionStatus.Connecting => (Status: "正在连接", Reason: "正在连接，自动调度尚未运行。"),
            _ => (Status: "未连接", Reason: "服务未连接，自动调度未运行。")
        };
        var reason = !connected ? unavailable.Reason : serviceSubmissionError ?? (servicePublishPending ? "正在同步已保存策略到系统服务。"
            : serviceAutomationStatus?.Reason ?? "正在等待系统服务的调度状态。");
        DecisionReasonText.Text = connected && !autoEnabled && !servicePublishPending ? "自动调度已关闭" : reason;
        ServiceControlStatusText.Text = !connected ? unavailable.Status : servicePublishPending ? "正在同步策略"
            : !autoEnabled ? "自动调度已关闭"
            : serviceSubmissionError is not null ? "策略同步失败"
            : serviceAutomationStatus?.Running == true ? "系统服务正在调度" : "系统服务已暂停";
        var candidate = connected && autoEnabled && !servicePublishPending ? serviceAutomationStatus?.CandidateTarget : null;
        CandidateModeText.Text = candidate is { } key ? AdaptiveTargetMap.PerformanceModeFor(key) switch
        {
            PerformanceMode.Quiet => "办公", PerformanceMode.Balanced => "游戏", PerformanceMode.Turbo => "狂飙", _ => "自定义"
        } : !connected ? "未运行" : !autoEnabled ? "未开启" : serviceAutomationStatus?.Running == false ? "已暂停" : "保持当前";
        CandidateDetailText.Text = candidate is { } candidateKey ? TargetName(candidateKey, preferences.Load().PresetNames)
            : !connected ? "仅编辑方案" : "等待有效判定";
        if (lastTelemetry is { CpuTemperatureC: { } cpu, GpuTemperatureC: { } gpu } telemetry
            && double.IsFinite(cpu) && double.IsFinite(gpu) && DateTimeOffset.UtcNow - telemetry.CapturedAtUtc <= TimeSpan.FromSeconds(3))
        {
            var margin = Math.Min(activePolicy.Advanced.CpuTemperatureCeiling - cpu, activePolicy.Advanced.GpuTemperatureCeiling - gpu);
            ThermalMarginText.Text = $"热余量 {Math.Max(0, margin):0}°\nCPU {cpu:0}° · GPU {gpu:0}°";
            ToolTipService.SetToolTip(ThermalMarginText, $"策略温度门槛：CPU {activePolicy.Advanced.CpuTemperatureCeiling}°C / GPU {activePolicy.Advanced.GpuTemperatureCeiling}°C");
        }
        else ThermalMarginText.Text = "温度待确认";
        DecisionDetailsText.Text = reason + (connected && serviceAutomationStatus?.LastError is { } error ? $"\n{error}" : "")
            + "\n仅在客户端运行且控制会话有效时调度；完全退出后停止并恢复本次会话约定的设置。";
    }

    public void StopServiceClient()
    {
        serviceContextTimer.Stop();
        serviceClientLifetime.Cancel();
    }
}
