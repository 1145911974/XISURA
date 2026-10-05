using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;

namespace Jiaolong.Automation;

public sealed class PolicyArbiter
{
    private static readonly FanControlPlan EmergencyFanPlan = new(
        [new FanPoint(60, 70), new FanPoint(80, 100)]);

    public PolicyDecision Decide(
        AutomationInputs inputs,
        AutomationState previous,
        DateTimeOffset now)
    {
        if (inputs.Telemetry.IsThermalEmergency())
        {
            return Decision(AutomationStateKind.ThermalEmergency, PerformanceMode.Balanced,
                EmergencyFanPlan, PolicyReason.ThermalEmergency, now);
        }

        if (inputs.HasConflict || inputs.CompatibilityMode != CompatibilityMode.Writable)
        {
            return Decision(AutomationStateKind.CompatibilityOrConflict, PerformanceMode.Balanced,
                null, PolicyReason.CompatibilityOrConflict, now);
        }

        if (!inputs.IsAcConnected)
        {
            return Decision(AutomationStateKind.DcPolicy, PerformanceMode.Balanced,
                null, PolicyReason.Dc, now);
        }

        if (inputs.IsSessionLocked)
        {
            return Decision(AutomationStateKind.SessionLock, PerformanceMode.Balanced,
                null, PolicyReason.SessionLock, now);
        }

        if (inputs.ManualOverride is { } manual && manual.IsActive(now))
        {
            return Decision(AutomationStateKind.ManualOverride, manual.Mode,
                null, PolicyReason.ManualOverride, now);
        }

        if (inputs.ForegroundApp is { } app && now - app.ObservedAtUtc >= TimeSpan.FromSeconds(5))
        {
            return Decision(AutomationStateKind.ForegroundApp, PerformanceMode.Turbo,
                null, PolicyReason.ForegroundApp, now);
        }

        if (inputs.Telemetry.HasHighGpuLoad(now))
        {
            var holdUntil = previous.Kind == AutomationStateKind.AdaptiveLoad
                ? previous.EnteredAtUtc + TimeSpan.FromSeconds(90)
                : now + TimeSpan.FromSeconds(90);
            return Decision(AutomationStateKind.AdaptiveLoad, PerformanceMode.Turbo,
                null, PolicyReason.AdaptiveLoad, now, holdUntil);
        }

        if (previous.Kind == AutomationStateKind.AdaptiveLoad
            && now - previous.EnteredAtUtc < TimeSpan.FromSeconds(90))
        {
            return Decision(AutomationStateKind.AdaptiveLoad, PerformanceMode.Turbo,
                null, PolicyReason.AdaptiveLoad, now,
                previous.EnteredAtUtc + TimeSpan.FromSeconds(90));
        }

        if (inputs.Telemetry.HasLowGpuLoad(now, TimeSpan.FromSeconds(180)))
        {
            return Decision(AutomationStateKind.Baseline, PerformanceMode.Quiet,
                null, PolicyReason.Baseline, now);
        }

        return Decision(AutomationStateKind.Baseline, PerformanceMode.Balanced,
            null, PolicyReason.Baseline, now);
    }

    private static PolicyDecision Decision(
        AutomationStateKind state,
        PerformanceMode targetMode,
        FanControlPlan? fanPlan,
        PolicyReason reason,
        DateTimeOffset now,
        DateTimeOffset? earliestNextTransitionUtc = null) =>
        new(new AutomationState(state, now), targetMode, fanPlan, [reason],
            earliestNextTransitionUtc ?? now + TimeSpan.FromSeconds(5));
}
