using System.Diagnostics;
using System.Text.RegularExpressions;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class PerformanceWorkspace : UserControl
{
    private HomeControlSession? session;
    private readonly PerformancePresetStore presetStore = new();
    private readonly Dictionary<string, PerformanceDraft[]> defaults = new(StringComparer.Ordinal)
    {
        ["office"] = CreateDefaultPresets("office"),
        ["game"] = CreateDefaultPresets("game"),
        ["turbo"] = CreateDefaultPresets("turbo"),
        ["custom-1"] = CreateDefaultPresets("custom-1"),
        ["custom-2"] = CreateDefaultPresets("custom-2"),
        ["custom-3"] = CreateDefaultPresets("custom-3")
    };
    private string selectedProfile = "office";
    private int selectedPresetIndex;
    private bool synchronizingValues;
    private bool loadingPowerPlans;
    private int selectedAdvancedCore;
    private Guid selectedPowerPlanId;
    private bool cpuTuningAvailable;
    private bool curveOptimizerAvailable;
    private bool advancedSmuAvailable;
    private bool advancedPboAvailable;
    // The advanced section is a layout preview until its hardware read/write protocol is verified.
    private static readonly bool AdvancedTuningLayoutPreviewOnly = true;
    private bool applyInProgress;
    private bool localDraftChanged;
    private bool hardwareStateApplied;
    private CpuTuningState? pendingCpuTuningState;
    private readonly List<AnimatedMetric> animatedMetrics = new();
    private readonly Dictionary<int, int> perCoreCurveOptimizerDraft = new();
    private readonly DispatcherQueueTimer MetricAnimationTimer;
    private readonly DispatcherQueueTimer TelemetryRefreshTimer;
    private readonly DispatcherQueueTimer SaveFeedbackTimer;
    private Storyboard? saveFeedbackFade;
    private HardwareSnapshot? pendingTelemetry;
    private bool telemetryRefreshRunning;
    private bool isShutdown;
    private bool editingAcFrequency = true;
    private int editingAcMaxFrequencyMhz = 3_800;
    private int editingDcMaxFrequencyMhz = 3_800;
    private static readonly (Guid Id, string Name)[] PowerPlanDiscoveryCandidates =
    [
        (Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "高性能"),
        (Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a"), "节能"),
        (Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"), "卓越性能")
    ];

    private sealed class AnimatedMetric(TextBlock text, ProgressBar meter, string unit, double maximum, string format = "0")
    {
        public TextBlock Text { get; } = text;
        public ProgressBar Meter { get; } = meter;
        public string Unit { get; } = unit;
        public double Maximum { get; } = maximum;
        public string Format { get; } = format;
        public double? Current { get; set; }
        public double? Target { get; set; }
    }

    public PerformanceWorkspace()
    {
        InitializeComponent();
        ModePresetPickerControl.SelectedKeyChanged += OnModePresetSelected;
        MetricAnimationTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        MetricAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
        MetricAnimationTimer.Tick += OnMetricAnimationTick;
        TelemetryRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        TelemetryRefreshTimer.Interval = TimeSpan.FromSeconds(1);
        TelemetryRefreshTimer.Tick += OnTelemetryRefreshTick;
        SaveFeedbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        SaveFeedbackTimer.Interval = TimeSpan.FromSeconds(2);
        SaveFeedbackTimer.Tick += OnSaveFeedbackTimerTick;
        animatedMetrics.Add(new(CpuTemperatureText, CpuTemperatureMeter, "°C", 100));
        animatedMetrics.Add(new(CpuPowerText, CpuPowerMeter, "W", 120));
        animatedMetrics.Add(new(CpuUsageText, CpuUsageMeter, "%", 100));
        animatedMetrics.Add(new(CpuFrequencyText, CpuFrequencyMeter, "GHz", 5.4, "0.0"));
        animatedMetrics.Add(new(CpuVoltageText, CpuVoltageMeter, "V", 1.5, "0.00"));
        animatedMetrics.Add(new(CpuFanText, CpuFanMeter, "RPM", 6_000));
        ApplyProfile("office");
        UpdateUnsavedChangesPresentation();
        UpdateFrequencyPowerStatePresentation();
        _ = LoadPowerPlansAsync();
        _ = LoadPersistedPresetsAsync();
    }

    private static void ConfigureSlider(Slider slider, double minimum, double maximum)
    {
        slider.Maximum = maximum;
        slider.Minimum = minimum;
    }

    public void AttachSession(HomeControlSession value)
    {
        session = value;
        if (value.State is HomeStateSnapshot snapshot)
            ApplyState(snapshot);
    }

    public void Shutdown()
    {
        isShutdown = true;
        MetricAnimationTimer.Stop();
        MetricAnimationTimer.Tick -= OnMetricAnimationTick;
        TelemetryRefreshTimer.Stop();
        TelemetryRefreshTimer.Tick -= OnTelemetryRefreshTick;
        SaveFeedbackTimer.Stop();
        SaveFeedbackTimer.Tick -= OnSaveFeedbackTimerTick;
        pendingTelemetry = null;
        telemetryRefreshRunning = false;
        saveFeedbackFade?.Stop();
        saveFeedbackFade = null;
    }

    public void ApplyState(HomeStateSnapshot snapshot)
    {
        if (isShutdown) return;
        ArgumentNullException.ThrowIfNull(snapshot);
        var cpuTuning = snapshot.Capabilities.Items.FirstOrDefault(item => item.Key == "cpuTuning");
        var curveOptimizer = snapshot.Capabilities.Items.FirstOrDefault(item => item.Key == "cpuTuning:curveOptimizer");
        var advancedSmu = snapshot.Capabilities.Items.FirstOrDefault(item => item.Key == "cpuTuning:smu");
        var advancedPbo = snapshot.Capabilities.Items.FirstOrDefault(item => item.Key == "cpuTuning:pbo");
        cpuTuningAvailable = cpuTuning?.State == CapabilityState.Available;
        curveOptimizerAvailable = curveOptimizer?.State == CapabilityState.Available;
        advancedSmuAvailable = advancedSmu?.State == CapabilityState.Available;
        advancedPboAvailable = advancedPbo?.State == CapabilityState.Available;
        PerformanceControlsPanel.IsEnabled = cpuTuningAvailable;
        BoostToggle.IsEnabled = cpuTuningAvailable;
        PowerPlanSelector.IsEnabled = cpuTuningAvailable;
        AdvancedCoreSelector.IsEnabled = cpuTuningAvailable && !applyInProgress;
        CurveOptimizerParameterRow.IsEnabled = cpuTuningAvailable && curveOptimizerAvailable;
        ModePresetPickerControl.IsEnabled = !applyInProgress;
        SavePresetButton.IsEnabled = !applyInProgress;
        UsePresetButton.IsEnabled = cpuTuningAvailable && !applyInProgress;
        SetAdvancedPanelState(AdvancedSmuControlsPanel, AdvancedTuningLayoutPreviewOnly || cpuTuningAvailable && advancedSmuAvailable && !applyInProgress);
        SetAdvancedPanelState(AdvancedPboControlsPanel, AdvancedTuningLayoutPreviewOnly || cpuTuningAvailable && advancedPboAvailable && !applyInProgress);

        pendingCpuTuningState = snapshot.Controls.CpuTuning;
        if (!localDraftChanged && !applyInProgress && pendingCpuTuningState is not null)
            ApplyLiveCpuTuning(pendingCpuTuningState);
    }

    public void ApplyTelemetry(HardwareSnapshot snapshot)
    {
        if (isShutdown) return;
        pendingTelemetry = snapshot;
        if (telemetryRefreshRunning)
            return;

        pendingTelemetry = null;
        ApplyTelemetryNow(snapshot);
        TelemetryRefreshTimer.Start();
        telemetryRefreshRunning = true;
    }

    private void OnTelemetryRefreshTick(DispatcherQueueTimer sender, object args)
    {
        if (isShutdown) return;
        if (pendingTelemetry is not HardwareSnapshot snapshot)
        {
            TelemetryRefreshTimer.Stop();
            telemetryRefreshRunning = false;
            return;
        }

        pendingTelemetry = null;
        ApplyTelemetryNow(snapshot);
    }

    private void ApplyTelemetryNow(HardwareSnapshot snapshot)
    {
        SetMetricTarget(animatedMetrics[0], snapshot.CpuTemperatureC);
        SetMetricTarget(animatedMetrics[1], snapshot.CpuPowerWatts is int power ? (double?)power : null);
        SetMetricTarget(animatedMetrics[2], snapshot.CpuUsagePercent);
        SetMetricTarget(animatedMetrics[3], snapshot.CpuFrequencyMhz is double frequency ? frequency / 1_000d : null);
        SetMetricTarget(animatedMetrics[4], snapshot.CpuVoltageVolts);
        SetMetricTarget(animatedMetrics[5], snapshot.CpuFanRpm);
        UpdateTelemetryStatuses(snapshot);
    }

    private void UpdateTelemetryStatuses(HardwareSnapshot snapshot)
    {
        if (snapshot.CpuTemperatureC is double temperature && double.IsFinite(temperature))
        {
            var highTemperature = temperature >= 90;
            CpuTemperatureStatusText.Text = highTemperature ? "温度过高" : "温度正常";
            CpuTemperatureStatusBadge.Background = (Brush)Application.Current.Resources["PrototypeControlAcrylicBrush"];
            CpuTemperatureStatusBadge.BorderBrush = (Brush)Resources[highTemperature ? "TemperatureStatusDangerTextBrush" : "TemperatureStatusNormalTextBrush"];
            CpuTemperatureStatusText.Foreground = (Brush)Resources[highTemperature ? "TemperatureStatusDangerTextBrush" : "TemperatureStatusNormalTextBrush"];
        }
        else
        {
            CpuTemperatureStatusText.Text = "等待数据";
            CpuTemperatureStatusBadge.Background = (Brush)Application.Current.Resources["PrototypeControlAcrylicBrush"];
            CpuTemperatureStatusBadge.BorderBrush = (Brush)Application.Current.Resources["PrototypeStrokeBrush"];
            CpuTemperatureStatusText.Foreground = (Brush)Resources["PrototypeSecondaryTextBrush"];
        }

        CpuPowerStatusText.Text = snapshot.CpuPowerWatts is int ? "功耗稳定" : "等待数据";
        CpuUsageStatusText.Text = snapshot.CpuUsagePercent is double usage && double.IsFinite(usage)
            ? usage > 75 ? "高负载" : usage < 20 ? "负载较低" : "负载适中"
            : "等待数据";
        CpuFrequencyStatusText.Text = snapshot.CpuFrequencyMhz is double frequency && double.IsFinite(frequency)
            ? frequency >= 3_800 ? "动态加速" : "频率稳定"
            : "等待数据";
        CpuVoltageStatusText.Text = snapshot.CpuVoltageVolts is double voltage && double.IsFinite(voltage)
            ? "实时回读"
            : "等待数据";
        CpuFanStatusText.Text = snapshot.CpuFanRpm is double fan && double.IsFinite(fan)
            ? fan > 0 ? "散热运行" : "风扇停转"
            : "等待数据";
    }

    private void SetMetricTarget(AnimatedMetric metric, double? value)
    {
        metric.Target = value is double actual && double.IsFinite(actual)
            ? Math.Clamp(actual, 0, metric.Maximum)
            : null;

        if (metric.Current is null)
            metric.Current = metric.Target;

        if (metric.Target is null)
            RenderMetric(metric);
        else
            MetricAnimationTimer.Start();
    }

    private void OnMetricAnimationTick(DispatcherQueueTimer sender, object args)
    {
        if (isShutdown) return;
        var stillAnimating = false;
        foreach (var metric in animatedMetrics)
        {
            if (metric.Target is not double target)
                continue;

            if (metric.Current is not double current)
            {
                metric.Current = target;
            }
            else
            {
                var delta = target - current;
                if (Math.Abs(delta) < 0.05)
                    metric.Current = target;
                else
                {
                    metric.Current = current + delta * 0.22;
                    stillAnimating = true;
                }
            }

            RenderMetric(metric);
        }

        if (!stillAnimating)
            MetricAnimationTimer.Stop();
    }

    private static void RenderMetric(AnimatedMetric metric)
    {
        metric.Text.Text = Format(metric.Current, metric.Unit, metric.Format);
        SetMeter(metric.Meter, metric.Current, metric.Maximum);
    }

    private static void SetMeter(ProgressBar meter, double? value, double maximum) =>
        meter.Value = value is double actual && double.IsFinite(actual) ? Math.Clamp(actual, 0, maximum) : 0;

    private static void AttachScaleMarker(Canvas scale, TrackAwareSlider slider, FrameworkElement marker, double minimum, double maximum, double recommended)
    {
        marker.Tag = recommended;
        void UpdateMarker() => PositionScaleMarker(scale, slider, marker, minimum, maximum);

        scale.SizeChanged += (_, _) => UpdateMarker();
        slider.SizeChanged += (_, _) => UpdateMarker();
        slider.TemplateReady += (_, _) =>
        {
            if (slider.GetHorizontalThumbElement() is FrameworkElement thumb)
                thumb.SizeChanged += (_, _) => UpdateMarker();
            UpdateMarker();
        };
        marker.SizeChanged += (_, _) => UpdateMarker();
        UpdateMarker();
    }

    private static void PositionScaleMarker(Canvas scale, TrackAwareSlider slider, FrameworkElement marker, double minimum, double maximum)
    {
        var track = slider.GetHorizontalTrackElement();
        if (track is null || track.ActualWidth <= 0 || track.ActualHeight <= 0 || marker.ActualWidth <= 0 || marker.ActualHeight <= 0)
            return;

        var recommended = marker.Tag is double value ? value : minimum;
        var stepFrequency = slider.StepFrequency > 0 ? slider.StepFrequency : 1;
        var stepCount = Math.Max(1, (int)Math.Round((maximum - minimum) / stepFrequency));
        var valueIndex = Math.Clamp((int)Math.Round((recommended - minimum) / stepFrequency), 0, stepCount);
        var valueRatio = (double)valueIndex / stepCount;
        var thumb = slider.GetHorizontalThumbElement();
        var thumbWidth = thumb?.ActualWidth > 0 ? thumb.ActualWidth : 0;
        var trackOrigin = track.TransformToVisual(scale).TransformPoint(new Point(0, 0));
        var travelWidth = Math.Max(0, track.ActualWidth - thumbWidth);
        var markerCenter = trackOrigin.X + (thumbWidth / 2) + (travelWidth * valueRatio);
        Canvas.SetLeft(marker, markerCenter - (marker.ActualWidth / 2));
        Canvas.SetTop(marker, trackOrigin.Y + (track.ActualHeight / 2) - (marker.ActualHeight / 2));
    }

    private static void SetScaleMarker(Canvas scale, TrackAwareSlider slider, FrameworkElement marker, double minimum, double maximum, double recommendation)
    {
        marker.Tag = recommendation;
        PositionScaleMarker(scale, slider, marker, minimum, maximum);
    }

    private void OnProfileMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (!synchronizingValues && sender is MenuFlyoutItem { Tag: string profile })
        {
            selectedPresetIndex = 0;
            ApplyProfile(profile);
            UpdateProfileSelectorPresentation(profile);
            if (hardwareStateApplied)
            {
                localDraftChanged = true;
                UpdateUnsavedChangesPresentation();
            }
        }
    }

    private void OnModePresetSelected(object? sender, PresetKey key)
    {
        if (synchronizingValues)
            return;

        selectedProfile = ToProfile(key.Mode);
        selectedPresetIndex = key.Slot - 1;
        ApplyProfile(selectedProfile);
        if (hardwareStateApplied)
        {
            localDraftChanged = true;
            UpdateUnsavedChangesPresentation();
        }
    }

    private void OnPresetMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (synchronizingValues || sender is not MenuFlyoutItem { Tag: string tag } || !int.TryParse(tag, out var index))
            return;

        selectedPresetIndex = Math.Clamp(index, 0, 2);
        ApplyProfile(selectedProfile);
        ModePresetPickerControl.SelectedKey = ToPresetKey(selectedProfile, selectedPresetIndex);
        if (hardwareStateApplied)
        {
            localDraftChanged = true;
            UpdateUnsavedChangesPresentation();
        }
    }

    private void UpdateProfileSelectorPresentation(string profile)
    {
        ResetSaveFeedback();
    }

    private static string ProfileDisplayName(string profile) => profile switch
    {
        "office" => "办公",
        "game" => "游戏",
        "turbo" => "狂飙",
        "custom-1" => "自定义一",
        "custom-2" => "自定义二",
        "custom-3" => "自定义三",
        _ => "当前模式"
    };

    private static string ToProfile(ControlModeId mode) => mode switch
    {
        ControlModeId.Office => "office",
        ControlModeId.Gaming => "game",
        ControlModeId.Turbo => "turbo",
        ControlModeId.Custom1 => "custom-1",
        ControlModeId.Custom2 => "custom-2",
        ControlModeId.Custom3 => "custom-3",
        _ => "office"
    };

    private static PresetKey ToPresetKey(string profile, int presetIndex) =>
        PresetKey.Create(profile switch
        {
            "game" => ControlModeId.Gaming,
            "turbo" => ControlModeId.Turbo,
            "custom-1" => ControlModeId.Custom1,
            "custom-2" => ControlModeId.Custom2,
            "custom-3" => ControlModeId.Custom3,
            _ => ControlModeId.Office
        }, Math.Clamp(presetIndex, 0, 2) + 1);

    private void ApplyProfile(string profile)
    {
        if (!defaults.TryGetValue(profile, out var presets) || presets.Length == 0) return;
        selectedProfile = profile;
        selectedPresetIndex = Math.Clamp(selectedPresetIndex, 0, presets.Length - 1);
        var draft = presets[selectedPresetIndex];

        synchronizingValues = true;
        try
        {
            ModePresetPickerControl.SelectedKey = ToPresetKey(selectedProfile, selectedPresetIndex);
            TemperatureParameterRow.Value = draft.TemperatureLimitC;
            SplParameterRow.Value = draft.SplWatts;
            SpptParameterRow.Value = draft.SpptWatts;
            editingAcMaxFrequencyMhz = draft.AcMaxFrequencyMhz;
            editingDcMaxFrequencyMhz = draft.DcMaxFrequencyMhz;
            SetFrequencyInputValue(editingAcFrequency ? editingAcMaxFrequencyMhz : editingDcMaxFrequencyMhz);
            BoostToggle.IsOn = draft.IsBoostEnabled;
            var curve = draft.NegativeCurveOptimizer ?? -15;
            CurveOptimizerParameterRow.Value = curve;
            ApplyAdvancedCpuTuning(draft.AdvancedCpuTuning);
            UpdateBoostStateText();
            UpdateProfileRecommendationMarkers(profile);
            UpdateFrequencyPowerStatePresentation();
        }
        finally
        {
            synchronizingValues = false;
        }

    }

    private void OnTemperatureValueChanged(object? sender, double value)
    {
        if (!synchronizingValues)
            MarkPresetChanged();
    }
    private void OnSplValueChanged(object? sender, double value) => MarkPresetChanged();
    private void OnSpptValueChanged(object? sender, double value) => MarkPresetChanged();

    private async void OnApplyTemperatureLimitClick(object sender, RoutedEventArgs e) =>
        await ApplyConfirmedOemTemperatureLimitAsync(TemperatureParameterRow.Value);

    private async Task ApplyConfirmedOemTemperatureLimitAsync(double rawValue)
    {
        if (session?.State is not { } state || !cpuTuningAvailable || applyInProgress ||
            state.Capabilities.SupportState != DeviceSupportState.Ready ||
            state.Telemetry?.AcPowerConnected != true || !double.IsFinite(rawValue) || rawValue != Math.Truncate(rawValue))
        {
            ShowSaveFeedback("未写入", "需要可用的 CPU 调校、交流供电和整数目标值。");
            return;
        }

        int value = checked((int)rawValue);
        const int minimum = 45;
        const int maximum = 100;
        if (value < minimum || value > maximum)
        {
            ShowSaveFeedback("未写入", $"目标超出此项范围：{minimum}–{maximum}。");
            return;
        }

        var plan = new CpuTuningPlan(value, null, null, null, null, null, null, null);

        applyInProgress = true;
        SetOemLimitButtonsEnabled(false);
        try
        {
            var result = await session.ExecuteAsync(
                new SetCpuTuningCommand(Guid.NewGuid(), plan, RiskConfirmed: true), CancellationToken.None);
            ShowSaveFeedback(
                result.State == CommandState.Applied && result.Error is null ? "已发送，未读回" : "写入失败",
                result.State == CommandState.Applied && result.Error is null
                    ? "OEM 设置调用已返回成功；硬件未读回确认，此目标不会参与自动恢复。"
                    : "服务未确认写入；没有自动回放未知旧值。");
        }
        catch
        {
            ShowSaveFeedback("写入失败", "服务调用失败；没有自动回放未知旧值。");
        }
        finally
        {
            applyInProgress = false;
            SetOemLimitButtonsEnabled(cpuTuningAvailable);
        }
    }

    private void SetOemLimitButtonsEnabled(bool enabled)
    {
        TemperatureParameterRow.IsEnabled = enabled;
        SplParameterRow.IsEnabled = enabled;
        SpptParameterRow.IsEnabled = enabled;
    }

    private void OnFrequencyValueChanged(object? sender, double value)
    {
        if (!synchronizingValues && double.IsFinite(value))
        {
            SetEditingFrequency((int)Math.Round(value));
            MarkPresetChanged();
        }
    }
    private void OnCurveValueChanged(object? sender, double value) => MarkPresetChanged();
    private void OnFrequencyAcClick(object sender, RoutedEventArgs e) => SelectFrequencyPowerState(ac: true);

    private void OnFrequencyDcClick(object sender, RoutedEventArgs e) => SelectFrequencyPowerState(ac: false);

    private void SelectFrequencyPowerState(bool ac)
    {
        CaptureEditingFrequency();
        editingAcFrequency = ac;
        SetFrequencyInputValue(ac ? editingAcMaxFrequencyMhz : editingDcMaxFrequencyMhz);
        UpdateFrequencyPowerStatePresentation();
    }

    private void CaptureEditingFrequency()
    {
        if (double.IsFinite(FrequencyParameterRow.Value))
            SetEditingFrequency((int)Math.Round(FrequencyParameterRow.Value));
    }

    private void SetEditingFrequency(int value)
    {
        if (editingAcFrequency) editingAcMaxFrequencyMhz = value;
        else editingDcMaxFrequencyMhz = value;
    }

    private void SetFrequencyInputValue(int value)
    {
        var wasSynchronizing = synchronizingValues;
        synchronizingValues = true;
        try
        {
            FrequencyParameterRow.Value = value;
        }
        finally
        {
            synchronizingValues = wasSynchronizing;
        }
    }

    private void UpdateFrequencyPowerStatePresentation()
    {
        var glassBackground = (Brush)Application.Current.Resources["PrototypeControlAcrylicBrush"];
        var activeForeground = (Brush)Application.Current.Resources["PrototypeTextBrush"];
        var inactiveForeground = (Brush)Application.Current.Resources["PrototypeSecondaryTextBrush"];
        var activeBorder = (Brush)Application.Current.Resources["ModeAccentBrush"];
        var glassBorder = (Brush)Application.Current.Resources["PrototypeStrokeBrush"];
        FrequencyAcButton.Background = glassBackground;
        FrequencyAcButton.BorderBrush = editingAcFrequency ? activeBorder : glassBorder;
        FrequencyAcButton.Foreground = editingAcFrequency ? activeForeground : inactiveForeground;
        FrequencyDcButton.Background = glassBackground;
        FrequencyDcButton.BorderBrush = editingAcFrequency ? glassBorder : activeBorder;
        FrequencyDcButton.Foreground = editingAcFrequency ? inactiveForeground : activeForeground;
    }
    private void OnAdvancedNumberBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (synchronizingValues) return;

        var core = SelectedAdvancedCore();
        if (sender == PerCoreCurveOptimizerBox)
        {
            if (TryReadInteger(sender, out var value)) perCoreCurveOptimizerDraft[core] = value;
            else perCoreCurveOptimizerDraft.Remove(core);
        }

        MarkPresetChanged();
    }

    private void OnAdvancedCoreMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (synchronizingValues || sender is not MenuFlyoutItem { Tag: string tag } || !int.TryParse(tag, out var core))
            return;

        synchronizingValues = true;
        try
        {
            selectedAdvancedCore = Math.Clamp(core, 0, 15);
            AdvancedCoreSelector.Content = $"C{selectedAdvancedCore}";
            ApplySelectedCoreValues();
        }
        finally
        {
            synchronizingValues = false;
        }
    }

    private AdvancedCpuTuningDraft? ReadAdvancedCpuTuning()
    {
        if (AdvancedTuningLayoutPreviewOnly)
            return null;

        var hasScalar = new[]
        {
            StapmBox, FastPptBox, SlowPptBox, PptBox, VrmBox, TdcBox, EdcBox,
            Mp1Box, RsmuBox, PboScalarBox, CurveOptimizerAllBox
        }.Any(box => double.IsFinite(box.Value));

        if (!hasScalar && perCoreCurveOptimizerDraft.Count == 0) return null;
        return new AdvancedCpuTuningDraft
        {
            StapmWatts = ReadOptionalDouble(StapmBox),
            FastPptWatts = ReadOptionalDouble(FastPptBox),
            SlowPptWatts = ReadOptionalDouble(SlowPptBox),
            PptWatts = ReadOptionalDouble(PptBox),
            VrmCurrentMilliamps = ReadOptionalInteger(VrmBox),
            TdcCurrentMilliamps = ReadOptionalInteger(TdcBox),
            EdcCurrentMilliamps = ReadOptionalInteger(EdcBox),
            Mp1TemperatureC = ReadOptionalInteger(Mp1Box),
            RsmuTemperatureC = ReadOptionalInteger(RsmuBox),
            PboScalar = ReadOptionalInteger(PboScalarBox),
            CurveOptimizerAll = ReadOptionalInteger(CurveOptimizerAllBox),
            PerCoreCurveOptimizer = perCoreCurveOptimizerDraft.Count == 0 ? null : new Dictionary<int, int>(perCoreCurveOptimizerDraft)
        };
    }

    private void ApplyAdvancedCpuTuning(AdvancedCpuTuningDraft? tuning)
    {
        var effectiveTuning = tuning ?? new AdvancedCpuTuningDraft
        {
            StapmWatts = 55,
            FastPptWatts = 65,
            SlowPptWatts = 45,
            PptWatts = 65,
            VrmCurrentMilliamps = 120_000,
            TdcCurrentMilliamps = 90_000,
            EdcCurrentMilliamps = 140_000,
            Mp1TemperatureC = 95,
            RsmuTemperatureC = 100,
            PboScalar = 1,
            CurveOptimizerAll = -10,
            PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -10 }
        };

        perCoreCurveOptimizerDraft.Clear();
        if (effectiveTuning.PerCoreCurveOptimizer is not null)
            foreach (var pair in effectiveTuning.PerCoreCurveOptimizer) perCoreCurveOptimizerDraft[pair.Key] = pair.Value;

        SetOptionalNumberBox(StapmBox, effectiveTuning.StapmWatts);
        SetOptionalNumberBox(FastPptBox, effectiveTuning.FastPptWatts);
        SetOptionalNumberBox(SlowPptBox, effectiveTuning.SlowPptWatts);
        SetOptionalNumberBox(PptBox, effectiveTuning.PptWatts);
        SetOptionalNumberBox(VrmBox, effectiveTuning.VrmCurrentMilliamps);
        SetOptionalNumberBox(TdcBox, effectiveTuning.TdcCurrentMilliamps);
        SetOptionalNumberBox(EdcBox, effectiveTuning.EdcCurrentMilliamps);
        SetOptionalNumberBox(Mp1Box, effectiveTuning.Mp1TemperatureC);
        SetOptionalNumberBox(RsmuBox, effectiveTuning.RsmuTemperatureC);
        SetOptionalNumberBox(PboScalarBox, effectiveTuning.PboScalar);
        SetOptionalNumberBox(CurveOptimizerAllBox, effectiveTuning.CurveOptimizerAll);
        ApplySelectedCoreValues();
    }

    private void ApplySelectedCoreValues()
    {
        var core = SelectedAdvancedCore();
        SetOptionalNumberBox(PerCoreCurveOptimizerBox, perCoreCurveOptimizerDraft.TryGetValue(core, out var curve) ? curve : null);
    }

    private int SelectedAdvancedCore() => selectedAdvancedCore;

    private static double? ReadOptionalDouble(NumberBox box) => double.IsFinite(box.Value) ? box.Value : null;

    private static int? ReadOptionalInteger(NumberBox box) => TryReadInteger(box, out var value) ? value : null;

    private static bool TryReadInteger(NumberBox box, out int value)
    {
        if (double.IsFinite(box.Value) && box.Value >= int.MinValue && box.Value <= int.MaxValue)
        {
            value = (int)Math.Round(box.Value);
            return true;
        }

        value = 0;
        return false;
    }

    private static void SetOptionalNumberBox(NumberBox box, double? value) => box.Value = value is double actual ? actual : double.NaN;

    private static void SetAdvancedPanelState(FrameworkElement panel, bool enabled)
    {
        panel.Opacity = enabled ? 1 : 0.48;
        panel.IsHitTestVisible = enabled;
    }

    private static PerformanceDraft[] CreateDefaultPresets(string profile)
    {
        var baseline = CreateDefaultDraft(profile);
        return
        [
            baseline,
            baseline with
            {
                TemperatureLimitC = Math.Max(45, baseline.TemperatureLimitC - 10),
                SplWatts = Math.Max(20, baseline.SplWatts - 10),
                SpptWatts = Math.Max(20, baseline.SpptWatts - 10),
                MaxFrequencyMhz = Math.Max(1_500, baseline.MaxFrequencyMhz - 300),
                IsBoostEnabled = false
            },
            baseline with
            {
                TemperatureLimitC = Math.Min(100, baseline.TemperatureLimitC + 5),
                SplWatts = Math.Min(105, baseline.SplWatts + 10),
                SpptWatts = Math.Min(120, baseline.SpptWatts + 10),
                MaxFrequencyMhz = Math.Min(5_400, baseline.MaxFrequencyMhz + 300),
                IsBoostEnabled = true
            }
        ];
    }

    private void UpdateProfileRecommendationMarkers(string profile)
    {
        var recommendation = GetProfileRecommendation(profile);
        TemperatureParameterRow.SetRecommendedValue(recommendation.TemperatureLimitC);
        SpptParameterRow.SetRecommendedValue(recommendation.SpptWatts);
        SplParameterRow.SetRecommendedValue(recommendation.SplWatts);
        FrequencyParameterRow.SetRecommendedValue(recommendation.MaxFrequencyMhz);
    }

    private static PerformanceDraft GetProfileRecommendation(string profile) => profile switch
    {
        "game" or "custom-2" => new() { TemperatureLimitC = 85, SplWatts = 70, SpptWatts = 55, MaxFrequencyMhz = 4_700 },
        "turbo" or "custom-3" => new() { TemperatureLimitC = 95, SplWatts = 95, SpptWatts = 75, MaxFrequencyMhz = 5_000 },
        _ => new() { TemperatureLimitC = 75, SplWatts = 35, SpptWatts = 45, MaxFrequencyMhz = 3_800 }
    };

    private static PerformanceDraft CreateDefaultDraft(string profile) => profile switch
    {
        "game" or "custom-2" => new() { TemperatureLimitC = 85, SplWatts = 70, SpptWatts = 55, MaxFrequencyMhz = 4_700, IsBoostEnabled = true },
        "turbo" or "custom-3" => new() { TemperatureLimitC = 95, SplWatts = 95, SpptWatts = 75, MaxFrequencyMhz = 5_000, IsBoostEnabled = true },
        _ => new() { TemperatureLimitC = 75, SplWatts = 35, SpptWatts = 45, MaxFrequencyMhz = 3_800, IsBoostEnabled = false }
    };

    private void UpdateDraftFromSlider(NumberBox target, double value)
    {
        var wasSynchronizing = synchronizingValues;
        SetNumberBox(target, value);
        if (!wasSynchronizing) MarkPresetChanged();
    }

    private void UpdateDraftFromNumberBox(Slider target, double value)
    {
        var wasSynchronizing = synchronizingValues;
        SetSlider(target, value);
        if (!wasSynchronizing) MarkPresetChanged();
    }

    private void MarkPresetChanged()
    {
        if (synchronizingValues || SaveFeedbackTimer is null) return;

        localDraftChanged = true;
        UpdateUnsavedChangesPresentation();
        ResetSaveFeedback();
    }

    private void UpdateUnsavedChangesPresentation()
    {
        var visibility = localDraftChanged ? Visibility.Visible : Visibility.Collapsed;
        UnsavedChangesIndicator.Visibility = visibility;
        AdvancedUnsavedText.Visibility = visibility;
    }

    private void OnNumberBoxPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not NumberBox box) return;

        var delta = e.GetCurrentPoint(box).Properties.MouseWheelDelta;
        if (delta == 0 || !double.IsFinite(box.Value)) return;

        var step = box.SmallChange > 0 ? box.SmallChange : 1;
        var direction = delta > 0 ? 1 : -1;
        box.Value = Math.Clamp(box.Value + direction * step, box.Minimum, box.Maximum);
        e.Handled = true;
    }

    private void OnNumberBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not NumberBox numberBox) return;

        CenterNumberBoxInput(numberBox);
        numberBox.DispatcherQueue.TryEnqueue(() => CenterNumberBoxInput(numberBox));
    }

    private static void CenterNumberBoxInput(NumberBox numberBox)
    {
        numberBox.HorizontalContentAlignment = HorizontalAlignment.Center;
        numberBox.VerticalContentAlignment = VerticalAlignment.Center;

        if (FindDescendant<TextBox>(numberBox) is not { } inputBox) return;

        inputBox.TextAlignment = TextAlignment.Center;
        inputBox.HorizontalContentAlignment = HorizontalAlignment.Center;
        inputBox.VerticalContentAlignment = VerticalAlignment.Center;
        inputBox.Padding = new Thickness(0);
        inputBox.VerticalAlignment = VerticalAlignment.Center;

        if (FindDescendant<ScrollViewer>(inputBox) is not { } inputScrollViewer) return;

        inputScrollViewer.HorizontalContentAlignment = HorizontalAlignment.Center;
        inputScrollViewer.VerticalContentAlignment = VerticalAlignment.Center;
        inputScrollViewer.Padding = new Thickness(0);
    }

    private void OnPerformanceContentPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<NumberBox>(source) is not null ||
            FindAncestor<TextBox>(source) is not null ||
            FindAncestor<ButtonBase>(source) is not null ||
            FindAncestor<ToggleSwitch>(source) is not null ||
            FindAncestor<Slider>(source) is not null ||
            FindAncestor<ComboBox>(source) is not null)
            return;

        PerformanceContentScrollViewer.Focus(FocusState.Programmatic);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }

        return null;
    }

    private void OnBoostToggled(object sender, RoutedEventArgs e)
    {
        UpdateBoostStateText();
        MarkPresetChanged();
    }

    private void UpdateBoostStateText() => BoostStateText.Text = BoostToggle.IsOn ? "已启用 · 性能优先" : "未启用 · 节能优先";

    private void SetNumberBox(NumberBox target, double value)
    {
        if (synchronizingValues || !double.IsFinite(value)) return;
        synchronizingValues = true;
        try { target.Value = Math.Clamp(value, target.Minimum, target.Maximum); }
        finally { synchronizingValues = false; }
    }

    private void SetSlider(Slider target, double value)
    {
        if (synchronizingValues || !double.IsFinite(value)) return;
        synchronizingValues = true;
        try { target.Value = Math.Clamp(value, target.Minimum, target.Maximum); }
        finally { synchronizingValues = false; }
    }

    private void OnPowerPlanMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (!loadingPowerPlans && !synchronizingValues && sender is MenuFlyoutItem { Tag: Guid id } item)
        {
            selectedPowerPlanId = id;
            PowerPlanSelector.Content = item.Text;
            MarkPresetChanged();
        }
    }

    private async Task LoadPersistedPresetsAsync()
    {
        try
        {
            var stored = await presetStore.LoadAsync(CancellationToken.None);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (isShutdown) return;
                foreach (var profile in defaults.Keys.ToArray())
                {
                    if (stored.TryGetValue(profile, out var loaded) && loaded.Count > 0)
                    {
                        var builtIn = defaults[profile];
                        defaults[profile] = Enumerable.Range(0, 3)
                            .Select(index => index < loaded.Count ? loaded[index] : builtIn[index])
                            .ToArray();
                    }
                }

                 if (!hardwareStateApplied && !localDraftChanged)
                     ApplyProfile(selectedProfile);
            });
        }
        catch
        {
            // Missing or invalid local preset data leaves the built-in defaults intact.
        }
    }

    private async Task LoadPowerPlansAsync()
    {
        try
        {
            var plans = await ReadPowerPlanListAsync();
            DispatcherQueue.TryEnqueue(() => ApplyPowerPlans(plans));
        }
        catch
        {
            DispatcherQueue.TryEnqueue(ApplyPowerPlanReadFailure);
        }
    }

    private void ApplyPowerPlans(IReadOnlyList<(Guid Id, string Name)> plans)
    {
        if (isShutdown) return;
        loadingPowerPlans = true;
        try
        {
            if (PowerPlanSelector.Flyout is not MenuFlyout menu)
                return;

            menu.Items.Clear();
            if (plans.Count == 0)
            {
                ApplyPowerPlanReadFailure();
                return;
            }

            foreach (var plan in plans)
            {
                var item = new MenuFlyoutItem { Text = plan.Name, Tag = plan.Id };
                item.Click += OnPowerPlanMenuItemClick;
                menu.Items.Add(item);
            }

            selectedPowerPlanId = plans[0].Id;
            PowerPlanSelector.Content = plans[0].Name;
            if (!localDraftChanged && !applyInProgress && pendingCpuTuningState is not null)
                ApplyLiveCpuTuning(pendingCpuTuningState);
        }
        finally
        {
            loadingPowerPlans = false;
        }
    }

    private void ApplyPowerPlanReadFailure()
    {
        if (isShutdown) return;
        loadingPowerPlans = true;
        try
        {
            selectedPowerPlanId = Guid.Empty;
            PowerPlanSelector.Content = "无法读取当前电源选项";
            if (PowerPlanSelector.Flyout is MenuFlyout menu)
            {
                menu.Items.Clear();
                menu.Items.Add(new MenuFlyoutItem { Text = "无法读取当前电源选项", IsEnabled = false });
            }
        }
        finally
        {
            loadingPowerPlans = false;
        }
    }

    private static async Task<IReadOnlyList<(Guid Id, string Name)>> ReadPowerPlanListAsync()
    {
        var listed = await RunPowerCfgAsync("/list");
        var plans = ParsePowerPlans(listed.Output).ToList();
        var knownIds = plans.Select(plan => plan.Id).ToHashSet();

        foreach (var candidate in PowerPlanDiscoveryCandidates)
        {
            if (knownIds.Contains(candidate.Id)) continue;
            var query = await RunPowerCfgAsync($"/query {candidate.Id:D}");
            if (query.ExitCode != 0 || !query.Output.Contains(candidate.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                continue;

            plans.Add(candidate);
            knownIds.Add(candidate.Id);
        }

        return plans;
    }

    private static async Task<(int ExitCode, string Output)> RunPowerCfgAsync(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }

    private static IEnumerable<(Guid Id, string Name)> ParsePowerPlans(string output)
    {
        const string pattern = @"(?<id>[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})[^\r\n]*\((?<name>[^)\r\n]+)\)";
        foreach (Match match in Regex.Matches(output, pattern))
        {
            if (!Guid.TryParse(match.Groups["id"].Value, out var id)) continue;
            var name = LocalizePowerPlanName(match.Groups["name"].Value.Trim());
            if (name.Length > 0) yield return (id, name);
        }
    }

    private static string LocalizePowerPlanName(string name) => name switch
    {
        "Balanced" => "平衡",
        "High performance" => "高性能",
        "Power saver" => "节能",
        "Ultimate Performance" => "卓越性能",
        _ => name
    };

    private async void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (applyInProgress)
        {
            ShowSaveFeedback("保存失败", "当前正在应用设置");
            return;
        }

        try
        {
            var saved = await SaveDraftAsync(ModePresetPickerControl.SelectedKey, CancellationToken.None);
            if (saved)
                ShowSaveFeedback("已保存", $"已保存“{SelectedProfileName()} · 预设 {selectedPresetIndex + 1}”，尚未写入硬件");
        }
        catch (OperationCanceledException)
        {
            ShowSaveFeedback("保存失败", "保存已取消，未写入硬件");
        }
        catch
        {
            ShowSaveFeedback("保存失败", "预设文件未完成更新");
        }
    }

    private async Task<bool> SaveDraftAsync(PresetKey key, CancellationToken cancellationToken)
    {
        var profile = ToProfile(key.Mode);
        var presetIndex = key.Slot - 1;
        var draft = ReadDraft();
        var validation = PerformanceDraftValidator.Validate(draft);
        if (!validation.IsValid)
        {
            ShowSaveFeedback("保存失败", validation.Errors[0]);
            return false;
        }

        defaults[profile][presetIndex] = draft;
        await presetStore.SaveAsync(PresetsForStorage(), cancellationToken);
        selectedProfile = profile;
        selectedPresetIndex = presetIndex;
        localDraftChanged = false;
        UpdateUnsavedChangesPresentation();
        return true;
    }

    private async void OnRestorePresetClick(object sender, RoutedEventArgs e)
    {
        if (session is null || !cpuTuningAvailable || applyInProgress)
            return;

        if (XamlRoot is null)
            return;

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "还原并应用默认设置",
            Content = "将当前模式恢复为默认预设，并立即写入硬件。",
            PrimaryButtonText = "还原并应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            return;

        var draft = CreateDefaultPresets(selectedProfile)[Math.Clamp(selectedPresetIndex, 0, 2)];
        try
        {
            var result = await ApplyDraftToHardwareAsync(draft);
            if (result.State != CommandState.Applied)
                return;

            defaults[selectedProfile][selectedPresetIndex] = draft;
            await presetStore.SaveAsync(PresetsForStorage(), CancellationToken.None);
            ApplyProfile(selectedProfile);
            localDraftChanged = false;
            UpdateUnsavedChangesPresentation();
            ShowSaveFeedback("还原成功", $"已还原“{SelectedProfileName()}”的预设 {selectedPresetIndex + 1}");
        }
        catch
        {
        }
        finally
        {
            if (session.State is HomeStateSnapshot snapshot)
                ApplyState(snapshot);
        }
    }

    private async Task<CommandResult> ApplyDraftToHardwareAsync(PerformanceDraft draft)
    {
        applyInProgress = true;
        ModePresetPickerControl.IsEnabled = false;
        SavePresetButton.IsEnabled = false;
        UsePresetButton.IsEnabled = false;
        PowerPlanSelector.IsEnabled = false;
        AdvancedCoreSelector.IsEnabled = false;
        try
        {
            return await session!.ExecuteAsync(
                PerformanceCommandFactory.Create(draft, riskConfirmed: true),
                CancellationToken.None);
        }
        finally
        {
            applyInProgress = false;
        }
    }

    private void ShowSaveFeedback(string content, string toolTip)
    {
        if (isShutdown) return;
        saveFeedbackFade?.Stop();
        saveFeedbackFade = null;
        SaveFeedbackTimer.Stop();
        PresetFeedbackText.Opacity = 1;
        PresetFeedbackText.Text = content;
        ToolTipService.SetToolTip(PresetFeedbackText, toolTip);
        SaveFeedbackTimer.Start();
    }

    private void OnSaveFeedbackTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (isShutdown) return;
        SaveFeedbackTimer.Stop();
        saveFeedbackFade?.Stop();
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260))
        };
        Storyboard.SetTarget(fade, PresetFeedbackText);
        Storyboard.SetTargetProperty(fade, "Opacity");
        saveFeedbackFade = new Storyboard();
        saveFeedbackFade.Children.Add(fade);
        saveFeedbackFade.Completed += (_, _) =>
        {
            if (isShutdown) return;
            PresetFeedbackText.Text = string.Empty;
            PresetFeedbackText.Opacity = 1;
            saveFeedbackFade = null;
        };
        saveFeedbackFade.Begin();
    }

    private void ResetSaveFeedback()
    {
        SaveFeedbackTimer?.Stop();
        saveFeedbackFade?.Stop();
        saveFeedbackFade = null;
        PresetFeedbackText.Opacity = 1;
        PresetFeedbackText.Text = string.Empty;
    }

    private async void OnUsePresetClick(object sender, RoutedEventArgs e)
    {
        if (session is null || !cpuTuningAvailable || applyInProgress)
        {
            ShowSaveFeedback("使用失败", "当前硬件调节不可用");
            return;
        }

        if (!defaults.TryGetValue(selectedProfile, out var presets) || presets.Length == 0)
            return;

        var draft = presets[Math.Clamp(selectedPresetIndex, 0, presets.Length - 1)];
        var validation = PerformanceDraftValidator.Validate(draft);
        if (!validation.IsValid)
        {
            ShowSaveFeedback("使用失败", validation.Errors[0]);
            return;
        }

        try
        {
            var result = await ApplyDraftToHardwareAsync(draft);
            if (result.State != CommandState.Applied)
            {
                ShowSaveFeedback("使用失败", PerformanceCommandOutcome.Describe(result));
                return;
            }

            ApplyProfile(selectedProfile);
            localDraftChanged = false;
            UpdateUnsavedChangesPresentation();
            ShowSaveFeedback("使用成功", $"已应用“{SelectedProfileName()}”的预设 {selectedPresetIndex + 1}");
        }
        catch
        {
            ShowSaveFeedback("使用失败", "硬件未完成更新");
        }
        finally
        {
            if (session.State is HomeStateSnapshot snapshot)
                ApplyState(snapshot);
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<PerformanceDraft>> PresetsForStorage() =>
        defaults.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<PerformanceDraft>)pair.Value, StringComparer.Ordinal);

    private PerformanceDraft ReadDraft() => new()
    {
        TemperatureLimitC = (int)TemperatureParameterRow.Value,
        SplWatts = (int)SplParameterRow.Value,
        SpptWatts = (int)SpptParameterRow.Value,
        MaxFrequencyMhz = editingAcMaxFrequencyMhz,
        AcMaxFrequencyMhz = editingAcMaxFrequencyMhz,
        DcMaxFrequencyMhz = editingDcMaxFrequencyMhz,
        IsBoostEnabled = BoostToggle.IsOn,
        NegativeCurveOptimizer = curveOptimizerAvailable ? (int)CurveOptimizerParameterRow.Value : null,
        AdvancedCpuTuning = ReadAdvancedCpuTuning(),
        WindowsPowerSchemeId = selectedPowerPlanId
    };

    private void ApplyLiveCpuTuning(CpuTuningState state)
    {
        synchronizingValues = true;
        try
        {
            if (state.TemperatureLimitC is int temperature)
                TemperatureParameterRow.Value = temperature;
            if (state.SplWatts is int spl)
                SplParameterRow.Value = spl;
            if (state.SpptWatts is int sppt)
                SpptParameterRow.Value = sppt;
            var acFrequency = state.AcMaxFrequencyMhz ?? state.MaxFrequencyMhz;
            var dcFrequency = state.DcMaxFrequencyMhz ?? state.MaxFrequencyMhz;
            if (acFrequency is int ac)
                editingAcMaxFrequencyMhz = ac;
            if (dcFrequency is int dc)
                editingDcMaxFrequencyMhz = dc;
            if (acFrequency is not null || dcFrequency is not null)
            {
                FrequencyParameterRow.Value = editingAcFrequency
                    ? editingAcMaxFrequencyMhz
                    : editingDcMaxFrequencyMhz;
                UpdateFrequencyPowerStatePresentation();
            }
            if (state.BoostEnabled is bool boost)
                BoostToggle.IsOn = boost;
            if (state.NegativeCurveOptimizer is int curve)
            {
                CurveOptimizerParameterRow.Value = curve;
            }

            AvailableCoreCountText.Text = state.EnabledCoreCount is int cores
                ? cores.ToString()
                : "不可用";

            if (state.WindowsPowerSchemeId is Guid scheme)
            {
                if (PowerPlanSelector.Flyout is MenuFlyout menu)
                {
                    var item = menu.Items.OfType<MenuFlyoutItem>().FirstOrDefault(candidate => candidate.Tag is Guid id && id == scheme);
                    if (item is not null)
                    {
                        selectedPowerPlanId = scheme;
                        PowerPlanSelector.Content = item.Text;
                    }
                }
            }

            UpdateBoostStateText();
            hardwareStateApplied = true;
        }
        finally
        {
            synchronizingValues = false;
        }
    }

    private string SelectedProfileName() => ProfileDisplayName(selectedProfile);

    private static string Format(double? value, string unit, string format = "0") =>
        value is double actual && double.IsFinite(actual) ? $"{actual.ToString(format)} {unit}" : $"-- {unit}";

    private static string Format(int? value, string unit) =>
        value is int actual ? $"{actual} {unit}" : $"-- {unit}";
}

public sealed class TrackAwareSlider : Slider
{
    public event EventHandler? TemplateReady;

    public FrameworkElement? GetHorizontalTrackElement() =>
        GetTemplateChild("HorizontalTrackRect") as FrameworkElement;

    public FrameworkElement? GetHorizontalThumbElement() =>
        GetTemplateChild("HorizontalThumb") as FrameworkElement;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        TemplateReady?.Invoke(this, EventArgs.Empty);
    }
}
