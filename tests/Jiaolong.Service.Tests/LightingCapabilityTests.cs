using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class LightingCapabilityTests
{
    [TestMethod]
    public void Lighting_is_a_semantic_capability_and_remains_readonly_without_verified_evidence()
    {
        Assert.AreEqual("keyboardLighting", HomeCapabilityCatalog.RequiredCapability(
            new SetKeyboardLightingCommand(Guid.NewGuid(), new("Static", 100))));
        var denied = HomeCapabilityCatalog.ReadOnlyControls("unverified").Single(item => item.Key == "keyboardLighting");
        Assert.AreEqual(CapabilityState.ReadOnly, denied.State);
        Assert.AreEqual("unverified", denied.Reason);
    }
}
