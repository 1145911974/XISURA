using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class GpuPstateRangeTests
{
    [TestMethod]
    public void Wider_offsets_survive_balanced_baseline_presets_and_command_bounds()
    {
        var identity = new HardwareIdentity("MRID6-23", "V39", "7745HX", "RTX 4070");
        var field = new FieldCapability("coreOffsetKhz", CapabilityState.Available, true, true, 300_000, 700_000, 1000,
            "kHz", "nvapiReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1);
        var caps = new CapabilitySnapshot([]) { Identity = identity, Fields = [field,
            field with { Key = "memoryOffsetKhz", Minimum = -1_000_000, Maximum = 3_000_000 }] };
        var baseline = new DeviceSchedulingBaseline(1, identity, DateTimeOffset.UtcNow, PerformanceMode.Balanced,
            new(99, 54, 108, 4700, true, 8, null, -50, "hardwareReadback"))
            { Gpu = new(500_000, 500_000, new int[127]) };
        var preset = DefaultControlPresets.Create(ControlPageId.Gpu, PresetKey.Create(ControlModeId.Gaming, 2), caps, baseline);
        foreach (string key in new[] { "coreOffsetKhz", "memoryOffsetKhz" })
        {
            Assert.AreEqual(SettingIntent.Set, preset.Settings[key].Intent);
            Assert.AreEqual(500_000, preset.Settings[key].Value!.Value.GetInt32());
        }
        Assert.IsTrue(PresetValidation.ValidatePayload(ControlPageId.Gpu, preset.Payload).IsValid);
        foreach (HardwareCommand valid in new HardwareCommand[] { new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 3_000_000, true),
            new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 1_000_000, true) })
            Assert.IsNull(CommandValidation.Validate(valid));
        foreach (HardwareCommand invalid in new HardwareCommand[] { new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 3_001_000, true),
            new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, -1_001_000, true), new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 500_001, true),
            new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 1000, false) })
            Assert.IsNotNull(CommandValidation.Validate(invalid));
    }
}
