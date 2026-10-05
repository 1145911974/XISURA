using Jiaolong.Automation;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions;
using Jiaolong.Hardware.Abstractions.Compatibility;

namespace Jiaolong.Automation.Tests;

[TestClass]
public sealed class AdaptiveStateMachineTests
{
    [TestMethod]
    public void HighGpuLoadChoosesAdaptiveTurboAndKeepsTransitionDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var arbiter = new PolicyArbiter();
        var decision = arbiter.Decide(
            new AutomationInputs(true, false, TelemetryWindow.Single(now.AddSeconds(-10), 40, 55, 70), null, null,
                CompatibilityMode.Writable, false),
            new AutomationState(AutomationStateKind.Baseline, now.AddMinutes(-5)), now);

        Assert.AreEqual(AutomationStateKind.AdaptiveLoad, decision.State.Kind);
        Assert.AreEqual(PerformanceMode.Turbo, decision.TargetMode);
        Assert.IsTrue(decision.EarliestNextTransitionUtc > now);
    }
}
