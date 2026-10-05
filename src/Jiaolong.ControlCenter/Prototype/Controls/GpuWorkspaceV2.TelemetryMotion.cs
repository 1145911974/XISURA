using System.Diagnostics;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private readonly TelemetryTransition[] monitorValues = Enumerable.Range(0, 7).Select(_ => new TelemetryTransition()).ToArray();
    private readonly Stopwatch monitorClock = Stopwatch.StartNew();
    private readonly MotionSettingsService monitorMotion = new();
    private TimeSpan lastDisplayRouteRead = TimeSpan.FromSeconds(-1);
    private bool monitorSubscribed;
    private long lastMonitorFrameTimestamp;
    private bool reducedMotion;
    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            if (RouteDiagram is not null) RouteDiagram.ReducedMotion = value;
            foreach (var curve in new[] { TemperatureTrendCurve, UsageTrendCurve, PowerTrendCurve,
                         OperatingFrequencyCurve, OperatingVoltageCurve })
                if (curve is not null) curve.ReducedMotion = value;
        }
    }

    private bool MonitorVisible()
    {
        if (!IsLoaded || XamlRoot?.IsHostVisible != true) return false;
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private void ApplyMonitorTargets(HardwareSnapshot snapshot)
    {
        monitorMotion.Refresh();
        double seconds = MonitorVisible() && !ReducedMotion && !monitorMotion.IsReducedMotionEnabled
            ? TelemetryTransition.LiveSampleDurationSeconds : 0;
        double?[] values = [snapshot.GpuTemperatureC, snapshot.GpuUsagePercent, snapshot.GpuPowerWatts,
            snapshot.GpuFrequencyMhz, snapshot.GpuMemoryUsedGb, snapshot.GpuMemoryTotalGb, snapshot.GpuFanRpm];
        for (int i = 0; i < values.Length; i++) monitorValues[i].SetTarget(values[i], monitorClock.Elapsed.TotalSeconds, seconds);
        RenderMonitorValues();
        if (monitorValues.Any(value => value.IsActive) && !monitorSubscribed)
        {
            lastMonitorFrameTimestamp = 0;
            CompositionTarget.Rendering += OnMonitorFrame;
            monitorSubscribed = true;
        }
        else if (!monitorValues.Any(value => value.IsActive)) StopMonitorMotion();
    }

    private void RefreshActiveDisplayRoute()
    {
        if (!MonitorVisible()) return;
        var elapsed = monitorClock.Elapsed;
        if (elapsed - lastDisplayRouteRead < TimeSpan.FromSeconds(1)) return;
        lastDisplayRouteRead = elapsed;
        RouteDiagram.RefreshDisplays();
        ActiveDisplayRouteText.Text = WindowsDisplayRouteReader.ReadStatus(session?.State?.Controls.MuxMode);
    }

    private void OnMonitorFrame(object? sender, object args)
    {
        if (!MonitorVisible())
        {
            foreach (var value in monitorValues) value.Advance(double.MaxValue);
            RenderMonitorValues();
            StopMonitorMotion();
            return;
        }
        double elapsed = monitorClock.Elapsed.TotalSeconds;
        foreach (var value in monitorValues) value.Advance(elapsed);
        bool active = monitorValues.Any(value => value.IsActive);
        long now = Stopwatch.GetTimestamp();
        if (active && now - lastMonitorFrameTimestamp < Stopwatch.Frequency / 30) return;
        lastMonitorFrameTimestamp = now;
        RenderMonitorValues();
        if (!active) StopMonitorMotion();
    }

    private void RenderMonitorValues()
    {
        SetText(TemperatureText, monitorValues[0].Value?.ToString("0") ?? "--");
        SetText(UsageText, monitorValues[1].Value?.ToString("0") ?? "--");
        UpdateUsageGauge(monitorValues[1].Value);
        SetText(PowerText, monitorValues[2].Value?.ToString("0") ?? "--");
        string frequency = Format(monitorValues[3].Value, "MHz");
        SetText(FrequencyText, frequency);
        SetText(GpuCoreFrequencyText, frequency);
        SetText(MemoryText, monitorValues[4].Value is double used && monitorValues[5].Value is double total
            ? $"{used:0.0} / {total:0.0}" : "-- / --");
        SetText(FanText, Format(monitorValues[6].Value, "RPM"));
    }

    private static void SetText(TextBlock element, string value)
    {
        if (element.Text != value) element.Text = value;
    }

    private void StopMonitorMotion()
    {
        if (!monitorSubscribed) return;
        CompositionTarget.Rendering -= OnMonitorFrame;
        monitorSubscribed = false;
    }
}
