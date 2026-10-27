using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ProcessInputMethodAttemptGateTests
{
    [TestMethod]
    public void Missing_target_or_activation_failure_allows_one_delayed_retry_per_session()
    {
        var gate = new ProcessInputMethodAttemptGate(TimeSpan.FromSeconds(15));
        var start = DateTimeOffset.UnixEpoch;

        Assert.IsTrue(gate.ShouldAttempt(start));
        gate.Failed(start);
        Assert.IsFalse(gate.ShouldAttempt(start.AddSeconds(14)));
        Assert.IsTrue(gate.ShouldAttempt(start.AddSeconds(15)));
        gate.Failed(start.AddSeconds(15));
        Assert.IsFalse(gate.ShouldAttempt(start.AddHours(1)));
    }

    [TestMethod]
    public void Successful_activation_clears_retry_limit_for_a_later_external_switch()
    {
        var gate = new ProcessInputMethodAttemptGate(TimeSpan.FromSeconds(15));
        var start = DateTimeOffset.UnixEpoch;

        gate.Failed(start);
        Assert.IsTrue(gate.ShouldAttempt(start.AddSeconds(15)));
        gate.Succeeded();
        Assert.IsTrue(gate.ShouldAttempt(start.AddSeconds(16)));
        gate.Failed(start.AddSeconds(16));
        Assert.IsFalse(gate.ShouldAttempt(start.AddSeconds(20)));
        Assert.IsTrue(gate.ShouldAttempt(start.AddSeconds(31)));
    }
}
