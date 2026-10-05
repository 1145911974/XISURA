using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class CpuOperatingBoundaryV2 : UserControl
{
    private const double ArcRadius = 116d;
    private const double ArcCenter = 170d;
    private const double SegmentStart = 6d;
    private const double SegmentSweep = 74d;

    private double? temperatureValue;
    private double? powerValue;
    private double? frequencyValue;
    private double? voltageValue;
    private double? temperatureLimit;
    private double? powerLimit;
    private double? frequencyLimit;
    private double? voltageLimit;
    private readonly TelemetryTransition[] transitions = Enumerable.Range(0, 8).Select(_ => new TelemetryTransition()).ToArray();
    private readonly Stopwatch transitionClock = Stopwatch.StartNew();
    private readonly MotionSettingsService motionSettings = new();
    private readonly SmoothedCpuFrequency cpuFrequencyDisplay = new();
    private bool rendering;
    public bool ReducedMotion { get; set; }

    public CpuOperatingBoundaryV2()
    {
        InitializeComponent();
        SetBaseGeometry(TemperatureBasePath, 188d);
        SetBaseGeometry(PowerBasePath, 278d);
        SetBaseGeometry(VoltageBasePath, 8d);
        SetBaseGeometry(FrequencyBasePath, 98d);
        UpdateArcVisuals();
        Unloaded += (_, _) => StopFrames();
    }

    public void ApplyTelemetry(HardwareSnapshot snapshot)
    {
        temperatureValue = snapshot.CpuTemperatureC;
        powerValue = snapshot.CpuPowerWatts is int power ? power : null;
        frequencyValue = cpuFrequencyDisplay.Update(snapshot.CpuFrequencyMhz);
        voltageValue = snapshot.CpuVoltageVolts;
        ChipStatusText.Text = IsLiveState(snapshot.HardwareState)
            ? "实时"
            : "未连接";
        UpdateArcVisuals();
    }

    public void ApplyLimits(CpuTuningState? state) => ApplyLimits(
        state?.TemperatureLimitC, state?.SpptWatts, state?.AcFrequency(), null, automaticVoltage: true);

    public void ApplyLimits(double? temperature, double? power, double? frequency, double? voltage, bool automaticVoltage)
    {
        temperatureLimit = CpuOperatingBoundaryFallbacks.ResolveTemperatureLimit(temperature);
        powerLimit = CpuOperatingBoundaryFallbacks.ResolvePowerLimit(power);
        frequencyLimit = frequency;
        voltageLimit = CpuOperatingBoundaryFallbacks.ResolveVoltageLimit(voltage);
        TemperatureLimitText.Text = CpuOperatingBoundaryText.FormatTemperatureLimit(temperature);
        PowerLimitText.Text = CpuOperatingBoundaryText.FormatPowerLimit(power);
        FrequencyLimitText.Text = frequency is double frequencyValue
            ? $"上限 {frequencyValue:0} MHz"
            : "上限不可用";
        VoltageLimitText.Text = CpuOperatingBoundaryText.FormatVoltageLimit(voltage, automaticVoltage);
        UpdateArcVisuals();
    }

    private static string Format(double? value, string unit, string format) =>
        value is double actual && double.IsFinite(actual) ? $"{actual.ToString(format)} {unit}" : $"-- {unit}";

    private static string Format(int? value, string unit, string format) =>
        value is int actual ? $"{actual.ToString(format)} {unit}" : $"-- {unit}";

    internal static bool IsLiveState(string? state) =>
        string.Equals(state, "normal", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase);

    private void UpdateArcVisuals()
    {
        motionSettings.Refresh();
        double now = transitionClock.Elapsed.TotalSeconds;
        double seconds = IsVisibleForMotion() && !ReducedMotion && !motionSettings.IsReducedMotionEnabled
            ? TelemetryTransition.LiveSampleDurationSeconds : 0;
        double?[] targets = [temperatureValue, powerValue, voltageValue, frequencyValue,
            CpuOperatingBoundaryGeometry.ResolveProgress(temperatureValue, temperatureLimit),
            CpuOperatingBoundaryGeometry.ResolveProgress(powerValue, powerLimit),
            CpuOperatingBoundaryGeometry.ResolveProgress(voltageValue, voltageLimit),
            CpuOperatingBoundaryGeometry.ResolveProgress(frequencyValue, frequencyLimit)];
        for (int i = 0; i < transitions.Length; i++) transitions[i].SetTarget(targets[i], now, seconds);
        RenderValues();
        if (transitions.Any(value => value.IsActive) && !rendering)
        {
            CompositionTarget.Rendering += OnFrame;
            rendering = true;
        }
        else if (!transitions.Any(value => value.IsActive)) StopFrames();
    }

    private bool IsVisibleForMotion()
    {
        if (!IsLoaded || XamlRoot?.IsHostVisible != true) return false;
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private void OnFrame(object? sender, object args)
    {
        if (!IsVisibleForMotion()) { UpdateArcVisuals(); return; }
        foreach (var value in transitions) value.Advance(transitionClock.Elapsed.TotalSeconds);
        RenderValues();
        if (!transitions.Any(value => value.IsActive)) StopFrames();
    }

    private void StopFrames()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= OnFrame;
        rendering = false;
    }

    private static void SetText(TextBlock text, string value)
    {
        if (text.Text != value) text.Text = value;
    }

    private void RenderValues()
    {
        SetText(TemperatureText, Format(transitions[0].Value, "°C", "0"));
        SetText(PowerText, Format(transitions[1].Value, "W", "0"));
        SetText(VoltageText, Format(transitions[2].Value * 1000d, "mV", "0"));
        SetText(FrequencyText, Format(frequencyValue, "MHz", "0"));
        ApplyArc(
            TemperatureInnerPath,
            TemperatureActivePath,
            transitions[4].Value,
            188d);
        ApplyArc(
            PowerInnerPath,
            PowerActivePath,
            transitions[5].Value,
            278d);
        ApplyArc(
            VoltageInnerPath,
            VoltageActivePath,
            transitions[6].Value,
            8d);
        ApplyArc(
            FrequencyInnerPath,
            FrequencyActivePath,
            transitions[7].Value,
            98d);
    }

    private static void SetBaseGeometry(Microsoft.UI.Xaml.Shapes.Path path, double startAngle)
        => path.Data = CreateArc(startAngle, SegmentSweep);

    private static void ApplyArc(
        Microsoft.UI.Xaml.Shapes.Path inner,
        Microsoft.UI.Xaml.Shapes.Path active,
        double? progress,
        double startAngle)
    {
        bool available = progress.HasValue;
        double ratio = progress.GetValueOrDefault();
        double sweep = available
            ? CpuOperatingBoundaryGeometry.ResolveSweep(ratio, SegmentSweep)
            : 0d;
        bool visible = available && sweep > 0d;
        if (visible)
        {
            // WinUI Path.Data owns the Geometry instance; do not share one instance between layers.
            UpdateArcGeometry(inner, startAngle, sweep);
            UpdateArcGeometry(active, startAngle, sweep);
        }

        inner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        active.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void UpdateArcGeometry(Microsoft.UI.Xaml.Shapes.Path path, double startAngle, double sweep)
    {
        if (path.Data is PathGeometry geometry && geometry.Figures.Count == 1 &&
            geometry.Figures[0].Segments.Count == 1 && geometry.Figures[0].Segments[0] is ArcSegment arc)
        {
            arc.Point = PointAt(startAngle + sweep);
            arc.IsLargeArc = sweep > 180;
        }
        else path.Data = CreateArc(startAngle, sweep);
    }

    private static PathGeometry CreateArc(double startAngle, double sweep)
    {
        Point start = PointAt(startAngle);
        Point end = PointAt(startAngle + sweep);
        return new PathGeometry
        {
            Figures =
            {
                new PathFigure
                {
                    StartPoint = start,
                    IsClosed = false,
                    Segments =
                    {
                        new ArcSegment
                        {
                            Point = end,
                            Size = new Size(ArcRadius, ArcRadius),
                            IsLargeArc = sweep > 180d,
                            SweepDirection = SweepDirection.Clockwise
                        }
                    }
                }
            }
        };
    }

    private static Point PointAt(double angle)
    {
        double radians = angle * Math.PI / 180d;
        return new Point(
            ArcCenter + ArcRadius * Math.Cos(radians),
            ArcCenter + ArcRadius * Math.Sin(radians));
    }
}

internal static class CpuOperatingBoundaryFallbacks
{
    internal const double TemperatureLimitC = 100d;
    // AMD's 7745HX cTDP ceiling; a live SPPT readback replaces this fallback.
    internal const double PowerLimitWatts = 75d;
    // UI editor scale only; this is not a safe hardware voltage limit.
    internal const double VoltageDisplayLimitVolts = 2d;

    internal static double ResolveTemperatureLimit(double? value) =>
        value is double temperature && double.IsFinite(temperature) && temperature > 0d
            ? temperature
            : TemperatureLimitC;

    internal static double ResolvePowerLimit(double? value) =>
        value is double power && double.IsFinite(power) && power > 0d
            ? power
            : PowerLimitWatts;

    internal static double ResolveVoltageLimit(double? value) =>
        value is double volts && double.IsFinite(volts) && volts > 0d
            ? volts
            : VoltageDisplayLimitVolts;
}

internal static class CpuOperatingBoundaryText
{
    internal static string FormatTemperatureLimit(double? value) =>
        value is double actual && double.IsFinite(actual) && actual > 0d
            ? $"上限 {actual:0}°C"
            : $"参考上限 {CpuOperatingBoundaryFallbacks.TemperatureLimitC:0}°C";

    internal static string FormatPowerLimit(double? value) =>
        value is double actual && double.IsFinite(actual) && actual > 0d
            ? $"上限 {actual:0} W"
            : $"参考上限 {CpuOperatingBoundaryFallbacks.PowerLimitWatts:0} W";

    internal static string FormatVoltageLimit(double? volts, bool automatic) =>
        volts is double value && double.IsFinite(value) && value > 0d
            ? $"上限 {value * 1000d:0} mV"
            : $"刻度 {CpuOperatingBoundaryFallbacks.VoltageDisplayLimitVolts * 1000d:0} mV{(automatic ? " · 自动" : string.Empty)}";
}

internal static class CpuOperatingBoundaryGeometry
{
    internal static double? ResolveProgress(double? value, double? limit)
    {
        if (value is not double actual || limit is not double maximum ||
            !double.IsFinite(actual) || !double.IsFinite(maximum) || maximum <= 0d)
            return null;

        return Math.Clamp(actual / maximum, 0d, 1d);
    }

    internal static double ResolveSweep(double progress, double segmentSweep)
        => Math.Clamp(progress, 0d, 1d) * Math.Max(0d, segmentSweep);
}
