using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class WindowsCpuPowerSnapshotTests
{
    [TestMethod]
    public async Task Single_axis_frequency_preserves_other_side_and_legacy_frequency_still_sets_both()
    {
        foreach (string side in new[] { "ac", "dc", "legacy" })
        {
            var power = new SnapshotTransport();
            var original = power.Snapshot;
            var controller = new PerformanceController(cpuTransport: new WindowsCpuTuningTransport(null!, null!, power));
            var plan = new Jiaolong.Contracts.Commands.CpuTuningPlan(null, null, null, side == "legacy" ? 3600 : null, null, null, null, null)
                { AcMaxFrequencyMhz = side == "ac" ? 3600 : null, DcMaxFrequencyMhz = side == "dc" ? 2200 : null };
            var result = await controller.ApplyCpuTuningAsync(plan, true, CancellationToken.None);
            Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.Applied, result.State);
            int ac = side == "dc" ? original.AcMaxFrequencyMhz : 3600;
            int dc = side == "ac" ? original.DcMaxFrequencyMhz : side == "dc" ? 2200 : 3600;
            Assert.AreEqual(original with { AcMaxFrequencyMhz = ac, DcMaxFrequencyMhz = dc }, power.Snapshot);
            Assert.AreEqual(new WindowsPowerFrequencyValue(ac, dc), result.FinalValues[CpuTuningField.MaxFrequencyMhz]);
        }
    }

    [TestMethod]
    public async Task Partial_frequency_batch_uses_fresh_selected_scheme_values_and_preserves_prior_axis_writes()
    {
        var power = new SnapshotTransport();
        var first = power.Snapshot;
        var second = first with { ActiveSchemeId = Guid.NewGuid(), AcMaxFrequencyMhz = 4900, DcMaxFrequencyMhz = 2100 };
        power.Schemes[first.ActiveSchemeId] = first;
        power.Schemes[second.ActiveSchemeId] = second;
        var controller = new PerformanceController(cpuTransport: new WindowsCpuTuningTransport(null!, null!, power));
        var empty = new Jiaolong.Contracts.Commands.CpuTuningPlan(null, null, null, null, null, null, null, null);
        var result = await controller.ApplyCpuTuningBatchAsync([
            empty with { AcMaxFrequencyMhz = 3500 },
            empty with { WindowsPowerSchemeId = second.ActiveSchemeId, DcMaxFrequencyMhz = 1800 },
            empty with { AcMaxFrequencyMhz = 4200 }
        ], true, CancellationToken.None);
        Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("cause"));
        Assert.AreEqual(first with { AcMaxFrequencyMhz = 3500 }, power.Schemes[first.ActiveSchemeId]);
        Assert.AreEqual(second with { AcMaxFrequencyMhz = 4200, DcMaxFrequencyMhz = 1800 }, power.Snapshot);
        Assert.AreEqual(new WindowsPowerFrequencyValue(4200, 1800), result.FinalValues[CpuTuningField.MaxFrequencyMhz]);
        Assert.AreEqual(3, power.FrequencyWrites.Count);
        CollectionAssert.AreEqual(new[] { new WindowsPowerFrequencyValue(3500, 3000), new(4900, 1800), new(4200, 1800) }, power.FrequencyWrites);
    }

    [TestMethod]
    public async Task Partial_frequency_readback_failure_restores_original_pair_and_unreadable_pair_prevents_write()
    {
        foreach (bool unreadable in new[] { false, true })
        {
            var power = new SnapshotTransport { CorruptNextFrequencyWrite = !unreadable };
            var original = power.Snapshot;
            var transport = new WindowsCpuTuningTransport(null!, null!, power);
            var controller = new PerformanceController(cpuTransport: transport);
            if (unreadable) power.BeforeFrequencyRead = () => { if (power.ReadCount >= 2) power.FailRead = true; };
            var plan = new Jiaolong.Contracts.Commands.CpuTuningPlan(null, null, null, null, null, null, null, null) { AcMaxFrequencyMhz = 3500 };
            var result = await controller.ApplyCpuTuningAsync(plan, true, CancellationToken.None);
            Assert.AreNotEqual(Jiaolong.Contracts.Commands.CommandState.Applied, result.State);
            if (unreadable)
            {
                Assert.AreEqual(2, power.ReadCount, "Capture succeeds; fresh dispatch read fails.");
                Assert.AreEqual(0, power.FrequencyWrites.Count);
            }
            else
            {
                Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.RolledBack, result.State);
                Assert.AreEqual(original, power.Snapshot);
            }
        }
    }

    [TestMethod]
    public async Task Exact_boost_policy_applies_and_rolls_back_failed_readback_without_oem_access()
    {
        foreach (bool corrupt in new[] { false, true })
        {
            var power = new SnapshotTransport { CorruptNextBoostRestore = corrupt };
            power.Snapshot = power.Snapshot with { AcBoostMode = 4, DcBoostMode = 1 };
            var before = power.Snapshot;
            var controller = new PerformanceController(cpuTransport: new WindowsCpuTuningTransport(null!, null!, power));
            var plan = new Jiaolong.Contracts.Commands.CpuTuningPlan(null, null, null, null, null, null, null, null)
            { BoostPolicy = new(0, 0) };
            var result = await controller.ApplyCpuTuningAsync(plan, true, CancellationToken.None);
            Assert.AreEqual(corrupt ? Jiaolong.Contracts.Commands.CommandState.RolledBack : Jiaolong.Contracts.Commands.CommandState.Applied,
                result.State, result.Error?.Details.GetValueOrDefault("cause"));
            Assert.AreEqual(corrupt ? before : before with { AcBoostMode = 0, DcBoostMode = 0 }, power.Snapshot);
        }
    }

    [TestMethod]
    public async Task Invalid_or_conflicting_boost_policy_is_rejected_before_power_reads_and_boolean_api_still_works()
    {
        var power = new SnapshotTransport();
        var controller = new PerformanceController(cpuTransport: new WindowsCpuTuningTransport(null!, null!, power));
        var plan = new Jiaolong.Contracts.Commands.CpuTuningPlan(null, null, null, null, null, null, null, null)
        { AcMaxFrequencyMhz = 3800 };
        foreach (var invalid in new[] { plan with { BoostPolicy = new(7, 1) },
            plan with { BoostPolicy = new(4, 1), BoostEnabled = false } })
        {
            var rejected = await controller.ApplyCpuTuningAsync(invalid, true, CancellationToken.None);
            Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.Rejected, rejected.State);
            Assert.AreEqual(0, power.ReadCount);
        }
        foreach (var invalidBatch in new[]
        {
            new[] { plan with { BoostPolicy = new(4, 1) }, plan with { BoostEnabled = true } },
            new[] { plan with { BoostPolicy = new(4, 1) }, plan with { BoostPolicy = new(1, 4) } }
        })
        {
            var rejected = await controller.ApplyCpuTuningBatchAsync(invalidBatch, true, CancellationToken.None);
            Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.Rejected, rejected.State);
            Assert.AreEqual(0, power.ReadCount);
        }
        var applied = await controller.ApplyCpuTuningAsync(plan with { BoostEnabled = true }, true, CancellationToken.None);
        Assert.AreEqual(Jiaolong.Contracts.Commands.CommandState.Applied, applied.State);
        Assert.AreEqual(2, power.Snapshot.AcBoostMode);
        Assert.AreEqual(2, power.Snapshot.DcBoostMode);
    }

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
        Assert.AreEqual(0, power.RestoreCount, "Native capture alone must not authorize Windows writes.");
        var original = power.Snapshot;
        await transport.CaptureTransactionAsync([windows], true, CancellationToken.None);
        power.Snapshot = original with { AcMaxFrequencyMhz = 4123 };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => transport.RestoreTransactionAsync(_ => Task.CompletedTask, CancellationToken.None));
        Assert.AreEqual(4123, power.Snapshot.AcMaxFrequencyMhz);
        Assert.AreEqual(0, power.RestoreCount, "A transport without scoped recovery must also preserve unowned drift.");
        power.Snapshot = original;
        await transport.RestoreTransactionAsync(CancellationToken.None);
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
        public bool CorruptNextBoostRestore { get; set; }
        public bool CorruptNextFrequencyWrite { get; set; }
        public Action? BeforeFrequencyRead { get; set; }
        public Dictionary<Guid, WindowsPowerSettingsSnapshot> Schemes { get; } = [];
        public List<WindowsPowerFrequencyValue> FrequencyWrites { get; } = [];
        public int RestoreCount { get; private set; }
        public Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            BeforeFrequencyRead?.Invoke();
            return FailRead ? throw new InvalidOperationException("powerReadFailed") : Task.FromResult(Snapshot);
        }
        public Task SetSchemeAsync(Guid schemeId, CancellationToken cancellationToken)
        { Schemes[Snapshot.ActiveSchemeId] = Snapshot; Snapshot = Schemes[schemeId]; return Task.CompletedTask; }
        public Task SetMaxFrequencyAsync(int acMegahertz, int dcMegahertz, CancellationToken cancellationToken)
        {
            FrequencyWrites.Add(new(acMegahertz, dcMegahertz));
            Snapshot = Snapshot with { AcMaxFrequencyMhz = acMegahertz, DcMaxFrequencyMhz = dcMegahertz };
            if (CorruptNextFrequencyWrite) { CorruptNextFrequencyWrite = false; Snapshot = Snapshot with { DcMaxFrequencyMhz = dcMegahertz + 100 }; }
            Schemes[Snapshot.ActiveSchemeId] = Snapshot;
            return Task.CompletedTask;
        }
        public Task SetBoostAsync(bool enabled, CancellationToken cancellationToken)
        { Snapshot = Snapshot with { AcBoostMode = enabled ? 2 : 0, DcBoostMode = enabled ? 2 : 0 }; return Task.CompletedTask; }
        public Task SetCoreParkingAsync(int acPercent, int dcPercent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreAsync(CpuTuningField field, WindowsPowerSettingsSnapshot snapshot, CancellationToken cancellationToken)
        {
            RestoreCount++;
            Snapshot = snapshot;
            if (field == CpuTuningField.BoostEnabled && CorruptNextBoostRestore)
            { CorruptNextBoostRestore = false; Snapshot = snapshot with { AcBoostMode = 3 }; }
            return Task.CompletedTask;
        }
    }
}
