using System.Diagnostics;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using Jiaolong_ControlCenter.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2 : UserControl
{
    private readonly ControlPresetStore presetStore = new();
    private PathGeometry? usageGeometry;
    private ArcSegment? usageArc;
    private bool gpuVfDirty;
    private bool gpuVfPending => GpuWritePending;
    private bool memorySynchronizing;
    private bool memoryPending => GpuWritePending;
    private bool memoryReady;
    private int? memoryAppliedKhz;
    private bool coreOffsetPending => GpuWritePending;
    private bool coreOffsetReady;
    private bool coreOffsetSynchronizing;
    private bool coreOffsetDirty;
    private int? coreOffsetAppliedKhz;
    private GpuVfState? lastGpuVf;
    private string? displayedLimitReason;
    private string? pendingLimitReason;
    private DateTime pendingLimitSinceUtc;
    private int[]? gpuVfDraft;
    private HomeControlSession? session;
    private bool gpuPresetLoading;

    public GpuWorkspaceV2()
    {
        InitializeComponent();
        IsFollowingPreset = followPreferences.Load().GpuFollowPreset;
        PresetToolbar.ConfigureFollowPreset(IsFollowingPreset);
        PresetToolbar.FollowPresetChanged += OnFollowPresetChanged;
        OperatingFrequencyCurve.FrameRendered += OnOperatingCurveFrame;
        OperatingVoltageCurve.FrameRendered += OnOperatingCurveFrame;
        InitializeGpuHelp();
        SetTuningControls(false);
        PresetToolbar.SetActionAvailability(true, false);
        RouteDiagram.SetUnknownMode();
        VoltageFrequencyCurve.CurveChanged += OnGpuVfCurveChanged;
        VoltageFrequencyCurve.RestoreDefaultsRequested += OnGpuVfRestoreDefaultsRequested;
        PresetToolbar.SelectedKeyChanged += OnGpuPresetSelected;
        PresetToolbar.UseRequested += OnUseGpuPreset;
        PresetToolbar.PresetUseRequested += OnGpuPresetUseRequested;
        PresetToolbar.EditingModeChanged += OnGpuEditingModeChanged;
        PresetToolbar.SaveAsRequested += OnGpuSaveAsRequested;
        PresetToolbar.EnablePresetReset(OnResetGpuPreset);
        foreach (var rail in new UIElement[] { CoreRail, MemoryRail, CoreOffsetRail })
        {
            rail.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => gpuRailPointerHeld = true), true);
            rail.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => gpuRailPointerHeld = false), true);
            rail.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => gpuRailPointerHeld = false), true);
        }
        PresetToolbar.SelectedKey = PresetKey.Create(ControlModeId.Turbo, 2);
        CoreRail.SetValue(2280);
        MemoryRail.SetValue(0);
        ApplyCoreOffsetProfile(-500, 500, 0);
        CoreRail.ValueChanged += (_, value) => UpdateClockDraft(value, fromRail: true);
        CoreValueBox.ValueChanged += (_, value) => UpdateClockDraft(value, fromRail: false);
        MemoryRail.ValueChanged += (_, value) =>
        {
            if (memorySynchronizing) return;
            memorySynchronizing = true;
            MemoryValueBox.Value = value;
            memorySynchronizing = false;
            UpdateMemoryDraft();
            MarkPresetDirty();
        };
        MemoryValueBox.ValueChanged += (_, value) =>
        {
            if (memorySynchronizing) return;
            memorySynchronizing = true;
            MemoryRail.SetValue(value);
            memorySynchronizing = false;
            UpdateMemoryDraft();
            MarkPresetDirty();
        };
        CoreOffsetRail.ValueChanged += (_, value) =>
        {
            if (coreOffsetSynchronizing) return;
            CoreOffsetValueBox.Value = value;
        };
        CoreOffsetValueBox.ValueChanged += (_, _) =>
        {
            if (coreOffsetSynchronizing) return;
            coreOffsetSynchronizing = true;
            CoreOffsetRail.SetValue(CoreOffsetValueBox.Value);
            coreOffsetSynchronizing = false;
            UpdateCoreOffsetDraft();
            MarkPresetDirty();
        };
        Unloaded += (_, _) => { StopMonitorMotion(); };
    }

    public void AttachSession(HomeControlSession controlSession)
    {
        session = controlSession;
        if (controlSession.State is { } state) ApplyState(state);
    }

    public void ShowAdvancedPreview()
    {
        DispatcherQueue.TryEnqueue(() =>
            VoltageFrequencyCurve.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.02 }));
    }

    private void MarkPresetDirty(bool submitLive = true)
    {
        if (gpuPresetLoading || gpuEditorLoading) return;
        if (!PresetToolbar.IsEditingPreset)
        {
            PresetToolbar.SetCurrentSettingsModified();
            if (submitLive) QueueLiveGpuChange();
            else PresetToolbar.SetActionAvailability(!GpuWritePending, gpuVfDirty && !GpuWritePending);
            return;
        }
        gpuPresetDirty = true;
        PresetToolbar.SetActionAvailability(!GpuWritePending, !GpuWritePending);
        _ = PresetToolbar.SetDirtyStatusAsync(true);
    }

    private async void OnSavePresetRequested(object? sender, EventArgs e)
        => await SaveGpuPresetAsync();

    public void ApplyTelemetry(HardwareSnapshot snapshot)
    {
        var powerCeilingText = snapshot.GpuEnforcedPowerLimitWatts is double enforced
            ? snapshot.GpuMaximumPowerLimitWatts is double maximum
                ? $"当前 {enforced:0} / 最高 {maximum:0} W"
                : $"当前上限 {enforced:0} W"
            : "驱动上限 -- W";
        if (GpuPowerCeilingText.Text != powerCeilingText)
            GpuPowerCeilingText.Text = powerCeilingText;
        double? gpuVoltageMv = snapshot.GpuCoreVoltageVolts is >= 0.3 and <= 2.0
            ? snapshot.GpuCoreVoltageVolts * 1000d : null;
        ApplyOperatingPointState(gpuVoltageMv, snapshot.GpuFrequencyMhz, snapshot.GpuPerformanceState,
            snapshot.GpuPerformanceLimitReason, snapshot.CapturedAtUtc);
        ApplyMonitorTargets(snapshot);
        RefreshActiveDisplayRoute();
        // Live frequency is not the user's frequency limit; never overwrite the preset editor with telemetry.
        bool available = snapshot.GpuTemperatureC.HasValue || snapshot.GpuUsagePercent.HasValue || snapshot.GpuFrequencyMhz.HasValue || snapshot.GpuPowerWatts.HasValue;
        MonitorText.Text = available ? "连接正常" : "遥测不可用";
        MonitorDot.Fill = new SolidColorBrush(available ? Windows.UI.Color.FromArgb(255, 118, 185, 0) : Windows.UI.Color.FromArgb(255, 89, 99, 109));
        TelemetrySourceText.Text = available ? "硬件服务" : "不可用";
        TemperatureTrendCurve.UpdateSample(snapshot.GpuTemperatureC, snapshot.CapturedAtUtc);
        UsageTrendCurve.UpdateSample(snapshot.GpuUsagePercent, snapshot.CapturedAtUtc);
        PowerTrendCurve.UpdateSample(snapshot.GpuPowerWatts, snapshot.CapturedAtUtc);
    }

    public void ApplyOperatingPointState(
        double? coreVoltageMv,
        double? coreFrequencyMhz,
        string? pState,
        string? performanceLimitReason,
        DateTimeOffset? observedAtUtc = null)
    {
        string voltageText = Format(coreVoltageMv, "mV");
        if (GpuCoreVoltageText.Text != voltageText) GpuCoreVoltageText.Text = voltageText;
        string frequencyText = Format(coreFrequencyMhz, "MHz");
        if (GpuCoreFrequencyText.Text != frequencyText) GpuCoreFrequencyText.Text = frequencyText;
        string stateText = pState ?? "未读回";
        if (GpuPStateText.Text != stateText) GpuPStateText.Text = stateText;
        UpdateLimitReason(performanceLimitReason);
        ToolTipService.SetToolTip(PerformanceLimitReasonText, performanceLimitReason ?? "驱动未提供限制原因");
        bool hasVoltage = coreVoltageMv is >= 300 and <= 2000;
        bool hasFrequency = coreFrequencyMhz is >= 0 and <= 4000;
        OperatingFrequencyCurve.UpdateSample(hasFrequency ? coreFrequencyMhz : null, observedAtUtc);
        OperatingVoltageCurve.UpdateSample(hasVoltage ? coreVoltageMv : null, observedAtUtc);
        OperatingPointLabel.Visibility = hasFrequency || hasVoltage ? Visibility.Visible : Visibility.Collapsed;
        OperatingPointLabel.Text = hasVoltage && hasFrequency ? "LIVE" : "最近值";
    }

    private void OnOperatingCurveFrame(object? sender, EventArgs args)
    {
        OperatingPointLabel.Opacity = Math.Max(OperatingFrequencyCurve.HeadOpacity, OperatingVoltageCurve.HeadOpacity);
        if (OperatingPointLabel.Opacity < 1) OperatingPointLabel.Text = "最近值";
    }

    private void UpdateLimitReason(string? reason)
    {
        string next = reason ?? "未读回";
        string? previous = displayedLimitReason;
        if (displayedLimitReason is null || next == displayedLimitReason)
        {
            displayedLimitReason = next;
            pendingLimitReason = null;
        }
        else if (next != pendingLimitReason)
        {
            pendingLimitReason = next;
            pendingLimitSinceUtc = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - pendingLimitSinceUtc >= TimeSpan.FromSeconds(
                     next is "GPU 空闲" or "无活动限制" or "未读回" ? 3 : 1))
        {
            displayedLimitReason = next;
            pendingLimitReason = null;
        }
        if (displayedLimitReason == previous) return;
        PerformanceLimitReasonText.Text = displayedLimitReason;
        bool limited = displayedLimitReason is not ("GPU 空闲" or "无活动限制" or "未读回");
        PerformanceLimitStatusCircle.Fill = new SolidColorBrush(limited
            ? Windows.UI.Color.FromArgb(60, 255, 36, 79)
            : Windows.UI.Color.FromArgb(36, 89, 99, 109));
        PerformanceLimitStatusGlyph.Foreground = new SolidColorBrush(limited
            ? Windows.UI.Color.FromArgb(255, 255, 36, 79)
            : Windows.UI.Color.FromArgb(255, 138, 146, 154));
    }

    private static void ConfigureLimit(
        PerformanceTuningRailV2 rail,
        GpuValueStepper valueBox,
        double minimum,
        double maximum,
        double recommended,
        double value)
    {
        rail.Minimum = minimum;
        rail.Maximum = maximum;
        rail.LimitValue = maximum;
        rail.RecommendedValue = recommended;
        rail.Configure();
        rail.SetValue(value);
        valueBox.Minimum = minimum;
        valueBox.Maximum = maximum;
        valueBox.Value = value;
    }

    public void ApplyCoreOffsetProfile(
        double coreMinimum,
        double coreMaximum,
        double coreOffset)
    {
        ConfigureLimit(CoreOffsetRail, CoreOffsetValueBox, coreMinimum, coreMaximum, 0, coreOffset);
    }

    public void ApplyState(HomeStateSnapshot snapshot)
    {
        UpdateGpuActiveBadge(snapshot);
        string? gpuName = snapshot.Capabilities.Identity?.GpuName;
        RouteDiagram.SetGpuName(gpuName);
        bool muxAvailable = CapabilityAvailable(snapshot, "muxMode");
        bool tuningAvailable = CapabilityAvailable(snapshot, "gpuFrequencyLimit") && snapshot.Controls.GpuClockLimit is { Error: null, MaximumMhz: > 0 };
        RouteDiagram.SetAvailable(muxAvailable);
        if (muxAvailable && snapshot.Controls.MuxMode is MuxMode mode)
            RouteDiagram.SetMode(mode);
        else RouteDiagram.SetUnknownMode();
        if (PresetToolbar.IsEditingPreset || gpuEditorLoading) return;
        // Each group sets its own availability; a blanket reset restarts disabled-state motion on every poll.
        ApplyClockLimitState(snapshot.Controls.GpuClockLimit, tuningAvailable);
        CoreValueBox.Visibility = Visibility.Visible;
        CoreLiveUnknown.Visibility = tuningAvailable && !clockDirty && snapshot.Controls.GpuClockLimit?.SubmittedMhz is null
            ? Visibility.Visible : Visibility.Collapsed;
        PresetToolbar.SetActionAvailability(!GpuWritePending, gpuVfDirty && !GpuWritePending);
        TuningStateText.Text = tuningAvailable ? "可调节" : "只读";
        var unavailable = new List<string>(2);
        if (!muxAvailable) unavailable.Add("MUX");
        if (!tuningAvailable) unavailable.Add("GPU 绝对限频");
        DiagnosticText.Text = snapshot.Capabilities.Reason ?? (unavailable.Count == 0
            ? "已验证调校能力已连接"
            : $"{string.Join("、", unavailable)}暂不可调节；电压频率曲线按独立能力显示。可查看各控件的状态与说明。");
        ApplyGpuVfState(snapshot.Controls.GpuVf, CapabilityAvailable(snapshot, "gpuVfCurve"),
            CapabilityAvailable(snapshot, "gpuMemoryOffset"), CapabilityAvailable(snapshot, "gpuCoreOffset"));
        if (GpuWritePending) SetGpuPresetControlsEnabled(false);
    }

    private void ApplyGpuVfState(GpuVfState? hardware, bool capabilityAvailable, bool memoryAvailable, bool coreAvailable)
    {
        ApplyMemoryOffsetState(hardware, memoryAvailable);
        ApplyCoreOffsetState(hardware, coreAvailable);
        if (gpuVfDirty || gpuVfPending) return;
        if (hardware is not null && lastGpuVf is not null &&
            hardware.Error == lastGpuVf.Error && hardware.MemoryOffsetKhz == lastGpuVf.MemoryOffsetKhz &&
            hardware.CoreOffsetKhz == lastGpuVf.CoreOffsetKhz &&
            hardware.Nodes.SequenceEqual(lastGpuVf.Nodes)) return;
        lastGpuVf = hardware;
        bool ready = capabilityAvailable && hardware is { Nodes.Length: 127, Error: null };
        VoltageFrequencyCurve.ApplyState(new GpuCurveState(ready,
            hardware?.Error ?? "等待驱动曲线读回",
            (hardware?.MinimumOffsetKhz ?? 0) / 1000d,
            (hardware?.MaximumOffsetKhz ?? 0) / 1000d,
            ready ? hardware!.Nodes.Select(node => new GpuCurveNode(node.VoltageMv, node.BaseFrequencyMhz, node.OffsetKhz / 1000d)).ToArray() : [])
        {
            MemoryOffsetKhz = hardware?.MemoryOffsetKhz
        });
    }

    private void OnGpuVfCurveChanged(object? sender, IReadOnlyList<GpuCurveNode> nodes)
    {
        gpuVfDraft = nodes.Select(node => (int)Math.Round(node.OffsetMhz * 1000d)).ToArray();
        gpuVfDirty = lastGpuVf is { Nodes.Length: 127 } &&
            !gpuVfDraft.SequenceEqual(lastGpuVf.Nodes.Select(node => node.OffsetKhz));
        VoltageFrequencyCurve.SetStatus(gpuVfDirty
            ? PresetToolbar.IsEditingPreset ? "正在编辑预设曲线，保存并应用后生效" : "当前曲线待应用，请点击应用调整"
            : "与硬件读回曲线一致");
        MarkPresetDirty(submitLive: false);
    }

    private async void OnGpuVfRestoreDefaultsRequested(object? sender, EventArgs e)
    {
        var activeSession = session;
        if (GpuWritePending || activeSession?.State is not { } state) return;
        if (!state.Controls.GpuCurveFactoryResetAvailable || !CapabilityAvailable(state, "gpuVfCurve") ||
            state.Controls.GpuVf is not { Nodes.Length: 127, Error: null } before ||
            before.CoreOffsetKhz is null || !CapabilityAvailable(state, "gpuCoreOffset"))
        {
            VoltageFrequencyCurve.SetStatus("驱动恢复能力或读回不可用");
            return;
        }
        if (PresetToolbar.IsEditingPreset)
        {
            gpuVfDraft = new int[127];
            gpuVfDirty = true;
            coreOffsetDirty = true;
            coreOffsetSynchronizing = true;
            CoreOffsetValueBox.Value = 0; CoreOffsetRail.SetValue(0);
            coreOffsetSynchronizing = false;
            VoltageFrequencyCurve.ApplyState(new GpuCurveState(true, null, before.MinimumOffsetKhz / 1000d,
                before.MaximumOffsetKhz / 1000d, before.Nodes.Select(node => new GpuCurveNode(node.VoltageMv, node.BaseFrequencyMhz, 0)).ToArray()));
            MarkPresetDirty();
            return;
        }
        gpuPresetApplying = true;
        SetGpuPresetControlsEnabled(false);
        VoltageFrequencyCurve.SetStatus("正在恢复默认曲线…");
        string status;
        try
        {
            var result = await GpuCurveFactoryDefaults.RestoreAsync(before,
                command => activeSession.ExecuteAsync(command, CancellationToken.None),
                () => activeSession.State?.Controls.GpuVf);
            status = result.Message;
            if (result.Succeeded)
            {
                gpuVfDirty = coreOffsetDirty = false;
                gpuVfDraft = null;
                lastGpuVf = null;
                if (PresetToolbar.IsEditingPreset) MarkPresetDirty();
                else { appliedGpuPreset = null; appliedGpuKey = null; PresetToolbar.SetConfirmedActivePreset(null); }
            }
        }
        catch (Exception exception)
        {
            AppRuntimeLog.Write($"[{DateTime.Now:O}] GPU default curve restore failed: {exception}\n");
            status = "恢复默认请求中断，请检查硬件状态";
        }
        finally
        {
            gpuPresetApplying = false;
            if (activeSession.State is { } latest) ApplyState(latest);
            SetGpuPresetControlsEnabled(true);
        }
        VoltageFrequencyCurve.SetStatus(status);
    }

    private void SetTuningControls(bool available)
    {
        CoreRail.IsEnabled = available;
        CoreValueBox.IsEnabled = available;
    }

    private void ApplyMemoryOffsetState(GpuVfState? hardware, bool capabilityAvailable)
    {
        memoryReady = capabilityAvailable && hardware is
        {
            MemoryOffsetKhz: not null, MemoryMinimumOffsetKhz: not null, MemoryMaximumOffsetKhz: not null
        } && hardware.Error is null;
        if (!memoryReady)
        {
            MemoryRail.IsEnabled = MemoryValueBox.IsEnabled = false;
            SetGpuHelpState(MemoryOffsetHelp, "硬件偏移不可用");
            return;
        }
        int offset = hardware!.MemoryOffsetKhz!.Value;
        if (memoryAppliedKhz != offset && !memoryDraftDirty && !memoryPending)
        {
            memorySynchronizing = true;
            MemoryRail.SetValue(offset / 1000d);
            MemoryValueBox.Value = offset / 1000d;
            memorySynchronizing = false;
        }
        memoryAppliedKhz = offset;
        MemoryRail.IsEnabled = MemoryValueBox.IsEnabled = !memoryPending;
        UpdateMemoryDraft();
    }

    private bool memoryDraftDirty;

    private void UpdateMemoryDraft()
    {
        memoryDraftDirty = memoryReady && memoryAppliedKhz is int applied &&
            (int)Math.Round((MemoryRail.Value ?? 0) * 1000d) != applied;

        if (!memoryPending)
            SetGpuHelpState(MemoryOffsetHelp, memoryDraftDirty ? "草稿未应用" :
                memoryAppliedKhz is int offset ? $"硬件 {offset / 1000d:0} MHz" : "等待硬件读回");
    }

    private void ApplyCoreOffsetState(GpuVfState? hardware, bool capabilityAvailable)
    {
        coreOffsetReady = capabilityAvailable && hardware is
        {
            CoreOffsetKhz: not null, CoreMinimumOffsetKhz: not null, CoreMaximumOffsetKhz: not null,
            Error: null
        } && hardware.CoreMinimumOffsetKhz <= hardware.CoreMaximumOffsetKhz;
        if (!coreOffsetReady)
        {
            CoreOffsetRail.IsEnabled = CoreOffsetValueBox.IsEnabled = false;
            SetGpuHelpState(CoreOffsetHelp, "硬件偏移不可用");
            return;
        }
        int offset = hardware!.CoreOffsetKhz!.Value;
        CoreOffsetValueBox.Minimum = Math.Max(-200, hardware.CoreMinimumOffsetKhz!.Value / 1000d);
        CoreOffsetValueBox.Maximum = Math.Min(200, hardware.CoreMaximumOffsetKhz!.Value / 1000d);
        if (CoreOffsetValueBox.Minimum > CoreOffsetValueBox.Maximum)
        {
            coreOffsetReady = false;
            CoreOffsetRail.IsEnabled = CoreOffsetValueBox.IsEnabled = false;
            SetGpuHelpState(CoreOffsetHelp, "驱动偏移范围不可用");
            return;
        }
        coreOffsetSynchronizing = true;
        CoreOffsetRail.Minimum = CoreOffsetValueBox.Minimum;
        CoreOffsetRail.Maximum = CoreOffsetValueBox.Maximum;
        CoreOffsetRail.LimitValue = CoreOffsetValueBox.Maximum;
        CoreOffsetRail.Configure();
        coreOffsetSynchronizing = false;
        if (coreOffsetAppliedKhz != offset && !coreOffsetDirty && !coreOffsetPending)
        {
            coreOffsetSynchronizing = true;
            CoreOffsetValueBox.Value = offset / 1000d;
            CoreOffsetRail.SetValue(offset / 1000d);
            coreOffsetSynchronizing = false;
        }
        coreOffsetAppliedKhz = offset;
        CoreOffsetRail.IsEnabled = CoreOffsetValueBox.IsEnabled = !coreOffsetPending;
        UpdateCoreOffsetDraft();
    }

    private void UpdateCoreOffsetDraft()
    {
        coreOffsetDirty = coreOffsetReady && coreOffsetAppliedKhz is int applied &&
            (int)Math.Round(CoreOffsetValueBox.Value * 1000d) != applied;

        if (!coreOffsetPending)
            SetGpuHelpState(CoreOffsetHelp, coreOffsetDirty ? "草稿未应用" :
                coreOffsetAppliedKhz is int offset ? $"硬件 {offset / 1000d:0} MHz" : "等待硬件读回");
    }


    private void UpdateUsageGauge(double? percentage)
    {
        if (percentage is not double value)
        {
            UsageProgressPath.Data = null;
            return;
        }

        const double center = 61d;
        const double radius = 48d;
        const double startAngle = 135d;
        double sweep = 270d * Math.Clamp(value, 0d, 100d) / 100d;
        if (sweep <= 0.01d)
        {
            UsageProgressPath.Data = null;
            return;
        }

        static Point PointAt(double angle)
        {
            double radians = angle * Math.PI / 180d;
            return new Point(center + radius * Math.Cos(radians), center + radius * Math.Sin(radians));
        }

        if (usageGeometry is null)
        {
            usageArc = new ArcSegment { Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise };
            var figure = new PathFigure { StartPoint = PointAt(startAngle), IsClosed = false };
            figure.Segments.Add(usageArc);
            usageGeometry = new PathGeometry();
            usageGeometry.Figures.Add(figure);
        }
        usageArc!.Point = PointAt(startAngle + sweep);
        usageArc.IsLargeArc = sweep > 180d;
        if (!ReferenceEquals(UsageProgressPath.Data, usageGeometry)) UsageProgressPath.Data = usageGeometry;
    }

    private static bool CapabilityAvailable(HomeStateSnapshot snapshot, string key) => snapshot.Capabilities.Items.Any(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) && item.State == CapabilityState.Available);
    private static string Format(double? value, string unit) => value is double actual && double.IsFinite(actual) ? $"{actual:0} {unit}" : $"-- {unit}";

}
