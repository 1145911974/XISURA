using Jiaolong.Automation;
using Jiaolong.Hardware.Abstractions;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Automation.Tests;

[TestClass]
public sealed class PolicyArbiterTests
{
    [TestMethod]
    public void DecisionUsesTheRequiredPriorityOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var arbiter = new PolicyArbiter();
        var inputs = new AutomationInputs(
            IsAcConnected: true,
            IsSessionLocked: true,
            Telemetry: new TelemetryWindow([
                new TelemetrySample(now, 95, 88, 80, 95, 88)]),
            ForegroundApp: new ForegroundAppObservation("game.exe", "hash", now.AddSeconds(-10), 1),
            ManualOverride: new ManualOverride(PerformanceMode.Quiet, now.AddHours(1)),
            CompatibilityMode: CompatibilityMode.ReadOnlySafeMode,
            HasConflict: true);

        var decision = arbiter.Decide(inputs, new AutomationState(AutomationStateKind.Baseline, now), now);

        Assert.AreEqual(AutomationStateKind.ThermalEmergency, decision.State.Kind);
        CollectionAssert.AreEqual(
            new[] { PolicyReason.ThermalEmergency }, decision.Reasons.ToArray());
    }

    [TestMethod]
    public void LockedSessionDisablesBoostAndReportsSessionLock()
    {
        var now = DateTimeOffset.UtcNow;
        var arbiter = new PolicyArbiter();

        var decision = arbiter.Decide(
            new AutomationInputs(
                true, true, TelemetryWindow.Single(now, 10, 20, 10), null, null,
                CompatibilityMode.Writable, false),
            new AutomationState(AutomationStateKind.Baseline, now), now);

        Assert.AreEqual(AutomationStateKind.SessionLock, decision.State.Kind);
        Assert.AreEqual(PerformanceMode.Balanced, decision.TargetMode);
        CollectionAssert.Contains(decision.Reasons.ToArray(), PolicyReason.SessionLock);
    }
}
