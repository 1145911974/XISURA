using System.Diagnostics;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class AutomationWorkspaceV2
{
    private readonly TelemetryTransition cpuLoadTransition = new();
    private readonly TelemetryTransition gpuLoadTransition = new();
    private readonly Stopwatch loadClock = Stopwatch.StartNew();
    private readonly MotionSettingsService loadMotion = new();
    private bool loadSubscribed;
    public bool ReducedMotion { get; set; }

    private bool LoadVisible()
    {
        if (!IsLoaded || XamlRoot?.IsHostVisible != true) return false;
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private void ApplyLoadTargets(AdaptiveDecisionDisplay display)
    {
        loadMotion.Refresh();
        double duration = LoadVisible() && !ReducedMotion && !loadMotion.IsReducedMotionEnabled
            ? TelemetryTransition.LiveSampleDurationSeconds : 0;
        double now = loadClock.Elapsed.TotalSeconds;
        // The existing freshness/validity policy remains authoritative, never interpolate stale data.
        cpuLoadTransition.SetTarget(display.CpuUsage == "--" ? null : lastTelemetry?.CpuUsagePercent, now, duration);
        gpuLoadTransition.SetTarget(display.GpuUsage == "--" ? null : lastTelemetry?.GpuUsagePercent, now, duration);
        RenderLoads();
        if ((cpuLoadTransition.IsActive || gpuLoadTransition.IsActive) && !loadSubscribed)
        {
            CompositionTarget.Rendering += OnLoadFrame;
            loadSubscribed = true;
        }
        else if (!cpuLoadTransition.IsActive && !gpuLoadTransition.IsActive) StopLoadMotion();
    }

    private void OnLoadFrame(object? sender, object args)
    {
        bool visible = LoadVisible();
        double now = visible ? loadClock.Elapsed.TotalSeconds : double.MaxValue;
        cpuLoadTransition.Advance(now);
        gpuLoadTransition.Advance(now);
        RenderLoads();
        if (!visible || (!cpuLoadTransition.IsActive && !gpuLoadTransition.IsActive)) StopLoadMotion();
    }

    private void RenderLoads()
    {
        CpuLoadText.Text = cpuLoadTransition.Value is double cpu ? $"{cpu:0}%" : "--";
        GpuLoadText.Text = gpuLoadTransition.Value is double gpu ? $"{gpu:0}%" : "--";
    }

    private void StopLoadMotion()
    {
        if (!loadSubscribed) return;
        CompositionTarget.Rendering -= OnLoadFrame;
        loadSubscribed = false;
    }
}
