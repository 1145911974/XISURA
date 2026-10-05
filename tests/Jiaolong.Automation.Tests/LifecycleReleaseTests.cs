using Jiaolong.Automation;

namespace Jiaolong.Automation.Tests;

[TestClass]
public sealed class LifecycleReleaseTests
{
    [TestMethod]
    public async Task StopAndSuspendReleaseFanOwnership()
    {
        var arbiter = new AutomationEngine(new PolicyArbiter());
        var releases = new List<AutomationReleaseReason>();

        await arbiter.ReleaseAsync(AutomationReleaseReason.ServiceStopping, releases.Add);
        await arbiter.ReleaseAsync(AutomationReleaseReason.SystemSuspend, releases.Add);

        CollectionAssert.AreEqual(
            new[] { AutomationReleaseReason.ServiceStopping, AutomationReleaseReason.SystemSuspend },
            releases.ToArray());
    }
}
