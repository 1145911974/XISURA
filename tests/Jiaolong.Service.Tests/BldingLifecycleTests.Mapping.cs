using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class BldingLifecycleTests
{
    [TestMethod]
    public void Control_state_rejects_unmapped_targets_without_reads_writes_or_checkpoints()
    {
        foreach (bool invalidRead in new[] { false, true })
        {
            var bus = new PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = invalidRead };
            bus.Registers[0x1060] = 1;
            bus.Registers[0xC83C] = 37;
            bus.Registers[0xC83D] = 38;
            var records = new List<FanEcRecoveryCheckpoint>();
            var transport = bus.Create(records.Add);
            Assert.AreEqual("bldingFanBaselineMappingUnavailable",
                Assert.ThrowsExactly<IOException>(() => transport.ReadControlState()).Message);
            Assert.AreEqual(0, bus.UnmappedTargetReads);
            Assert.AreEqual(1, bus.ValueReads);
            Assert.AreEqual(0, bus.ValueWrites);
            Assert.AreEqual(0, records.Count);
            Assert.IsFalse(transport.HasWritten);
            Assert.AreEqual((byte)37, bus.Registers[0xC83C]);
        }
    }

    [TestMethod]
    public void Control_state_rejects_mapping_loss_during_snapshot_without_changing_ownership()
    {
        var bus = new PortBus { MappingGatesTargets = true };
        var records = new List<FanEcRecoveryCheckpoint>();
        var transport = bus.Create(records.Add);
        transport.SetTargets(40, 41);
        int writes = bus.ValueWrites, checkpoints = records.Count, start = bus.Transfers;
        bus.AfterTransfer = count => { if (count == start + 3 * 12) bus.Registers[0x1060] &= 0x7F; };
        Assert.AreEqual("bldingFanBaselineMappingUnavailable",
            Assert.ThrowsExactly<IOException>(() => transport.ReadControlState()).Message);
        Assert.AreEqual(writes, bus.ValueWrites);
        Assert.AreEqual(checkpoints, records.Count);
        Assert.IsTrue(transport.HasWritten);
        Assert.IsFalse(records[^1].Released || records[^1].Unchanged);
    }

    [TestMethod]
    public void Unmapped_new_lease_rejects_before_target_reads_checkpoints_or_value_writes()
    {
        foreach (bool invalidRead in new[] { false, true })
        foreach (bool open in new[] { false, true })
        {
            var bus = new PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = invalidRead };
            bus.Registers[0x1060] = 1;
            bus.Registers[0xC83C] = 37;
            bus.Registers[0xC83D] = 38;
            var records = new List<FanEcRecoveryCheckpoint>();
            var transport = bus.Create();
            Assert.AreEqual("bldingFanBaselineMappingUnavailable",
                Assert.ThrowsExactly<IOException>(open ? transport.Open : () => transport.SetTargets(40, 41)).Message);
            Assert.AreEqual(0, bus.UnmappedTargetReads);
            Assert.AreEqual(0, records.Count);
            Assert.AreEqual(0, bus.ValueWrites);
            Assert.IsFalse(transport.HasWritten);
            Assert.AreEqual((byte)1, bus.Registers[0x1060]);
            Assert.AreEqual((byte)37, bus.Registers[0xC83C]);
        }
    }

    [TestMethod]
    public void Legacy_unmapped_baseline_pending_released_and_unchanged_are_rejected_without_rewriting_evidence()
    {
        foreach (int state in new[] { 0, 1, 2 })
        {
            var bus = new PortBus { MappingGatesTargets = true };
            bus.Registers[0x1060] = 1;
            var baseline = new Dictionary<ushort, byte>(bus.Registers);
            var record = new FanEcRecoveryCheckpoint(baseline, new(baseline), new(), state == 1)
                { Unchanged = state == 2 };
            if (state == 0)
            {
                record.Expected[0x1060] = 0x81;
                record.Pending[0x1060] = [1, 0x81];
            }
            var records = new List<FanEcRecoveryCheckpoint>();
            var transport = bus.Create(records.Add);
            Assert.AreEqual("bldingFanBaselineMappingUnavailable",
                Assert.ThrowsExactly<IOException>(() => transport.OpenForRecovery(record)).Message);
            Assert.AreEqual(0, bus.Transfers);
            Assert.AreEqual(0, bus.ValueWrites);
            Assert.AreEqual(0, records.Count, "Keep the caller's original recovery evidence pending.");
            Assert.AreEqual(state == 0 ? 1 : 0, record.Pending.Count);
            Assert.AreEqual((byte)1, record.Baseline[0x1060]);
        }
    }

    [TestMethod]
    public void Mapping_loss_during_owned_lease_cannot_complete_clear_using_unmapped_zero_targets()
    {
        var bus = new PortBus { MappingGatesTargets = true };
        var records = new List<FanEcRecoveryCheckpoint>();
        var transport = bus.Create(records.Add);
        transport.SetTargets(40, 41);
        int writes = bus.ValueWrites, checkpoints = records.Count;
        bus.Registers[0x1060] &= 0x7F;
        Assert.AreEqual("bldingFanOwnershipChanged", Assert.ThrowsExactly<IOException>(transport.Clear).Message);
        Assert.AreEqual(0, bus.UnmappedTargetReads);
        Assert.AreEqual(writes, bus.ValueWrites);
        Assert.AreEqual(checkpoints, records.Count);
        Assert.IsTrue(transport.HasWritten);
        Assert.IsFalse(records[^1].Released || records[^1].Unchanged);
        Assert.AreEqual((byte)40, bus.Registers[0xC83C]);
        Assert.AreEqual((byte)41, bus.Registers[0xC83D]);
    }
}
