using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class WindowsCpuPowerSnapshotTests
{
    [TestMethod]
    public async Task Windows_only_capture_needs_no_smu_and_oem_capture_still_requires_a_baseline()
    {
        var power = new SnapshotTransport
        {
            Snapshot = new(Guid.NewGuid(), 3800, 3000, 2, 0)
            { AcMinActiveCoresPercent = 17, DcMinActiveCoresPercent = 10 }
        };
        var transport = new WindowsCpuTuningTransport(null!, null!, power);
        var windows = new Jiaolong.Contracts.Commands.CpuTuningPlan(null, null, null, 3800, false, null, null, null);
        await transport.CaptureTransactionAsync([windows], true, CancellationToken.None);
        await transport.RestoreTransactionAsync(_ => Task.CompletedTask, CancellationToken.None);
        Assert.AreEqual(4, power.RestoreCount);
        await transport.RestoreCapturedBoostModesAsync(4, 1, CancellationToken.None);
        Assert.AreEqual(4, power.Snapshot.AcBoostMode);
        Assert.AreEqual(1, power.Snapshot.DcBoostMode, "Restore the captured AC/DC enum values, not a bool-derived 2/2.");
        var controller = new PerformanceController(cpuTransport: transport);
        var result = await controller.ApplyCpuTuningAsync(
            windows, false, CancellationToken.None);
        Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.Applied, result.State);
        var oem = await controller.ApplyCpuTuningAsync(
            windows with { SplWatts = 55 }, true, CancellationToken.None);
        Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.Rejected, oem.State);
        Assert.AreEqual("Capture", oem.Error?.Details["stage"]);
    }

    [TestMethod]
    public async Task Windows_power_snapshot_reads_are_fresh_and_preserve_optional_values()
    {
        var power = new SnapshotTransport();
        var transport = new WindowsCpuTuningTransport(null!, null!, power);

        var first = await transport.ReadWindowsPowerSettingsAsync(CancellationToken.None);
        Assert.AreEqual(1, power.ReadCount);
        Assert.AreEqual(3800, first.AcMaxFrequencyMhz);
        Assert.AreEqual(3000, first.DcMaxFrequencyMhz);
        Assert.AreEqual(2, first.AcBoostMode);
        Assert.AreEqual(0, first.DcBoostMode);
        Assert.AreEqual(17, first.AcMinActiveCoresPercent);
        Assert.IsNull(first.DcMinActiveCoresPercent);
        Assert.AreEqual("first", transport.ActivePowerSchemeName);

        power.Snapshot = new(Guid.NewGuid(), 3500, 2500, 0, 2) { ActiveSchemeName = "second" };
        var second = await transport.ReadWindowsPowerSettingsAsync(CancellationToken.None);
        Assert.AreEqual(2, power.ReadCount);
        Assert.AreEqual(3500, second.AcMaxFrequencyMhz);
        Assert.AreEqual(2500, second.DcMaxFrequencyMhz);
        Assert.AreEqual(power.Snapshot.ActiveSchemeId, second.ActiveSchemeId);
        Assert.AreEqual("second", transport.ActivePowerSchemeName);
        Assert.IsNull(second.AcMinActiveCoresPercent);

        power.FailRead = true;
        try
        {
            await transport.ReadWindowsPowerSettingsAsync(CancellationToken.None);
            Assert.Fail("A failed Windows read must propagate.");
        }
        catch (InvalidOperationException error) { Assert.AreEqual("powerReadFailed", error.Message); }
        Assert.AreEqual("second", transport.ActivePowerSchemeName);
    }

    private sealed class SnapshotTransport : IWindowsPowerSettingsTransport
    {
        public WindowsPowerSettingsSnapshot Snapshot { get; set; } =
            new(Guid.NewGuid(), 3800, 3000, 2, 0) { ActiveSchemeName = "first", AcMinActiveCoresPercent = 17 };
        public int ReadCount { get; private set; }
        public bool FailRead { get; set; }
        public int RestoreCount { get; private set; }
        public Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return FailRead ? throw new InvalidOperationException("powerReadFailed") : Task.FromResult(Snapshot);
        }
        public Task SetSchemeAsync(Guid schemeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetMaxFrequencyAsync(int acMegahertz, int dcMegahertz, CancellationToken cancellationToken)
        { Snapshot = Snapshot with { AcMaxFrequencyMhz = acMegahertz, DcMaxFrequencyMhz = dcMegahertz }; return Task.CompletedTask; }
        public Task SetBoostAsync(bool enabled, CancellationToken cancellationToken)
        { Snapshot = Snapshot with { AcBoostMode = enabled ? 2 : 0, DcBoostMode = enabled ? 2 : 0 }; return Task.CompletedTask; }
        public Task SetCoreParkingAsync(int acPercent, int dcPercent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreAsync(CpuTuningField field, WindowsPowerSettingsSnapshot snapshot, CancellationToken cancellationToken)
        { RestoreCount++; Snapshot = snapshot; return Task.CompletedTask; }
    }
}
