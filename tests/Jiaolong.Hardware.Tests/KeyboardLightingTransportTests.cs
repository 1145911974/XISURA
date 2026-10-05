using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class KeyboardLightingTransportTests
{
    [TestMethod]
    public async Task Read_hardware_returns_fixed_color_and_effective_off_state()
    {
        var bus = new Bus();
        var controller = Create(bus);
        var active = await controller.ReadHardwareAsync(CancellationToken.None);
        Assert.AreEqual("Fixed", active.Effect);
        Assert.AreEqual(1, active.BrightnessLevel);
        Assert.AreEqual((byte)10, active.Red);
        Assert.AreEqual((byte)20, active.Green);
        Assert.AreEqual((byte)30, active.Blue);
        Assert.IsTrue(active.LogoEnabled);

        bus.Values[16] = [1];
        Assert.AreEqual("Cycle", (await controller.ReadHardwareAsync(CancellationToken.None)).Effect);
        bus.Values[16] = [0];
        var disabled = await controller.ReadHardwareAsync(CancellationToken.None);
        Assert.AreEqual(0, disabled.BrightnessLevel);
        Assert.AreEqual(0, disabled.Brightness);
    }

    [TestMethod]
    public async Task Apply_verifies_all_fields_and_preserves_color_when_legacy_plan_omits_it()
    {
        var bus = new Bus();
        var controller = Create(bus);
        var resolved = await controller.ApplyAsync(new("Gradient", 67) { BrightnessLevel = 2 }, CancellationToken.None);
        Assert.AreEqual((byte)10, resolved.Red);
        Assert.AreEqual((byte)2, bus.Values[16][0]);
        Assert.AreEqual((byte)2, bus.Values[18][0]);
        await controller.ApplyAsync(new("Static", 100) { Red = 100, Green = 110, Blue = 120, LogoEnabled = false }, CancellationToken.None);
        CollectionAssert.AreEqual(new byte[] {100,110,120}, bus.Values[17]);
        Assert.AreEqual((byte)0, bus.Values[15][0]);
        int colorWrites = bus.ColorWrites;
        bus.ResetBrightnessOnCycle = true;
        await controller.ApplyAsync(new("Cycle", 67) { Red = 9, Green = 8, Blue = 7, BrightnessLevel = 2 }, CancellationToken.None);
        Assert.AreEqual((byte)1, bus.Values[16][0]);
        Assert.AreEqual((byte)2, bus.Values[18][0]);
        Assert.AreEqual(colorWrites, bus.ColorWrites);
        CollectionAssert.AreEqual(new byte[] {100,110,120}, bus.Values[17]);
        Assert.AreEqual("Cycle", (await controller.ReadHardwareAsync(CancellationToken.None)).Effect);
        await controller.ApplyAsync(new("Static", 100) { Red = 4, Green = 5, Blue = 6 }, CancellationToken.None);
        Assert.AreEqual((byte)2, bus.Values[16][0]);
        CollectionAssert.AreEqual(new byte[] {4,5,6}, bus.Values[17]);
        await controller.ApplyAsync(new("Cycle", 0), CancellationToken.None);
        Assert.AreEqual((byte)1, bus.Values[16][0]);
        Assert.AreEqual((byte)0, bus.Values[18][0]);
        Assert.AreEqual(colorWrites + 1, bus.ColorWrites);
        Assert.AreEqual(0, (await controller.ReadHardwareAsync(CancellationToken.None)).BrightnessLevel);
        await controller.ApplyAsync(new("Static", 0), CancellationToken.None);
        Assert.AreEqual((byte)2, bus.Values[16][0]);
        Assert.AreEqual((byte)0, bus.Values[18][0]);
    }

    [TestMethod]
    public async Task Readback_mismatch_rolls_back_original_values()
    {
        var bus = new Bus { IgnoreNextColorWrite = true };
        var controller = Create(bus);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.ApplyAsync(
            new("Static", 100) { Red = 100, Green = 110, Blue = 120 }, CancellationToken.None));
        CollectionAssert.AreEqual(new byte[] {10,20,30}, bus.Values[17]);
        Assert.AreEqual((byte)1, bus.Values[18][0]);
        bus.Values[16] = [1]; bus.IgnoreNextColorWrite = true; bus.ResetBrightnessOnCycle = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.ApplyAsync(
            new("Static", 100) { Red = 80, Green = 90, Blue = 100 }, CancellationToken.None));
        Assert.AreEqual((byte)1, bus.Values[16][0]);
        CollectionAssert.AreEqual(new byte[] {10,20,30}, bus.Values[17]);
        Assert.AreEqual("Cycle", (await controller.ReadHardwareAsync(CancellationToken.None)).Effect);
        Assert.AreEqual((byte)1, bus.Values[18][0]);
    }

    [TestMethod]
    public void Unsigned_or_unavailable_capability_cannot_construct_write_transport()
    {
        var decision = Decision() with { Mode = CompatibilityMode.ReadOnlySafeMode };
        Assert.ThrowsExactly<InvalidOperationException>(() => new MiKeyboardLightingController(new(new Bus(), new Bus()), decision));
    }

    private static MiKeyboardLightingController Create(Bus bus) => new(new(bus, bus), Decision());
    private static CompatibilityDecision Decision() => new(CompatibilityMode.Writable,
        new CapabilitySnapshot([new("keyboardLighting", CapabilityState.Available, null), new("quickSetting:lidLogo", CapabilityState.Available, null)]),
        new CompatibilityManifest { ManifestId = "test", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "test", ReadType = 250, WriteType = 251 } }, []);

    private sealed class Bus : IMiReadTransport, IMiWriteTransport
    {
        public Dictionary<byte, byte[]> Values { get; } = new() { [16] = [2], [17] = [10,20,30], [18] = [1], [15] = [1] };
        public bool IgnoreNextColorWrite { get; set; }
        public int ColorWrites { get; private set; }
        public bool ResetBrightnessOnCycle { get; set; }
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        {
            var result = new byte[32];
            Values[binding.MethodName].CopyTo(result, 4);
            return Task.FromResult<byte[]?>(result);
        }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            if (binding.MethodName == 17) ColorWrites++;
            if (binding.MethodName == 17 && IgnoreNextColorWrite) IgnoreNextColorWrite = false;
            else Values[binding.MethodName] = request.Slice(4, binding.MethodName == 17 ? 3 : 1).ToArray();
            if (binding.MethodName == 16 && Values[16][0] == 0) Values[16] = [2];
            if (binding.MethodName == 16 && Values[16][0] == 1 && ResetBrightnessOnCycle) Values[18] = [3];
            return Task.FromResult<byte[]?>([]);
        }
    }
}
