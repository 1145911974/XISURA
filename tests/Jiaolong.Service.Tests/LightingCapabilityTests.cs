using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using System.Reflection;

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
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var create = typeof(WindowsHomeHardwareProvider).GetMethod("CreateFieldCapabilities", flags)!;
        var light = new KeyboardLightingPlan("Fixed", 100)
        { BrightnessLevel = 3, Red = 1, Green = 2, Blue = 3, LogoEnabled = true };
        CapabilityDescriptor[] items = [new("keyboardLighting", CapabilityState.Available, null),
            new("quickSetting:lidLogo", CapabilityState.Available, null)];
        var fields = (FieldCapability[])create.Invoke(provider, [items, null])!;
        foreach (string field in new[] { "effect", "brightness", "hex", "speed", "logo" })
            Assert.IsTrue(fields.Single(item => item.Key == field).Readable, field);
        var withoutLogo = (FieldCapability[])create.Invoke(provider, [items.Take(1).ToArray(), null])!;
        Assert.IsFalse(withoutLogo.Single(item => item.Key == "logo").Readable);
        var missing = (FieldCapability[])create.Invoke(provider, [Array.Empty<CapabilityDescriptor>(), null])!;
        Assert.IsFalse(missing.Single(item => item.Key == "speed").Readable);

        var runtime = new HomeServiceRuntime(provider);
        fields = fields.Select(field => field with { HardwareEpoch = runtime.Execution.HardwareEpoch }).ToArray();
        typeof(HomeServiceRuntime).GetField("state", flags)!.SetValue(runtime,
            new HomeHardwareState(new(items) { Fields = fields }, DeviceSupportState.Ready, null, new("test", "test", "test", "test"), null));
        var readable = typeof(HomeServiceRuntime).GetMethod("FieldReadable", flags)!;
        var controls = new HomeControlState(null, null, []) { KeyboardLighting = light };
        bool Readable(string field, HomeControlState state) => (bool)readable.Invoke(runtime, [field, state])!;
        Assert.IsTrue(Readable("speed", controls));
        var gradient = controls with { KeyboardLighting = light with { Effect = "Gradient", Speed = 1.5 }, KeyboardLightingElapsedSeconds = 1 };
        foreach (string field in new[] { "effect", "speed" })
        {
            Assert.IsTrue(Readable(field, gradient));
            Assert.IsFalse(Readable(field, gradient with { KeyboardLightingElapsedSeconds = null }));
        }
        Assert.IsFalse(Readable("speed", controls with { KeyboardLighting = light with { Effect = "Cycle" } }));
        var matches = typeof(WindowsHomeHardwareProvider).GetMethod("MatchesLightingFrame", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Matches(KeyboardLightingPlan observed) => (bool)matches.Invoke(null, [observed, gradient.KeyboardLighting, new KeyboardRgb(1, 2, 3)])!;
        Assert.IsTrue(Matches(light));
        Assert.IsFalse(Matches(light with { Red = 99 }));
        Assert.IsFalse(Matches(light with { BrightnessLevel = 2 }));
        provider.StopClientLightingDynamicsAsync(CancellationToken.None).GetAwaiter().GetResult();
        provider.StopClientLightingDynamicsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var lost = typeof(WindowsHomeHardwareProvider).GetField("lightingOwnershipLost", flags)!;
        lost.SetValue(provider, true);
        provider.StopClientLightingDynamicsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var preview = (LightingPreviewLease)typeof(WindowsHomeHardwareProvider).GetField("lightingPreview", flags)!.GetValue(provider)!;
        preview.Begin(light, DateTimeOffset.UtcNow);
        Assert.ThrowsExactly<IOException>(() => provider.StopClientLightingDynamicsAsync(CancellationToken.None));
        preview.Complete();
        var active = typeof(WindowsHomeHardwareProvider).GetField("appliedLighting", flags)!;
        active.SetValue(provider, gradient.KeyboardLighting);
        try { Assert.ThrowsExactly<IOException>(() => provider.StopClientLightingDynamicsAsync(CancellationToken.None)); }
        finally { active.SetValue(provider, null); lost.SetValue(provider, false); }
    }
}
