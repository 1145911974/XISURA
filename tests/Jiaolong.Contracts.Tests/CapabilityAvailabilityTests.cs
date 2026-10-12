using Jiaolong.Contracts.Models;

namespace Jiaolong.Contracts.Tests;

[TestClass]
public sealed class CapabilityAvailabilityTests
{
    [TestMethod]
    public void Presence_does_not_make_unavailable_controls_actionable()
    {
        var capabilities = new CapabilitySnapshot([
            new("performanceMode", CapabilityState.Available, null),
            new("fanControl", CapabilityState.Unavailable, "bldingDependencyMissing"),
            new("keyboardLighting", CapabilityState.ReadOnly, "driverUnavailable")]);

        Assert.IsTrue(capabilities.IsAvailable("PERFORMANCEMODE"));
        Assert.IsFalse(capabilities.IsAvailable("fanControl"));
        Assert.IsFalse(capabilities.IsAvailable("keyboardLighting"));
        Assert.IsFalse(capabilities.IsAvailable("missing"));
    }
}
