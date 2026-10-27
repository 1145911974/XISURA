using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

internal sealed class PageModeFollowObserver(ControlPageId page = ControlPageId.Gpu)
{
    private long? connection;
    private PerformanceMode? baseline;
    private PerformanceMode? candidate;
    private DateTimeOffset candidateCapturedAt;

    internal PerformanceMode? Observe(HomeStateSnapshot snapshot, long connectionGeneration, bool canApply,
        PerformanceMode? manualTarget = null, bool manualInFlight = false, bool deferTransitions = false)
    {
        var evidence = snapshot.Controls.ModeEvidence;
        var mode = snapshot.Controls.PerformanceMode;
        var age = evidence is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - evidence.CapturedAtUtc;
        if (mode is not (PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo) ||
            evidence is null || evidence.ParsedMode != mode || evidence.RawValue is null ||
            age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1))
        {
            Reset();
            return null;
        }

        if (connection != connectionGeneration)
        {
            Reset();
            connection = connectionGeneration;
        }
        bool available = IsAvailable(snapshot);
        if (deferTransitions)
        {
            candidate = null;
            if (!available) Reset();
            return null;
        }
        if (baseline is null || !canApply || !available ||
            manualInFlight || manualTarget == mode)
        {
            baseline = mode;
            candidate = null;
            return null;
        }
        if (mode == baseline)
        {
            candidate = null;
            return null;
        }
        if (candidate != mode)
        {
            candidate = mode;
            candidateCapturedAt = evidence.CapturedAtUtc;
            return null;
        }
        if (evidence.CapturedAtUtc <= candidateCapturedAt) return null;
        baseline = mode;
        candidate = null;
        return mode;
    }

    internal bool IsAvailable(HomeStateSnapshot snapshot) =>
        snapshot.Capabilities.SupportState == DeviceSupportState.Ready &&
        snapshot.Controls.AdaptiveAutomation?.Enabled != true &&
        snapshot.Capabilities.Items.Any(item => item.State == CapabilityState.Available && (page switch
        {
            ControlPageId.Performance => item.Key == "cpuTuning",
            ControlPageId.Fan => item.Key == "fanControl",
            ControlPageId.Gpu => item.Key is "gpuFrequencyLimit" or "gpuMemoryOffset" or "gpuCoreOffset" or "gpuVfCurve",
            _ => false
        }));

    internal void Reset()
    {
        connection = null;
        baseline = candidate = null;
        candidateCapturedAt = default;
    }
}
