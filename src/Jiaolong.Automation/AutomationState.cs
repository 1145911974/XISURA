using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;

namespace Jiaolong.Automation;

public enum AutomationStateKind
{
    Baseline,
    AdaptiveLoad,
    ForegroundApp,
    ManualOverride,
    SessionLock,
    DcPolicy,
    CompatibilityOrConflict,
    ThermalEmergency
}

public enum PolicyReason
{
    ThermalEmergency,
    CompatibilityOrConflict,
    Dc,
    SessionLock,
    ManualOverride,
    ForegroundApp,
    AdaptiveLoad,
    Baseline
}

public enum AutomationReleaseReason
{
    ServiceStopping,
    SystemSuspend,
    SystemHibernate,
    SystemShutdown,
    SessionUnlock
}

public sealed record AutomationState(AutomationStateKind Kind, DateTimeOffset EnteredAtUtc);

public sealed record ForegroundAppObservation(
    string ProcessName,
    string ExecutableHash,
    DateTimeOffset ObservedAtUtc,
    int SessionId);

public sealed record ManualOverride(PerformanceMode Mode, DateTimeOffset ExpiresAtUtc)
{
    public bool IsActive(DateTimeOffset now) => ExpiresAtUtc > now;
}

public sealed record TelemetrySample(
    DateTimeOffset ObservedAtUtc,
    double CpuLoadPercent,
    double GpuLoadPercent,
    double GpuPowerWatts,
    double CpuTemperatureC = 0,
    double GpuTemperatureC = 0);

public sealed record TelemetryWindow(IReadOnlyList<TelemetrySample> Samples)
{
    public static TelemetryWindow Single(
        DateTimeOffset observedAtUtc,
        double cpuLoadPercent,
        double gpuLoadPercent,
        double gpuPowerWatts) =>
        new([new TelemetrySample(observedAtUtc, cpuLoadPercent, gpuLoadPercent, gpuPowerWatts)]);

    public TelemetrySample? Latest => Samples.Count == 0
        ? null
        : Samples.OrderByDescending(sample => sample.ObservedAtUtc).First();

    public bool IsThermalEmergency() =>
        Latest is { CpuTemperatureC: >= 95 } or { GpuTemperatureC: >= 87 };

    public bool HasHighGpuLoad(DateTimeOffset now) =>
        HighGpuLoadCandidateStartedAt(now) is { } startedAt
        && now - startedAt >= TimeSpan.FromSeconds(10);

    public DateTimeOffset? HighGpuLoadCandidateStartedAt(DateTimeOffset now)
    {
        var highSamples = Samples
            .Where(sample => sample.ObservedAtUtc >= now - TimeSpan.FromSeconds(15)
                && sample.GpuLoadPercent > 50)
            .OrderBy(sample => sample.ObservedAtUtc)
            .ToArray();
        return highSamples.Length == 0 ? null : highSamples[0].ObservedAtUtc;
    }

    public bool HasLowGpuLoad(DateTimeOffset now, TimeSpan duration) =>
        Samples.Count > 0
        && Samples.All(sample => sample.GpuLoadPercent <= 50)
        && Samples.Min(sample => sample.ObservedAtUtc) <= now - duration;
}

public sealed record AutomationInputs(
    bool IsAcConnected,
    bool IsSessionLocked,
    TelemetryWindow Telemetry,
    ForegroundAppObservation? ForegroundApp,
    ManualOverride? ManualOverride,
    CompatibilityMode CompatibilityMode,
    bool HasConflict);

public sealed record PolicyDecision(
    AutomationState State,
    PerformanceMode TargetMode,
    FanControlPlan? FanPlan,
    IReadOnlyList<PolicyReason> Reasons,
    DateTimeOffset EarliestNextTransitionUtc);
