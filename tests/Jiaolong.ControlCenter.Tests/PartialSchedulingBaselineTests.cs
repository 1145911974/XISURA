using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PartialSchedulingBaselineTests
{
    private static HomeStateSnapshot Snapshot() => new(
        new CapabilitySnapshot([])
        { Identity = new("MRID6-23", "V35", "7745HX", "RTX 4070"), SupportState = DeviceSupportState.Ready },
        null, new(PerformanceMode.Turbo, false, [])
        {
            CpuTuning = new(null, null, null, null, true, 8,
                Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), -50, "hardwareReadback")
            { AcMaxFrequencyMhz = 4700, DcMaxFrequencyMhz = 4500, AcBoostMode = 2, DcBoostMode = 2, PboScalar = 1 }
        });

    [TestMethod]
    public async Task Partial_baseline_preserves_unknowns_first_values_and_device_identity()
    {
        var paths = new RecordingPathProvider();
        var firstState = Snapshot();
        var first = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(firstState, default, allowPartial: true);
        Assert.IsNotNull(first);
        Assert.AreEqual(2, first.SchemaVersion);
        Assert.IsNull(first.Cpu.TemperatureLimitC);
        Assert.IsNull(first.Cpu.SplWatts);
        Assert.IsNull(first.Cpu.SpptWatts);
        Assert.IsNull(first.Gpu);
        Assert.IsNull(first.Fan);
        Assert.AreEqual(firstState.Controls.StrongCooling, first.StrongCooling);
        Assert.IsNotNull(first.CapturedCpuFields);
        CollectionAssert.IsSubsetOf(new[] { "acMaxFrequencyMhz", "dcMaxFrequencyMhz", "windowsPowerSchemeId", "isBoostEnabled", "negativeCurveOptimizer" }, first.CapturedCpuFields);
        Assert.IsFalse(first.CapturedCpuFields.Contains("splWatts"));
        string path = paths.WrittenPaths.Single();
        byte[] original = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new DeviceSchedulingBaselineStore(paths)
            .LoadOrCaptureAsync(firstState, default));
        var cached = new DeviceSchedulingBaselineStore(paths);
        await cached.LoadOrCaptureAsync(firstState, default, allowPartial: true);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => cached.LoadOrCaptureAsync(firstState, default));
        var changed = firstState with { Controls = firstState.Controls with
        { CpuTuning = firstState.Controls.CpuTuning! with { AcMaxFrequencyMhz = 4200, SplWatts = 54, SpptWatts = 108 } } };
        var reopened = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(changed, default, allowPartial: true);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(4700, reopened.Cpu.AcMaxFrequencyMhz);
        Assert.IsNull(reopened.Cpu.SplWatts);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        var other = firstState with { Capabilities = firstState.Capabilities with
        { Identity = firstState.Capabilities.Identity! with { BiosVersion = "V36" } } };
        var isolated = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(other, default, allowPartial: true);
        Assert.IsNotNull(isolated);
        Assert.AreEqual(other.Capabilities.Identity, isolated.Identity);
        Assert.HasCount(2, paths.WrittenPaths);
        Assert.AreNotEqual(paths.WrittenPaths[0], paths.WrittenPaths[1]);
    }

    [TestMethod]
    public async Task Partial_capture_rejects_invalid_known_oem_missing_windows_and_non_native_modes()
    {
        var state = Snapshot();
        var paths = new RecordingPathProvider();
        var complete = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(state, default, allowPartial: true);
        Assert.IsNotNull(complete);
        foreach (var invalid in new[]
        {
            state with { Controls = state.Controls with { PerformanceMode = PerformanceMode.Custom } },
            state with { Controls = state.Controls with { CpuTuning = state.Controls.CpuTuning! with { DcMaxFrequencyMhz = null } } },
            state with { Controls = state.Controls with { CpuTuning = state.Controls.CpuTuning! with { WindowsPowerSchemeId = Guid.Empty } } },
            state with { Controls = state.Controls with { CpuTuning = state.Controls.CpuTuning! with { BoostEnabled = null } } },
            state with { Controls = state.Controls with { CpuTuning = state.Controls.CpuTuning! with { SplWatts = 106 } } },
            state with { Controls = state.Controls with { CpuTuning = state.Controls.CpuTuning! with { SplWatts = 70, SpptWatts = 60 } } },
            state with { Controls = state.Controls with { CpuTuning = state.Controls.CpuTuning! with { SplWatts = 54, ObservedSplWatts = 55 } } },
            state with { Controls = state.Controls with { CpuTuning = state.Controls.CpuTuning! with { ObservedSplWatts = 70, SpptWatts = 60 } } }
        })
        {
            var rejected = new RecordingPathProvider();
            Assert.IsNull(await new DeviceSchedulingBaselineStore(rejected).LoadOrCaptureAsync(invalid, default, allowPartial: true));
            Assert.HasCount(0, rejected.WrittenPaths);
        }
    }

    [TestMethod]
    public async Task Legacy_baseline_remains_strict_and_corrupt_coverage_is_preserved()
    {
        var state = Snapshot();
        var cpu = state.Controls.CpuTuning!;
        var paths = new RecordingPathProvider();
        var legacy = state with { Controls = state.Controls with
        { CpuTuning = cpu with { TemperatureLimitC = 99, SplWatts = 54, SpptWatts = 108 } } };
        var first = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(legacy, default);
        Assert.IsNotNull(first);
        Assert.AreEqual(1, first.SchemaVersion);
        string path = paths.WrittenPaths.Single();
        byte[] original = await File.ReadAllBytesAsync(path);
        var reopened = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(state, default, allowPartial: true);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(1, reopened.SchemaVersion);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        var malformed = first with { Cpu = cpu };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(malformed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        byte[] invalidV1 = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new DeviceSchedulingBaselineStore(paths)
            .LoadOrCaptureAsync(state, default, allowPartial: true));
        CollectionAssert.AreEqual(invalidV1, await File.ReadAllBytesAsync(path));

        var partialPaths = new RecordingPathProvider();
        var partial = await new DeviceSchedulingBaselineStore(partialPaths).LoadOrCaptureAsync(state, default, allowPartial: true);
        Assert.IsNotNull(partial);
        string partialPath = partialPaths.WrittenPaths.Single();
        await File.WriteAllTextAsync(partialPath, JsonSerializer.Serialize(partial with { CapturedCpuFields = ["splWatts"] },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        byte[] invalidV2 = await File.ReadAllBytesAsync(partialPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new DeviceSchedulingBaselineStore(partialPaths)
            .LoadOrCaptureAsync(state, default, allowPartial: true));
        CollectionAssert.AreEqual(invalidV2, await File.ReadAllBytesAsync(partialPath));
        Assert.HasCount(1, partialPaths.WrittenPaths);
    }
}
