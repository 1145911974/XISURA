using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class GpuPstateRangeTests
{
    [TestMethod]
    public void Offset_capabilities_preserve_driver_memory_range_and_current_curve_headroom()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        var cache = typeof(WindowsHomeHardwareProvider).GetField("gpuVfCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var create = typeof(WindowsHomeHardwareProvider).GetMethod("CreateFieldCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CapabilityDescriptor[] items = [new("gpuCoreOffset", CapabilityState.Available, null), new("gpuMemoryOffset", CapabilityState.Available, null)];
        var state = new GpuVfState(Enumerable.Range(0, 127).Select(i => new GpuVfNode(800, 1500, i is > 0 and < 60 ? 75_000 : 0)).ToArray(), -200_000, 200_000, null)
        { CoreOffsetKhz = 0, CoreMinimumOffsetKhz = -1_000_000, CoreMaximumOffsetKhz = 1_000_000,
          MemoryOffsetKhz = 500_000, MemoryMinimumOffsetKhz = -1_000_000, MemoryMaximumOffsetKhz = 3_000_000 };
        FieldCapability[] Fields(GpuVfState value)
        {
            cache.SetValue(provider, value);
            return (FieldCapability[])create.Invoke(provider, [items, null, null])!;
        }
        foreach (int currentCore in new[] { 0, 500_000 })
        {
            var fields = Fields(state with { CoreOffsetKhz = currentCore });
            var memory = fields.Single(field => field.Key == "memoryOffsetKhz");
            Assert.IsTrue(memory.Writable);
            Assert.AreEqual(-1_000_000d, memory.Minimum);
            Assert.AreEqual(3_000_000d, memory.Maximum);
            var core = fields.Single(field => field.Key == "coreOffsetKhz");
            Assert.IsTrue(core.Writable);
            Assert.AreEqual(-1_000_000d, core.Minimum);
            Assert.AreEqual(1_000_000d, core.Maximum);
            var effective = (state with { CoreOffsetKhz = currentCore }).PstateOffsetRange(memory: false)!.Value;
            Assert.AreEqual(currentCore - 200_000, effective.MinimumKhz);
            Assert.AreEqual(currentCore + 125_000, effective.MaximumKhz);
        }
        var narrower = Fields(state with { MemoryMinimumOffsetKhz = -50_001, MemoryMaximumOffsetKhz = 100_999, MemoryOffsetKhz = 0 });
        Assert.AreEqual(-50_000d, narrower.Single(field => field.Key == "memoryOffsetKhz").Minimum);
        Assert.AreEqual(100_000d, narrower.Single(field => field.Key == "memoryOffsetKhz").Maximum);
        foreach (var invalid in new[] { state with { Error = "readFailed" }, state with { Nodes = [] },
            state with { MemoryMinimumOffsetKhz = 10, MemoryMaximumOffsetKhz = -10 } })
            Assert.IsFalse(Fields(invalid).Single(field => field.Key == "memoryOffsetKhz").Writable);
    }

    [TestMethod]
    public void Wider_memory_transaction_preserves_curve_and_adjacent_core_and_rechecks_driver_bounds()
    {
        var bus = new GpuVfLifecycleTests.FakeGpu();
        using var provider = new WindowsHomeHardwareProvider(bus.Create, () => true, _ => 50);
        typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider,
            new CompatibilityDecision(CompatibilityMode.Writable,
                new CapabilitySnapshot([new("gpuVfCurve", CapabilityState.Available, null)]), null, []));
        int[] original = bus.Offsets.ToArray();
        CommandResult Apply(int expected, int target)
        {
            var command = new SetGpuMemoryOffsetCommand(Guid.NewGuid(), expected, target, true);
            Assert.IsNull(CommandValidation.Validate(command));
            return (CommandResult)typeof(WindowsHomeHardwareProvider)
                .GetMethod("ExecuteGpuMemoryOffsetLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(provider, [command, CancellationToken.None])!;
        }
        Assert.AreEqual(CommandState.Applied, Apply(0, 500_000).State);
        Assert.AreEqual(500_000, bus.MemoryOffset);
        Assert.AreEqual(0, bus.CoreOffset);
        CollectionAssert.AreEqual(original, bus.Offsets);
        Assert.AreEqual(CommandState.Applied, Apply(500_000, 0).State);
        Assert.AreEqual(0, bus.MemoryOffset);
        CollectionAssert.AreEqual(original, bus.Offsets);
        int writes = bus.Writes;
        Assert.AreEqual(CommandState.Rejected, Apply(0, 3_000_000).State);
        Assert.AreEqual(writes, bus.Writes);
    }
}
