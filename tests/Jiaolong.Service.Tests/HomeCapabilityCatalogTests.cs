using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using System.Reflection;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class HomeCapabilityCatalogTests
{
    [TestMethod]
    public void Fractional_cpu_readback_is_readable_without_granting_oem_write_access()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        var method = typeof(WindowsHomeHardwareProvider).GetMethod("CreateFieldCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cpu = new CpuTuningState(99, null, null, 0, true, 8, Guid.NewGuid(), -30, "hardwareReadback")
        { ObservedSplWatts = 40.5, AdvancedLimits = new(FastPptWatts: 58.5, PptWatts: 120.125, Mp1TemperatureC: 99) };
        var fields = (FieldCapability[])method.Invoke(provider,
            [new CapabilityDescriptor[] { new("cpuTuning", CapabilityState.Available, null), new("cpuTuning:smu", CapabilityState.Available, null) }, cpu])!;
        foreach (string key in new[] { "splWatts", "spptWatts" })
        {
            var field = fields.Single(item => item.Key == key);
            Assert.IsTrue(field.Readable, key);
            Assert.IsFalse(field.Writable, key);
            Assert.AreEqual("Unknown", field.NativeModeSideEffect);
        }
        foreach (string key in new[] { "pptWatts", "mp1TemperatureC" })
        {
            var field = fields.Single(item => item.Key == key);
            Assert.IsTrue(field.Readable, key);
            Assert.IsFalse(field.Writable, key);
            Assert.AreEqual(CapabilityState.ReadOnly, field.Availability);
            Assert.IsFalse(PresetValidation.IsVerifiedWritable(field, 0));
            Assert.AreEqual(key == "pptWatts" ? 0.001 : 1, field.Step);
        }
    }

    [TestMethod]
    public void Gpu_capabilities_require_usable_live_readback()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        var cache = typeof(WindowsHomeHardwareProvider).GetField("gpuVfCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var create = typeof(WindowsHomeHardwareProvider).GetMethod("CreateFieldCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CapabilityDescriptor[] items = [new("gpuVfCurve", CapabilityState.Available, null),
            new("gpuCoreOffset", CapabilityState.Available, null), new("gpuMemoryOffset", CapabilityState.Available, null)];
        var nodes = Enumerable.Repeat(new GpuVfNode(800, 1500, 0), 127).ToArray();
        foreach (var (state, vfWritable, offsetsWritable) in new[]
        {
            (new GpuVfState(nodes, -200_000, 200_000, "gpuVfReadFailed")
                { CoreOffsetKhz = 0, CoreMinimumOffsetKhz = -200_000, CoreMaximumOffsetKhz = 200_000,
                  MemoryOffsetKhz = 0, MemoryMinimumOffsetKhz = -200_000, MemoryMaximumOffsetKhz = 200_000 }, false, false),
            (new GpuVfState(nodes, -200_000, 200_000, null)
                { CoreOffsetKhz = 0, CoreMinimumOffsetKhz = 10_000, CoreMaximumOffsetKhz = -10_000,
                  MemoryOffsetKhz = 300_000, MemoryMinimumOffsetKhz = -200_000, MemoryMaximumOffsetKhz = 200_000 }, true, false),
            (new GpuVfState(nodes, -200_000, 200_000, null)
                { CoreOffsetKhz = 0, CoreMinimumOffsetKhz = -200_000, CoreMaximumOffsetKhz = 200_000,
                  MemoryOffsetKhz = 0, MemoryMinimumOffsetKhz = -200_000, MemoryMaximumOffsetKhz = 200_000 }, true, true)
        })
        {
            cache.SetValue(provider, state);
            var fields = (FieldCapability[])create.Invoke(provider, [items, null])!;
            var summary = HomeCapabilityCatalog.WithFieldAvailability(items, fields);
            foreach (string key in new[] { "coreOffsetKhz", "memoryOffsetKhz" })
                Assert.AreEqual(offsetsWritable, fields.Single(field => field.Key == key).Writable, key);
            Assert.AreEqual(vfWritable, fields.Single(field => field.Key == "vfOffsetsKhz").Writable);
            Assert.AreEqual(vfWritable, summary.Single(item => item.Key == "gpuVfCurve").State == CapabilityState.Available);
            Assert.AreEqual(offsetsWritable, summary.Single(item => item.Key == "gpuCoreOffset").State == CapabilityState.Available);
            Assert.AreEqual(offsetsWritable, summary.Single(item => item.Key == "gpuMemoryOffset").State == CapabilityState.Available);
        }
    }

    [TestMethod]
    public void Unverified_oem_limits_do_not_claim_to_preserve_the_selected_mode()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        var method = typeof(WindowsHomeHardwareProvider).GetMethod("CreateFieldCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cpu = new CpuTuningState(99, 45, 55, 4700, true, 8, Guid.NewGuid(), -30, "hardwareReadback");
        var fields = (FieldCapability[])method.Invoke(provider,
            [new CapabilityDescriptor[] { new("cpuTuning", CapabilityState.Available, null) }, cpu])!;
        foreach (string key in new[] { "temperatureLimitC", "splWatts", "spptWatts" })
        {
            var field = fields.Single(item => item.Key == key);
            Assert.IsFalse(field.Writable);
            Assert.AreEqual("Unknown", field.NativeModeSideEffect, key);
            Assert.AreEqual("oemModeSideEffectUnknown", field.Source);
        }
        Assert.AreEqual("PreservesSelectedMode", fields.Single(item => item.Key == "acMaxFrequencyMhz").NativeModeSideEffect);
    }

    [TestMethod]
    public void Feature_summaries_follow_their_field_and_preserve_other_capabilities()
    {
        CapabilityDescriptor[] items = [new("cpuTuning:smu", CapabilityState.Available, null),
            new("gpuFrequencyLimit", CapabilityState.Available, null), new("cpuTuning", CapabilityState.Available, null),
            new("cpuTuning:pbo", CapabilityState.Available, null), new("gpuCoreOffset", CapabilityState.Unavailable, "gpuCoreRecoveryRequired")];
        FieldCapability Field(string key, bool readable, string source) => new(key,
            readable ? CapabilityState.ReadOnly : CapabilityState.Unavailable, readable, false,
            null, null, null, "", source, "Unknown", null, 1);
        FieldCapability[] fields = [Field("advancedCpuTuning", true, "advancedProtocolUnsupported"),
            Field("coreFrequencyLimitMhz", false, "nvidiaSmiRangeOnly")];
        var result = HomeCapabilityCatalog.WithFieldAvailability(items, fields);
        Assert.AreEqual(CapabilityState.ReadOnly, result[0].State);
        Assert.AreEqual("advancedProtocolUnsupported", result[0].Reason);
        Assert.AreEqual(CapabilityState.Unavailable, result[1].State);
        Assert.AreEqual("gpuClockLimitReadbackUnavailable", result[1].Reason);
        CollectionAssert.AreEqual(items.Skip(2).ToArray(), result.Skip(2).ToArray());
        var missing = HomeCapabilityCatalog.WithFieldAvailability(items, []);
        Assert.AreEqual(CapabilityState.Unavailable, missing[0].State);
        Assert.AreEqual(CapabilityState.Unavailable, missing[1].State);
        items[0] = items[0] with { State = CapabilityState.Unavailable, Reason = "advancedCpuProtocolUnavailable" };
        result = HomeCapabilityCatalog.WithFieldAvailability(items, fields);
        Assert.AreEqual(items[0], result[0]);
    }

    [TestMethod]
    public void Mux_command_requires_the_mux_mode_capability()
    {
        var command = new SetMuxModeCommand(Guid.NewGuid(), MuxMode.Discrete, UserConfirmedRestartImpact: true);

        Assert.AreEqual("muxMode", HomeCapabilityCatalog.RequiredCapability(command));
    }

    [TestMethod]
    public void Gpu_vf_command_requires_its_own_verified_capability()
    {
        var command = new SetGpuVfCurveCommand(Guid.NewGuid(), new int[127], new int[127], true);
        Assert.AreEqual("gpuVfCurve", HomeCapabilityCatalog.RequiredCapability(command));
    }
}
