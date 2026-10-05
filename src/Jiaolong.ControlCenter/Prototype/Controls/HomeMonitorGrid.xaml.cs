using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class HomeMonitorGrid : UserControl
{
    private static void SetReadout(TextBlock text, string value)
    {
        if (text.Text != value) text.Text = value;
    }

    private static readonly TimeSpan TelemetryDuration = TimeSpan.FromSeconds(TelemetryTransition.LiveSampleDurationSeconds);
    private readonly Stopwatch telemetryClock = new();
    private readonly SmoothedCpuFrequency cpuFrequencyDisplay = new();
    private HomeTelemetrySnapshot displayedTelemetry = HomeTelemetrySnapshot.Unknown;
    private HomeTelemetrySnapshot telemetryFrom = HomeTelemetrySnapshot.Unknown;
    private HomeTelemetrySnapshot targetTelemetry = HomeTelemetrySnapshot.Unknown;
    private bool reducedMotion;
    private bool telemetryInitialized;
    private bool telemetryAnimating;
    private bool renderingSubscribed;
    private long lastTelemetryFrameTimestamp;
    private double? displayedCpuFrequencyMhz;

    public HomeMonitorGrid()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public bool ReducedMotion
    {
        set
        {
            reducedMotion = value;
            CpuMonitorCard.ReducedMotion = value;
            GpuMonitorCard.ReducedMotion = value;
            CpuFanCurve.ReducedMotion = value;
            GpuFanCurve.ReducedMotion = value;
        }
    }

    public void ApplyTemperatures(double? cpuTemperatureC, double? gpuTemperatureC)
    {
        CpuMonitorCard.TemperatureC = cpuTemperatureC;
        GpuMonitorCard.TemperatureC = gpuTemperatureC;
    }

    public void ApplyTemperatureWall(double wallC)
    {
        CpuMonitorCard.ApplyTemperatureWall(wallC);
        GpuMonitorCard.ApplyTemperatureWall(wallC);
    }

    public void ApplyTelemetry(HomeTelemetrySnapshot snapshot)
    {
        CpuMonitorCard.ApplyTelemetry(snapshot.CpuTemperatureC, snapshot.CpuUsagePercent, snapshot.CapturedAtUtc);
        GpuMonitorCard.ApplyTelemetry(snapshot.GpuTemperatureC, snapshot.GpuUsagePercent, snapshot.CapturedAtUtc);
        CpuFanCurve.UpdateSample(snapshot.CpuFanRpm, snapshot.CapturedAtUtc);
        GpuFanCurve.UpdateSample(snapshot.GpuFanRpm, snapshot.CapturedAtUtc);
        if (telemetryInitialized && snapshot == targetTelemetry)
            return;

        displayedCpuFrequencyMhz = cpuFrequencyDisplay.Update(snapshot.CpuFrequencyMhz);

        if (!telemetryInitialized)
        {
            telemetryInitialized = true;
            displayedTelemetry = telemetryFrom = targetTelemetry = snapshot;
            ApplyDisplayedTelemetry(snapshot, 0);
            return;
        }

        telemetryFrom = displayedTelemetry;
        targetTelemetry = snapshot;
        if (reducedMotion || !CanAnimateTelemetry())
        {
            displayedTelemetry = targetTelemetry;
            telemetryAnimating = false;
            telemetryClock.Stop();
            ApplyDisplayedTelemetry(snapshot, 0);
            StopRenderingIfIdle();
            return;
        }

        telemetryAnimating = true;
        telemetryClock.Restart();
        EnsureRendering();
    }

    private void OnRendering(object? sender, object args)
    {
        if (!CanAnimateTelemetry())
        {
            displayedTelemetry = targetTelemetry;
            telemetryAnimating = false;
            telemetryClock.Stop();
            StopRenderingIfIdle();
            return;
        }
        if (!telemetryAnimating)
        {
            StopRenderingIfIdle();
            return;
        }

        var progress = Math.Clamp(telemetryClock.Elapsed.TotalMilliseconds / TelemetryDuration.TotalMilliseconds, 0, 1);
        long now = Stopwatch.GetTimestamp();
        if (progress < 1 && now - lastTelemetryFrameTimestamp < Stopwatch.Frequency / 30) return;
        lastTelemetryFrameTimestamp = now;
        var eased = 1 - Math.Pow(1 - progress, 3);
        displayedTelemetry = InterpolateTelemetry(telemetryFrom, targetTelemetry, eased);
        ApplyDisplayedTelemetry(displayedTelemetry, progress);
        if (progress >= 1)
        {
            displayedTelemetry = targetTelemetry;
            telemetryAnimating = false;
            telemetryClock.Stop();
        }

        StopRenderingIfIdle();
    }

    private void ApplyDisplayedTelemetry(HomeTelemetrySnapshot snapshot, double _)
    {
        CpuMonitorCard.ApplyDetailValues(
            Format(displayedCpuFrequencyMhz, "0", "MHz"),
            Format(snapshot.CpuPowerWatts, "0", "W"));
        GpuMonitorCard.ApplyDetailValues(
            FormatPair(snapshot.GpuMemoryUsedGb, snapshot.GpuMemoryTotalGb, "0.0", "GB"),
            Format(snapshot.GpuPowerWatts, "0", "W"));

        SetReadout(MemoryValue, FormatPair(snapshot.MemoryUsedGb, snapshot.MemoryTotalGb, "0.0", "GB"));
        UpdateMemorySegments(snapshot.MemoryUsedGb, snapshot.MemoryTotalGb);
        SetDrive(SystemDriveValue, SystemDriveFillScale, snapshot.SystemDriveUsedGb, snapshot.SystemDriveTotalGb);
        SetReadout(SystemDriveRemaining, FormatRemaining(snapshot.SystemDriveUsedGb, snapshot.SystemDriveTotalGb));
        SetDrive(AllDrivesValue, AllDrivesFillScale, snapshot.AllDrivesUsedGb, snapshot.AllDrivesTotalGb);
        SetReadout(AllDrivesRemaining, FormatRemaining(snapshot.AllDrivesUsedGb, snapshot.AllDrivesTotalGb));
        SetReadout(CpuFanValue, Format(snapshot.CpuFanRpm, "0", "RPM"));
        SetReadout(GpuFanValue, Format(snapshot.GpuFanRpm, "0", "RPM"));
    }

    private static void SetDrive(TextBlock text, ScaleTransform fillScale, double? used, double? total)
    {
        var value = FormatPair(used, total, "0", "GB");
        if (text.Text != value) text.Text = value;
        fillScale.ScaleX = Ratio(used, total) ?? 0;
    }

    private void UpdateMemorySegments(double? used, double? total)
    {
        var ratio = Ratio(used, total) ?? 0;
        var segmentCount = MemorySegmentBars.Children.Count;
        for (var index = 0; index < segmentCount; index++)
        {
            if (MemorySegmentBars.Children[index] is Grid segment && segment.Children.Count > 1)
            {
                var opacity = Math.Clamp((ratio * segmentCount) - index, 0, 1);
                if (segment.Children[1] is Border fill) fill.Opacity = opacity;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        telemetryClock.Stop();
        telemetryAnimating = false;
        StopRenderingIfIdle();
    }

    private void EnsureRendering()
    {
        if (renderingSubscribed || !CanAnimateTelemetry()) return;
        lastTelemetryFrameTimestamp = 0;
        CompositionTarget.Rendering += OnRendering;
        renderingSubscribed = true;
    }

    private bool CanAnimateTelemetry() => IsLoaded && Visibility == Visibility.Visible &&
        XamlRoot?.IsHostVisible == true;

    private void StopRenderingIfIdle()
    {
        if (!renderingSubscribed || telemetryAnimating) return;
        CompositionTarget.Rendering -= OnRendering;
        renderingSubscribed = false;
    }

    private static HomeTelemetrySnapshot InterpolateTelemetry(HomeTelemetrySnapshot from, HomeTelemetrySnapshot to, double amount) => new(
        Interpolate(from.CpuTemperatureC, to.CpuTemperatureC, amount),
        Interpolate(from.CpuUsagePercent, to.CpuUsagePercent, amount),
        Interpolate(from.CpuFrequencyMhz, to.CpuFrequencyMhz, amount),
        Interpolate(from.CpuPowerWatts, to.CpuPowerWatts, amount),
        Interpolate(from.GpuTemperatureC, to.GpuTemperatureC, amount),
        Interpolate(from.GpuUsagePercent, to.GpuUsagePercent, amount),
        Interpolate(from.GpuMemoryUsedGb, to.GpuMemoryUsedGb, amount),
        Interpolate(from.GpuMemoryTotalGb, to.GpuMemoryTotalGb, amount),
        Interpolate(from.GpuPowerWatts, to.GpuPowerWatts, amount),
        Interpolate(from.MemoryUsedGb, to.MemoryUsedGb, amount),
        Interpolate(from.MemoryTotalGb, to.MemoryTotalGb, amount),
        Interpolate(from.SystemDriveUsedGb, to.SystemDriveUsedGb, amount),
        Interpolate(from.SystemDriveTotalGb, to.SystemDriveTotalGb, amount),
        Interpolate(from.AllDrivesUsedGb, to.AllDrivesUsedGb, amount),
        Interpolate(from.AllDrivesTotalGb, to.AllDrivesTotalGb, amount),
        Interpolate(from.CpuFanRpm, to.CpuFanRpm, amount),
        Interpolate(from.GpuFanRpm, to.GpuFanRpm, amount))
    {
        AcPowerConnected = amount < 1 ? from.AcPowerConnected : to.AcPowerConnected,
        BatteryPercent = Interpolate(from.BatteryPercent, to.BatteryPercent, amount) is double percent
            ? (int)Math.Round(percent)
            : null
    };

    private static double? Interpolate(double? from, double? to, double amount) =>
        IsFinite(from) && IsFinite(to) ? Lerp(from!.Value, to!.Value, amount) : amount < 1 ? from : to;

    private static double? Ratio(double? used, double? total) =>
        IsFinite(used) && IsFinite(total) && total!.Value > 0
            ? Math.Clamp(used!.Value / total.Value, 0, 1)
            : null;

    private static bool IsFinite(double? value) => value is double valid && double.IsFinite(valid);

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private static string FormatRemaining(double? used, double? total) =>
        IsFinite(used) && IsFinite(total)
            ? $"剩余 {Math.Max(0, total!.Value - used!.Value):0} GB"
            : "剩余 -- GB";

    private static string Format(double? value, string format, string unit) =>
        IsFinite(value) ? $"{value!.Value.ToString(format)} {unit}" : $"-- {unit}";

    private static string FormatPair(double? used, double? total, string format, string unit) =>
        IsFinite(used) && IsFinite(total)
            ? $"{used!.Value.ToString(format)}/{total!.Value.ToString(format)} {unit}"
            : $"--/-- {unit}";
}
