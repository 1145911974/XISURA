using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class MiProviderLifecycleTests
{
    [TestMethod]
    public void Cached_gradient_reads_current_logo_and_preserves_unknown_without_writing()
    {
        using var fixture = new GradientFixture();
        var provider = fixture.Provider;
        var bus = fixture.Bus;
        var type = typeof(WindowsHomeHardwareProvider);
        var original = new KeyboardLightingPlan("Gradient", 100)
        { BrightnessLevel = 3, Red = 255, Green = 165, Blue = 0, Speed = 1.5, LogoEnabled = null };
        var cache = type.GetField("appliedLighting", Private)!;
        cache.SetValue(provider, original);
        KeyboardLightingPlan? Read() => fixture.Read();

        bus.Values[15] = [1];
        Assert.AreEqual(original with { LogoEnabled = true }, Read());
        bus.Values[15] = [0];
        Assert.AreEqual(original with { LogoEnabled = false }, Read());
        cache.SetValue(provider, original with { LogoEnabled = true });
        bus.Values[15] = [2];
        Assert.IsNull(Read(), "Invalid observation must not reuse cached lighting values.");
        bus.Unavailable = true;
        Assert.IsNull(Read());
        Assert.AreEqual(0, bus.Writes);
    }
}
