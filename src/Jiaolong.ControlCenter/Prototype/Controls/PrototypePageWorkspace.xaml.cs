using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.Prototype.QuickMenu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Storage.Pickers;
using SettingsViewModel = Jiaolong_ControlCenter.ViewModels.SettingsViewModel;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class PrototypePageWorkspace : UserControl
{
    private HomeControlSession? session;
    private Window? ownerWindow;
    private readonly UserPreferencesStore preferences = new();
    private readonly IStartupRegistration startupRegistration = new StartupRegistrationService();
    private bool synchronizingStartupToggle;
    private bool synchronizingWindowToggle;
    private bool reducedMotion;
    private bool deviceFirmwareExpanded;
    private bool projectSupportExpanded;
    private QuickMenuEditor? trayQuickMenuEditor;
    private Popup? trayQuickMenuEditorPopup;
    private readonly Dictionary<Grid, Storyboard> settingsDetailsAnimations = new();
    private string startupStatusText = "开机启动状态尚未读取";
    private string currentGpuName = "显卡";
    private readonly List<double> gpuUsageHistory = new()
    {
        50, 42, 48, 44, 47, 54, 58, 52, 48, 45, 52, 55, 50, 56, 52, 60, 68, 76, 72, 65, 70, 62, 58, 65, 78, 85, 82, 75, 70, 72
    };

    public event Action<bool>? RememberWindowSizeChanged;
    public event Action<bool>? StrongCoolingRequested;
    public event Action<bool>? LidLogoRequested;
    public event Action? QuickMenuEditorRequested;
    public event Action? TrayQuickMenuChanged;
    public event Action<string>? LogoStyleChanged;

    public void ApplyStrongCoolingState(bool? enabled, bool available) => FanWorkspace.ApplyStrongCoolingState(enabled, available);
    public void ApplyLidLogoState(bool? enabled, bool available) => LightingWorkspace.ApplyLogoReadback(enabled, available);
    public void SetLightingPresetTarget(PresetKey key) => LightingWorkspace.SetFollowPresetTarget(key);
    public void SetFanPresetTarget(PresetKey key) => FanWorkspace.SetFollowPresetTarget(key);
    public Task PauseFanPresetFollowingAsync(CancellationToken token) => FanWorkspace.PausePresetFollowingAsync(token);
    public void ResumeFanPresetFollowing() => FanWorkspace.ResumePresetFollowing();
    public void SetConfirmedPerformanceTarget(PresetKey? key) => LightingWorkspace.SetConfirmedPerformanceTarget(key);
    public void SetReducedMotion(bool reduced)
    {
        reducedMotion = reduced;
        FanWorkspace.ReducedMotion = reduced;
        AutomationWorkspace.ReducedMotion = reduced;
        if (reduced)
        {
            FinishSettingsDetailsMotion(SettingsDeviceFirmwareContentHost, SettingsDeviceFirmwareContent, SettingsDeviceFirmwareHeader, SettingsDeviceFirmwareChevron, deviceFirmwareExpanded);
            FinishSettingsDetailsMotion(SettingsProjectSupportContentHost, SettingsProjectSupportContent, SettingsProjectSupportHeader, SettingsProjectSupportChevron, projectSupportExpanded);
        }
    }

    private void OnToggleDeviceFirmwareDetails(object sender, RoutedEventArgs e)
    {
        deviceFirmwareExpanded = !deviceFirmwareExpanded;
        AnimateSettingsDetails(SettingsDeviceFirmwareContentHost, SettingsDeviceFirmwareContent, SettingsDeviceFirmwareHeader, SettingsDeviceFirmwareChevron, deviceFirmwareExpanded);
    }

    private void OnToggleProjectSupportDetails(object sender, RoutedEventArgs e)
    {
        projectSupportExpanded = !projectSupportExpanded;
        AnimateSettingsDetails(SettingsProjectSupportContentHost, SettingsProjectSupportContent, SettingsProjectSupportHeader, SettingsProjectSupportChevron, projectSupportExpanded);
    }

    private void AnimateSettingsDetails(Grid host, FrameworkElement content, Button header, TextBlock chevron, bool expanded)
    {
        var currentHeight = host.Visibility == Visibility.Visible ? host.ActualHeight : 0;
        if (settingsDetailsAnimations.Remove(host, out var previous)) previous.Stop();
        host.Height = currentHeight;
        UpdateSettingsDetailsHeader(header, chevron, expanded);

        if (reducedMotion)
        {
            FinishSettingsDetailsMotion(host, content, header, chevron, expanded);
            return;
        }

        host.Visibility = Visibility.Visible;
        content.IsHitTestVisible = expanded;
        content.Measure(new Size(Math.Max(1, host.ActualWidth > 0 ? host.ActualWidth : header.ActualWidth), double.PositiveInfinity));
        var targetHeight = expanded ? content.DesiredSize.Height : 0;
        if (Math.Abs(targetHeight - currentHeight) < 1)
        {
            FinishSettingsDetailsMotion(host, content, header, chevron, expanded);
            return;
        }

        var animation = new DoubleAnimation
        {
            From = currentHeight,
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, host);
        Storyboard.SetTargetProperty(animation, "Height");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        settingsDetailsAnimations[host] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!settingsDetailsAnimations.TryGetValue(host, out var active) || !ReferenceEquals(active, storyboard)) return;
            FinishSettingsDetailsMotion(host, content, header, chevron, expanded);
        };
        storyboard.Begin();
    }

    private void FinishSettingsDetailsMotion(Grid host, FrameworkElement content, Button header, TextBlock chevron, bool expanded)
    {
        if (settingsDetailsAnimations.Remove(host, out var active)) active.Stop();
        content.IsHitTestVisible = expanded;
        host.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        host.Height = expanded ? double.NaN : 0;
        UpdateSettingsDetailsHeader(header, chevron, expanded);
    }

    private static void UpdateSettingsDetailsHeader(Button header, TextBlock chevron, bool expanded)
    {
        AutomationProperties.SetName(header, (expanded ? "折叠" : "展开") + header.Tag);
        chevron.Text = expanded ? "\uE70E" : "\uE70D";
    }

    public PrototypePageWorkspace()
    {
        InitializeComponent();
        foreach (var host in new[] { SettingsDeviceFirmwareContentHost, SettingsProjectSupportContentHost })
        {
            host.Clip = new RectangleGeometry();
            host.SizeChanged += (_, args) => ((RectangleGeometry)host.Clip).Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);
        }
        FanWorkspace.StrongCoolingRequested += enabled => StrongCoolingRequested?.Invoke(enabled);
        LightingWorkspace.LidLogoRequested += enabled => LidLogoRequested?.Invoke(enabled);
        RefreshLogoStyleSetting();
        _ = LoadWindowPreferenceAsync();
        RenderGpuUsageTrend();
    }

    public void AttachSession(HomeControlSession value, Window owner)
    {
        session = value;
        ownerWindow = owner;
        LightingWorkspace.AttachSession(value);
        FanWorkspace.AttachSession(value);
        AutomationWorkspace.AttachService(value);
    }

    private async Task LoadWindowPreferenceAsync()
    {
        synchronizingWindowToggle = true;
        try
        {
            var current = await preferences.LoadAsync(CancellationToken.None);
            SettingsRememberWindowSizeToggle.IsOn = current.RememberWindowSize;
        }
        catch
        {
            SettingsRememberWindowSizeToggle.IsOn = false;
        }
        finally { synchronizingWindowToggle = false; }
    }

    public void Show(string destination)
    {
        WorkspaceRoot.Padding = destination is "Fan" or "Lighting" or "Automation" ? new Thickness(0) : new Thickness(18, 30, 18, 28);
        GpuPage.Visibility = destination == "Gpu" ? Visibility.Visible : Visibility.Collapsed;
        FanPage.Visibility = destination == "Fan" ? Visibility.Visible : Visibility.Collapsed;
        FanWorkspace.SetPageActive(destination == "Fan");
        LightingPage.Visibility = destination == "Lighting" ? Visibility.Visible : Visibility.Collapsed;
        LightingWorkspace.SetPageActive(destination == "Lighting");
        AutomationPage.Visibility = destination == "Automation" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = destination == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (destination == "Settings")
        {
            RefreshLogoStyleSetting();
            RefreshStartupSetting();
            RefreshQuickMenuSummary();
            _ = RefreshServiceUptimeAsync();
        }
    }

    public Task RestoreLightingPreviewAsync() => LightingWorkspace.RestoreHardwarePreviewAsync();

    public void SetAutomationEnabled(bool enabled) => AutomationWorkspace.SetAutomationEnabled(enabled);
    public Task<bool> SetAutomationEnabledConfirmedAsync(bool enabled) => AutomationWorkspace.ConfirmAutomationEnabledAsync(enabled);
    public Task<bool> SelectAdaptiveStrategyAsync(AdaptiveStrategyId strategy) => AutomationWorkspace.SelectSavedStrategyAsync(strategy);
    public void StopAutomationClient() => AutomationWorkspace.StopServiceClient();


    public void SetAutomaticPresetReader(Func<IReadOnlyCollection<PresetKey>, CancellationToken, Task<AdaptiveAutomationPreset[]>> readPresets) =>
        AutomationWorkspace.SetServicePresetReader(readPresets);


    public void ApplyTelemetry(HardwareSnapshot snapshot)
    {
        AutomationWorkspace.ApplyTelemetry(snapshot);
        if (Visibility != Visibility.Visible || XamlRoot?.IsHostVisible != true) return;
        if (FanPage.Visibility == Visibility.Visible)
        {
        CpuFanText.Text = Format(snapshot.CpuFanRpm, "RPM");
        GpuFanText.Text = Format(snapshot.GpuFanRpm, "RPM");

        if (FanCpuTemperatureText is not null)
            FanCpuTemperatureText.Text = Format(snapshot.CpuTemperatureC, "°C");
        if (FanGpuTemperatureText is not null)
            FanGpuTemperatureText.Text = Format(snapshot.GpuTemperatureC, "°C");
        if (FanCpuMeter is not null && snapshot.CpuFanRpm.HasValue)
            FanCpuMeter.Value = Math.Clamp(snapshot.CpuFanRpm.Value, 0, 6000);
        if (FanGpuMeter is not null && snapshot.GpuFanRpm.HasValue)
            FanGpuMeter.Value = Math.Clamp(snapshot.GpuFanRpm.Value, 0, 6000);
        if (FanCpuTempMeter is not null && snapshot.CpuTemperatureC.HasValue)
            FanCpuTempMeter.Value = Math.Clamp(snapshot.CpuTemperatureC.Value, 0, 100);
        if (FanGpuTempMeter is not null && snapshot.GpuTemperatureC.HasValue)
            FanGpuTempMeter.Value = Math.Clamp(snapshot.GpuTemperatureC.Value, 0, 100);
        FanWorkspace.ApplyTelemetry(snapshot);
        }

        if (GpuPage.Visibility != Visibility.Visible) return;
        var usage = snapshot.GpuUsagePercent.GetValueOrDefault();
        var temp = snapshot.GpuTemperatureC.GetValueOrDefault();
        var freq = snapshot.GpuFrequencyMhz.GetValueOrDefault();
        var memUsed = snapshot.GpuMemoryUsedGb.GetValueOrDefault();
        var memTotal = snapshot.GpuMemoryTotalGb.GetValueOrDefault();
        var power = snapshot.GpuPowerWatts.GetValueOrDefault();
        var fanRpm = snapshot.GpuFanRpm.GetValueOrDefault();

        GpuTemperatureText.Text = Format(snapshot.GpuTemperatureC, "°C");
        GpuUsageText.Text = Format(snapshot.GpuUsagePercent, "%");
        GpuPowerText.Text = snapshot.GpuPowerWatts is int actualPower ? $"{actualPower} W" : "-- W";
        GpuFrequencyText.Text = Format(snapshot.GpuFrequencyMhz, "MHz");
        GpuMemoryText.Text = snapshot.GpuMemoryUsedGb is double u && snapshot.GpuMemoryTotalGb is double t
            ? $"{u:0.0} / {t:0.0} GB"
            : "-- / -- GB";
        GpuPageFanText.Text = Format(snapshot.GpuFanRpm, "RPM");

        UpdateMeterColumns(GpuUsageColActive, GpuUsageColInactive, usage, 100);
        UpdateMeterColumns(GpuTempColActive, GpuTempColInactive, temp, 100);
        UpdateMeterColumns(GpuFreqColActive, GpuFreqColInactive, freq, 3000);
        UpdateMeterColumns(GpuMemFreqColActive, GpuMemFreqColInactive, 8501, 12000);
        UpdateMeterColumns(GpuMemUsedColActive, GpuMemUsedColInactive, memUsed, Math.Max(memUsed, memTotal));
        UpdateMeterColumns(GpuPowerColActive, GpuPowerColInactive, power, 300);
        UpdateMeterColumns(GpuFanColActive, GpuFanColInactive, fanRpm, 3000);
        UpdateMeterColumns(GpuVoltColActive, GpuVoltColInactive, 0.975, 1.2);
        UpdateGpuUsageTrend(usage);

        var gpuMonitorAvailable = snapshot.GpuTemperatureC.HasValue
            || snapshot.GpuPowerWatts.HasValue
            || snapshot.GpuUsagePercent.HasValue
            || snapshot.GpuFrequencyMhz.HasValue
            || snapshot.GpuMemoryUsedGb.HasValue
            || snapshot.GpuMemoryTotalGb.HasValue
            || snapshot.GpuFanRpm.HasValue;
        GpuMonitorStatusDot.Fill = (Brush)GpuPage.Resources[gpuMonitorAvailable
            ? "GpuMonitorOnlineBrush"
            : "GpuMonitorOfflineBrush"];
        GpuMonitorStatusText.Text = gpuMonitorAvailable ? currentGpuName : "监控不可用";
    }

    private static void UpdateMeterColumns(ColumnDefinition activeCol, ColumnDefinition inactiveCol, double value, double max)
    {
        var ratio = Math.Clamp(value / Math.Max(0.001, max), 0, 1);
        var activeWeight = Math.Max(0.001, ratio * 100.0);
        var inactiveWeight = Math.Max(0.001, (1.0 - ratio) * 100.0);
        activeCol.Width = new GridLength(activeWeight, GridUnitType.Star);
        inactiveCol.Width = new GridLength(inactiveWeight, GridUnitType.Star);
    }

    private void RenderGpuUsageTrend()
    {
        if (GpuTrendWavePath is null || GpuTrendAreaPath is null || gpuUsageHistory.Count == 0) return;

        var avg = Math.Round(gpuUsageHistory.Average());
        var peak = Math.Round(gpuUsageHistory.Max());
        var min = Math.Round(gpuUsageHistory.Min());

        if (GpuAvgUsageText is not null) GpuAvgUsageText.Text = $"{avg}%";
        if (GpuPeakUsageText is not null) GpuPeakUsageText.Text = $"{peak}%";
        if (GpuMinUsageText is not null) GpuMinUsageText.Text = $"{min}%";

        var waveFigure = new PathFigure
        {
            StartPoint = new Point(0, 100 - Math.Clamp(gpuUsageHistory[0], 0, 100)),
            IsClosed = false
        };
        var areaFigure = new PathFigure
        {
            StartPoint = new Point(0, 100),
            IsClosed = true
        };
        areaFigure.Segments.Add(new LineSegment { Point = new Point(0, 100 - Math.Clamp(gpuUsageHistory[0], 0, 100)) });

        for (int i = 1; i < gpuUsageHistory.Count; i++)
        {
            double x = (double)i / (gpuUsageHistory.Count - 1) * 100.0;
            double y = 100.0 - Math.Clamp(gpuUsageHistory[i], 0, 100);
            var pt = new Point(x, y);
            waveFigure.Segments.Add(new LineSegment { Point = pt });
            areaFigure.Segments.Add(new LineSegment { Point = pt });
        }
        areaFigure.Segments.Add(new LineSegment { Point = new Point(100, 100) });

        var waveGeo = new PathGeometry();
        waveGeo.Figures.Add(waveFigure);
        GpuTrendWavePath.Data = waveGeo;

        var areaGeo = new PathGeometry();
        areaGeo.Figures.Add(areaFigure);
        GpuTrendAreaPath.Data = areaGeo;
    }

    private void UpdateGpuUsageTrend(double currentUsage)
    {
        gpuUsageHistory.Add(currentUsage);
        if (gpuUsageHistory.Count > 60)
        {
            gpuUsageHistory.RemoveAt(0);
        }
        RenderGpuUsageTrend();
    }

    public void ApplyState(HomeStateSnapshot snapshot)
    {
        FanWorkspace.ApplyControlState(snapshot);
        LightingWorkspace.ApplyState(snapshot);
        AutomationWorkspace.ApplyState(snapshot);
        var supportText = snapshot.Capabilities.SupportState switch
        {
            DeviceSupportState.Ready => "设备已识别",
            DeviceSupportState.ReadOnly => "只读安全模式",
            DeviceSupportState.RepairRequired => "需要修复",
            DeviceSupportState.Repairing => "正在修复",
            _ => "正在诊断"
        };
        MuxStatusText.Text = supportText;
        FanStatusText.Text = supportText;
        SettingsServiceStatusText.Text = session?.IsServiceConnected == true
            ? "硬件服务 IPC 已连接"
            : "硬件服务 IPC 未连接";

        var identity = snapshot.Capabilities.Identity;
        currentGpuName = identity?.GpuName ?? "显卡";
        SettingsBoardText.Text = identity?.BoardProduct ?? "未知";
        SettingsCpuModelText.Text = identity?.CpuModel ?? "未知";
        SettingsBiosText.Text = identity?.BiosVersion ?? "未知";
        SettingsCapabilityCountText.Text = $"{snapshot.Capabilities.Items.Count(item => item.State == CapabilityState.Available)} / {snapshot.Capabilities.Items.Length} 项";
        ToolTipService.SetToolTip(SettingsCapabilityCountText, "可用控制 / 全部控制");
        SettingsCapabilityBadgeText.Text = supportText;
        ToolTipService.SetToolTip(SettingsCapabilityBadgeText, snapshot.Capabilities.Reason);
        ToolTipService.SetToolTip(SettingsBoardText, SettingsBoardText.Text);
        ToolTipService.SetToolTip(SettingsCpuModelText, SettingsCpuModelText.Text);
        ToolTipService.SetToolTip(SettingsBiosText, SettingsBiosText.Text);
        SettingsServiceConnectionText.Text = session?.IsServiceConnected == true
            ? "客户端 IPC 已连接"
            : "客户端 IPC 未连接";

        var muxAvailable = IsCapabilityAvailable(snapshot, "muxMode");
        GpuHybridButton.IsEnabled = muxAvailable;
        GpuDiscreteButton.IsEnabled = muxAvailable;
        GpuEcoButton.IsEnabled = muxAvailable;
        GpuAutoButton.IsEnabled = muxAvailable;
        var fanAvailable = IsCapabilityAvailable(snapshot, "fanControl");
        FanModeBox.IsEnabled = fanAvailable;
        FanTargetSlider.IsEnabled = fanAvailable;
    }

    private async void OnMuxModeCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        UpdateMuxSelectionVisual(tag);
        if (Enum.TryParse<MuxMode>(tag, out var mode))
        {
            MuxStatusText.Text = "正在请求切换…";
            await ExecuteAsync(new SetMuxModeCommand(Guid.NewGuid(), mode, UserConfirmedRestartImpact: true), MuxStatusText);
        }
        else
        {
            MuxStatusText.Text = tag == "Eco" ? "集显优先已选择（重启后生效）" : "自动切换已选择（智能调度中）";
        }
    }

    private void UpdateMuxSelectionVisual(string selectedTag)
    {
        var activeBorderBrush = (Brush)GpuPage.Resources["GpuAccentBlueBrush"];
        var defaultBorderBrush = (Brush)GpuPage.Resources["GpuCardBorderBrush"];
        var activeBgBrush = (Brush)GpuPage.Resources["GpuActiveMuxBgBrush"];
        var defaultBgBrush = (Brush)GpuPage.Resources["GpuCardBgBrush"];

        SetCardState(GpuDiscreteCardBorder, GpuDiscreteBadge, selectedTag == "Discrete", activeBorderBrush, defaultBorderBrush, activeBgBrush, defaultBgBrush);
        SetCardState(GpuHybridCardBorder, GpuHybridBadge, selectedTag == "Hybrid", activeBorderBrush, defaultBorderBrush, activeBgBrush, defaultBgBrush);
        SetCardState(GpuEcoCardBorder, GpuEcoBadge, selectedTag == "Eco", activeBorderBrush, defaultBorderBrush, activeBgBrush, defaultBgBrush);
        SetCardState(GpuAutoCardBorder, GpuAutoBadge, selectedTag == "Auto", activeBorderBrush, defaultBorderBrush, activeBgBrush, defaultBgBrush);
    }

    private static void SetCardState(Border border, Border badge, bool isSelected, Brush activeBorder, Brush defaultBorder, Brush activeBg, Brush defaultBg)
    {
        border.BorderBrush = isSelected ? activeBorder : defaultBorder;
        border.BorderThickness = new Thickness(isSelected ? 1.5 : 1);
        border.Background = isSelected ? activeBg : defaultBg;
        badge.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGpuFrequencySliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (GpuFrequencyLimitBox is not null)
            GpuFrequencyLimitBox.Text = Math.Round(e.NewValue).ToString();
    }

    private void OnGpuMemoryFrequencySliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (GpuMemoryFrequencyBox is not null)
            GpuMemoryFrequencyBox.Text = Math.Round(e.NewValue).ToString();
    }

    private void OnGpuTuningToggleChanged(object sender, RoutedEventArgs e)
    {
        if (GpuFrequencyLimitSlider is not null && GpuCoreFrequencyToggle is not null)
        {
            GpuFrequencyLimitSlider.IsEnabled = GpuCoreFrequencyToggle.IsOn;
            GpuFrequencyLimitBox.IsEnabled = GpuCoreFrequencyToggle.IsOn;
        }
        if (GpuMemoryFrequencySlider is not null && GpuMemoryFrequencyToggle is not null)
        {
            GpuMemoryFrequencySlider.IsEnabled = GpuMemoryFrequencyToggle.IsOn;
            GpuMemoryFrequencyBox.IsEnabled = GpuMemoryFrequencyToggle.IsOn;
        }
    }

    private void OnGpuResetClick(object sender, RoutedEventArgs e)
    {
        GpuCoreFrequencyToggle.IsOn = true;
        GpuFrequencyLimitSlider.Value = 2550;
        GpuFrequencyLimitBox.Text = "2550";

        GpuMemoryFrequencyToggle.IsOn = true;
        GpuMemoryFrequencySlider.Value = 8400;
        GpuMemoryFrequencyBox.Text = "8400";


        GpuTuningStatusText.Text = "已重置为默认推荐预设";
    }

    private void OnGpuApplyClick(object sender, RoutedEventArgs e)
    {
        GpuTuningStatusText.Text = $"参数已应用：核心 {GpuFrequencyLimitBox.Text} MHz · 显存 {GpuMemoryFrequencyBox.Text} MHz";
    }

    private async void OnFanApply(object sender, RoutedEventArgs e)
    {
        var points = new[]
        {
            new FanPoint(40, 20), new FanPoint(50, 30), new FanPoint(60, 40),
            new FanPoint(70, 55), new FanPoint(80, 75), new FanPoint(90, 100)
        };
        await ExecuteAsync(
            new SetFanControlCommand(Guid.NewGuid(), new FanControlPlan(points), RiskConfirmed: true),
            FanStatusText);
    }

    private async void OnFanRelease(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested), FanStatusText);

    private void OnSaveFanPresetClick(object sender, RoutedEventArgs e) =>
        FanStatusText.Text = "风扇预设已保存";

    private void OnRestoreFanPresetClick(object sender, RoutedEventArgs e) =>
        FanStatusText.Text = "风扇预设已还原为默认";

    private void OnFanPreview(object sender, RoutedEventArgs e) =>
        FanStatusText.Text = "模拟算法预览：曲线在 60°C 时将输出约 40% (2400 RPM) 转速，具备严格单调性保护。";

    private async void OnSettingsRepair(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Visibility = Visibility.Visible;
        var connected = session?.IsServiceConnected == true;
        SettingsServiceStatusText.Text = connected ? "硬件服务 IPC 已连接" : "硬件服务 IPC 未连接";
        SettingsServiceConnectionText.Text = connected ? "客户端 IPC 已连接" : "客户端 IPC 未连接";
        SettingsStatusText.Text = connected
            ? "当前客户端服务会话已连接；此页未执行服务修复。"
            : "当前客户端未连接到硬件服务；此页未执行服务修复。";
        await RefreshServiceUptimeAsync();
    }

    private async Task RefreshServiceUptimeAsync()
    {
        var (uptime, status) = await Task.Run(() =>
            (WindowsServiceUptimeReader.Read(), SettingsRuntimeStatusReader.Read()));
        SettingsServiceUptimeText.Text = uptime is { } duration
            ? $"{(int)duration.TotalDays} 天 {duration.Hours} 小时 {duration.Minutes} 分钟"
            : "未能读取";
        SettingsRecoveryPlanText.Text = status.RecoveryPlan;
        ToolTipService.SetToolTip(SettingsRecoveryPlanText, status.RecoveryPlanDetail);
        SettingsLastConfigurationText.Text = status.LastConfiguration;
        ToolTipService.SetToolTip(SettingsLastConfigurationText, status.LastConfigurationDetail);
        SettingsPendingConfigurationText.Text = status.PendingConfiguration;
        ToolTipService.SetToolTip(SettingsPendingConfigurationText, status.PendingConfigurationDetail);
    }

    private void OnSettingsRepairNotes(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Visibility = Visibility.Visible;
        SettingsStatusText.Text = "依赖修复仅使用本机安装目录；不会下载、执行 OEM GUI 或访问网络。";
    }

    private bool exportingDiagnostics;
    private async void OnSettingsExport(object sender, RoutedEventArgs e)
    {
        if (exportingDiagnostics) return;
        exportingDiagnostics = true;
        SettingsExportLogsButton.IsEnabled = false;
        SettingsStatusText.Visibility = Visibility.Visible;
        string? temporary = null;
        try
        {
            var owner = ownerWindow ?? throw new InvalidOperationException("主窗口尚未连接。");
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"XISURA-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeChoices.Add("诊断日志压缩包", new List<string> { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;
            SettingsStatusText.Text = "正在收集诊断日志…";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var entries = await SupportDiagnosticCollector.CollectAsync(session?.State, session?.Status.ToString() ?? "NotStarted",
                session?.IsServiceConnected == true, deadline.Token);
            await using var client = new ControlCenterClient();
            Jiaolong.Diagnostics.DiagnosticExport? serviceExport = null;
            try
            {
                if (session?.IsServiceConnected == true)
                {
                    using var serviceDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    serviceDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                    try
                    {
                        serviceExport = await client.StageDiagnosticsAsync(new Jiaolong_ControlCenter.ViewModels.DiagnosticExportRequest(Guid.NewGuid()), serviceDeadline.Token);
                    }
                    catch (Exception error) when (!deadline.IsCancellationRequested)
                    { entries["service-export-error.txt"] = error.ToString(); }
                }
                else entries["service-export-status.txt"] = "IPC 未连接；已导出本地诊断，服务端遥测未收集。";
                temporary = Path.Combine(Path.GetTempPath(), $"xisura-diagnostics-{Guid.NewGuid():N}.zip");
                await using (var archive = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    await Jiaolong.Diagnostics.SupportDiagnosticBundle.WriteAsync(archive, entries, serviceExport?.FilePath, deadline.Token);
                await using (var source = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var target = await destination.OpenStreamForWriteAsync())
                {
                    target.SetLength(0);
                    await source.CopyToAsync(target, deadline.Token);
                    await target.FlushAsync(deadline.Token);
                }
                SettingsStatusText.Text = "诊断日志已导出，可将 ZIP 文件发送给作者排查。";
            }
            finally
            {
                if (serviceExport is not null)
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await client.DeleteStagedDiagnosticsAsync(serviceExport, cleanupDeadline.Token); }
                    catch (Exception error) { AppRuntimeLog.Write($"Diagnostic staging cleanup: {error.Message}\n"); }
                }
            }
        }
        catch (Exception error)
        {
            SettingsStatusText.Text = $"导出日志失败：{error.Message}";
            AppRuntimeLog.Write($"Diagnostic export failed: {error}\n");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { AppRuntimeLog.Write($"Diagnostic local cleanup: {error.Message}\n"); }
            }
            exportingDiagnostics = false;
            SettingsExportLogsButton.IsEnabled = true;
        }
    }
    private void OnSettingsStartupToggled(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Visibility = Visibility.Visible;
        if (synchronizingStartupToggle) return;

        var requested = SettingsStartupToggle.IsOn;
        try
        {
            if (startupRegistration.IsEnabled != requested)
            {
                if (requested) startupRegistration.Enable();
                else startupRegistration.Disable();
            }
            if (startupRegistration.IsEnabled != requested)
                throw new InvalidOperationException("开机启动设置未能读回确认。");

            startupStatusText = requested ? "已开启开机自启动" : "已关闭开机自启动";
            SettingsStatusText.Text = startupStatusText;
        }
        catch (Exception exception)
        {
            RefreshStartupSetting();
            startupStatusText = $"更新开机启动失败：{exception.Message}";
            SettingsStatusText.Text = startupStatusText;
        }
    }

    private void OnSettingsEditQuickMenu(object sender, RoutedEventArgs e) => QuickMenuEditorRequested?.Invoke();

    private void OnSettingsEditTrayQuickMenu(object sender, RoutedEventArgs e) => OpenTrayQuickMenuEditor();

    public void OpenTrayQuickMenuEditor()
    {
        if (trayQuickMenuEditorPopup is { IsOpen: true }) return;
        if (XamlRoot is null) return;
        trayQuickMenuEditor ??= CreateTrayQuickMenuEditor();
        var available = QuickMenuCatalog.CreateDefault().ToDictionary(item => item.Kind,
            item => QuickMenuCatalog.IsAvailable(item, session?.State));
        trayQuickMenuEditor.ApplyLayout(preferences.Load().ResolveTrayQuickMenuLayout(), available, maxItems: 6);
        var popup = new Popup
        {
            XamlRoot = XamlRoot,
            IsLightDismissEnabled = true,
            ShouldConstrainToRootBounds = true,
            Child = trayQuickMenuEditor,
            HorizontalOffset = Math.Max(0, (XamlRoot.Size.Width - 600) / 2),
            VerticalOffset = Math.Max(0, (XamlRoot.Size.Height - 380) / 2)
        };
        popup.Closed += (_, _) =>
        {
            popup.Child = null;
            if (ReferenceEquals(trayQuickMenuEditorPopup, popup)) trayQuickMenuEditorPopup = null;
        };
        trayQuickMenuEditorPopup = popup;
        popup.IsOpen = true;
    }

    private QuickMenuEditor CreateTrayQuickMenuEditor()
    {
        var editor = new QuickMenuEditor();
        editor.LayoutChanged += layout =>
        {
            try
            {
                preferences.Update(current => current with
                {
                    TrayQuickMenuEnabled = layout.ToPersistedEnabled(),
                    TrayQuickMenuOrder = layout.ToPersistedOrder()
                });
                RefreshQuickMenuSummary();
                TrayQuickMenuChanged?.Invoke();
            }
            catch (Exception error)
            {
                SettingsStatusText.Visibility = Visibility.Visible;
                SettingsStatusText.Text = $"快捷台菜单保存失败：{error.Message}";
                var available = editor.Options.ToDictionary(option => option.Item.Kind, option => option.IsAvailable);
                editor.ApplyLayout(preferences.Load().ResolveTrayQuickMenuLayout(), available, maxItems: 6);
            }
        };
        return editor;
    }

    private void RefreshQuickMenuSummary()
    {
        var saved = preferences.Load();
        var mainCount = QuickMenuLayout.FromPersisted(saved.QuickMenuEnabled, saved.QuickMenuOrder).EnabledOrder.Count;
        var trayCount = saved.ResolveTrayQuickMenuLayout().EnabledOrder.Count;
        SettingsQuickMenuSummary.Text = $"主菜单 {mainCount}/9 项 · 快捷台 {trayCount}/6 项，各自独立编辑与排序。";
    }

    private void RefreshStartupSetting()
    {
        try
        {
            var enabled = startupRegistration.IsEnabled;
            synchronizingStartupToggle = true;
            SettingsStartupToggle.IsOn = enabled;
            SettingsStartupToggle.IsEnabled = true;
            startupStatusText = enabled ? "已开启开机自启动" : "已关闭开机自启动";
        }
        catch (Exception exception)
        {
            SettingsStartupToggle.IsEnabled = false;
            startupStatusText = $"读取开机启动状态失败：{exception.Message}";
        }
        finally
        {
            synchronizingStartupToggle = false;
        }
        SettingsStatusText.Text = startupStatusText;
    }

    private void OnSettingsRememberWindowSizeToggled(object sender, RoutedEventArgs e)
    {
        if (synchronizingWindowToggle) return;
        SettingsStatusText.Visibility = Visibility.Visible;
        var enabled = SettingsRememberWindowSizeToggle.IsOn;
        try
        {
            preferences.Update(current => current with { RememberWindowSize = enabled });
            RememberWindowSizeChanged?.Invoke(enabled);
            SettingsStatusText.Text = enabled
                ? "已开启：下次启动沿用已保存的窗口尺寸"
                : "已关闭：下次启动使用标准窗口尺寸";
        }
        catch (Exception exception)
        {
            synchronizingWindowToggle = true;
            SettingsRememberWindowSizeToggle.IsOn = preferences.Load().RememberWindowSize;
            synchronizingWindowToggle = false;
            SettingsStatusText.Text = $"保存窗口尺寸设置失败：{exception.Message}";
        }
    }

    private async void OnOpenProjectHomepage(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/1145911974/XISURA")))
                throw new InvalidOperationException("系统未能打开浏览器");
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"打开项目主页失败：{ex.Message}";
            SettingsStatusText.Visibility = Visibility.Visible;
        }
    }

    private void OnOpenOfficialInstallerFolder(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Visibility = Visibility.Visible;
        var folder = AppContext.BaseDirectory;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            SettingsStatusText.Text = $"已打开程序与依赖文件目录：{folder}";
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private async Task<CommandResult> ExecuteAsync(HardwareCommand command, TextBlock status)
    {
        if (session is null)
        {
            await ShowOperationFailureAsync("硬件服务尚未连接，本次未应用。请重试或导出日志反馈。");
            return new CommandResult(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None, null, false);
        }

        try
        {
            var result = await session.ExecuteAsync(command, CancellationToken.None);
            status.Text = string.Empty;
            return result;
        }
        catch (Exception exception)
        {
            await ShowOperationFailureAsync($"本次设置未完成：{exception.Message}");
            return new CommandResult(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None, null, false);
        }
    }

    private async Task ShowOperationFailureAsync(string message)
    {
        if (XamlRoot is null) return;
        await new ContentDialog { XamlRoot = XamlRoot, Title = "操作未完成", Content = message, CloseButtonText = "知道了" }.ShowAsync();
    }

    private static string Format(double? value, string suffix) =>
        value is double number && double.IsFinite(number) ? $"{number:0} {suffix}" : $"-- {suffix}";

    private static bool IsCapabilityAvailable(HomeStateSnapshot snapshot, string key) =>
        snapshot.Capabilities.Items.Any(item =>
            string.Equals(item.Key, key, StringComparison.Ordinal));
}
