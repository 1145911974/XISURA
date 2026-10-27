using System.Reflection;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ExistingControlAvailabilityTests
{
    [TestMethod]
    public void Implemented_fan_controls_are_exposed_when_the_device_supports_them()
    {
        using var provider = new WindowsHomeHardwareProvider();
        var fields = Fields(provider, CapabilityState.Available);
        foreach (var key in new[] { "strategy", "maximumRpm", "fixedRpm", "cpu", "gpu", "shared", "isShared", "strongCooling" })
        {
            var field = fields.Single(value => value.Key == key);
            Assert.IsTrue(field.Writable, key);
            Assert.AreEqual(CapabilityState.Available, field.Availability, key);
        }
        var fixedRpm = fields.Single(value => value.Key == "fixedRpm");
        Assert.AreEqual("RPM", fixedRpm.Unit);
        Assert.AreEqual(1800d, fixedRpm.Minimum);
        Assert.AreEqual(5800d, fixedRpm.Maximum);
    }

    [TestMethod]
    public void Unsupported_device_cannot_gain_fan_writes_from_the_interface()
    {
        using var provider = new WindowsHomeHardwareProvider();
        foreach (var field in Fields(provider, CapabilityState.Unavailable)
            .Where(value => value.Key is "strategy" or "maximumRpm" or "fixedRpm" or "cpu" or "gpu" or "shared" or "isShared" or "strongCooling"))
            Assert.IsFalse(field.Writable, field.Key);
    }

    private static FieldCapability[] Fields(WindowsHomeHardwareProvider provider, CapabilityState state) =>
        (FieldCapability[])typeof(WindowsHomeHardwareProvider)
            .GetMethod("CreateFieldCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider, [new CapabilityDescriptor[] { new("fanControl", state, null), new("strongCooling", state, null) }, null, null])!;
}
