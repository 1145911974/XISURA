using System.Reflection;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class BldingLifecycleTests
{
    private static void FinishMapping(BldingFanEcTransport transport)
    {
        var method = typeof(BldingFanEcTransport).GetMethod("FinishLease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Final mapping restoration must be separate from ordinary target release.");
        try { method.Invoke(transport, null); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    [TestMethod]
    public void Mapping_lease_persists_before_mapping_and_target_writes_and_rejects_capture_loss()
    {
        foreach (string fault in new[] { "openingWal", "targetWal", "capture" })
        {
            var bus = new PortBus { MappingGatesTargets = true };
            bus.Registers[0x1060] = 1;
            bus.Registers[0xC83C] = 23; bus.Registers[0xC83D] = 29;
            var records = new List<FanEcRecoveryCheckpoint>();
            var transport = bus.Create(record =>
            {
                Assert.IsTrue(ControlExecutionJournal.ValidFanCheckpoint(record));
                if (fault == "openingWal" && record.MappingLease?.Pending.Length > 0 ||
                    fault == "targetWal" && record.MappingLease?.TargetWritesStarted == true)
                    throw new IOException("fakeWalFailure");
                records.Add(record);
            });
            if (fault == "capture") bus.AfterTransfer = _ =>
            { if (bus.ValueWrites == 1 && bus.ValueReads == 4) bus.Registers[0x1060] = 1; };
            Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 41));
            Assert.IsTrue(records.Count > 0, "An unmapped lease needs a durable original before any value write.");
            Assert.AreEqual(fault == "openingWal" ? 0 : 1, bus.ValueWrites);
            Assert.IsFalse(records.Any(record => record.Released || record.Unchanged));
            if (fault == "capture") Assert.IsFalse(records.Any(record => record.MappingLease!.TargetsCaptured));
            if (fault == "targetWal")
            {
                FinishMapping(transport);
                Assert.AreEqual((byte)23, bus.Registers[0xC83C]);
                Assert.AreEqual((byte)29, bus.Registers[0xC83D]);
                Assert.AreEqual(2, bus.ValueWrites, "A failed first target WAL must only undo the mapping acquisition.");
                Assert.IsTrue(records[^1].Unchanged);
            }
        }
    }

    [TestMethod]
    public void Mapping_lease_ordinary_release_reuses_ownership_and_finally_restores_only_mapping_bit()
    {
        var bus = new PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
        bus.Registers[0x1060] = 1;
        bus.Registers[0xC83C] = 37; bus.Registers[0xC83D] = 38;
        var records = new List<FanEcRecoveryCheckpoint>();
        var transport = bus.Create(record => { Assert.IsTrue(ControlExecutionJournal.ValidFanCheckpoint(record)); records.Add(record); });
        transport.Open();
        Assert.AreEqual((byte)37, records[^1].Baseline[0xC83C]);
        int writes = bus.ValueWrites;
        transport.Clear();
        Assert.AreEqual(writes, bus.ValueWrites, "A mapping-only acquisition must preserve external targets.");
        transport.SetTargets(40, 41);
        transport.Clear();
        Assert.AreEqual((byte)0x81, bus.Registers[0x1060]);
        Assert.IsFalse(ControlExecutionJournal.FanCheckpointComplete(records[^1]));
        transport.SetTargets(42, 43);
        bus.Registers[0x1060] = 0xD5;
        FinishMapping(transport);
        Assert.AreEqual((byte)0x55, bus.Registers[0x1060]);
        Assert.AreEqual((byte)0, bus.Registers[0xC83C]);
        Assert.AreEqual((byte)0, bus.Registers[0xC83D]);
        Assert.IsTrue(records[^1].Released);
        Assert.IsTrue(records[^1].MappingLease!.Restored);
        Assert.AreEqual((byte)1, records[^1].MappingLease!.Original);
        Assert.AreEqual(0, bus.UnmappedTargetReads);
        transport.Dispose();
        Assert.AreEqual(1, bus.Shutdowns);
    }

    [TestMethod]
    public void Mapping_lease_new_process_reconciles_opening_lost_ack_without_touching_external_targets()
    {
        foreach (bool applied in new[] { false, true })
        {
            var bus = new PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
            bus.Registers[0x1060] = 1; bus.Registers[0xC83C] = 37; bus.Registers[0xC83D] = 38;
            bus.IgnoreValueWrite = (address, _) => address == 0x1060 && !applied;
            bus.AfterTransfer = _ => { if (bus.ValueWrites == 1) throw new IOException("fakeLostAck"); };
            var records = new List<FanEcRecoveryCheckpoint>();
            var transport = bus.Create(records.Add);
            Assert.ThrowsExactly<IOException>(transport.Open);
            Assert.IsTrue(records.Count > 0);
            Assert.IsFalse(records[^1].MappingLease!.TargetsCaptured);
            bus.AfterTransfer = null; bus.IgnoreValueWrite = null;
            var recovery = bus.Create(records.Add);
            recovery.OpenForRecovery(records[^1]);
            FinishMapping(recovery);
            Assert.AreEqual((byte)1, bus.Registers[0x1060]);
            Assert.AreEqual((byte)37, bus.Registers[0xC83C]);
            Assert.AreEqual((byte)38, bus.Registers[0xC83D]);
            Assert.AreEqual(applied ? 2 : 1, bus.ValueWrites);
            Assert.AreEqual(0, bus.UnmappedTargetReads);
            Assert.IsTrue(records[^1].Unchanged);
            recovery.Dispose();
        }
    }

    [TestMethod]
    public void Mapping_lease_partial_target_recovery_rejects_external_owner_and_releases_before_unmapping()
    {
        var bus = new PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
        bus.Registers[0x1060] = 1;
        var records = new List<FanEcRecoveryCheckpoint>();
        var transport = bus.Create(records.Add);
        bus.AfterTransfer = _ => { if (bus.ValueWrites == 2) throw new IOException("fakeTargetLostAck"); };
        Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 41));
        Assert.IsTrue(records.Count > 0);
        Assert.IsTrue(records[^1].MappingLease!.TargetWritesStarted);
        Assert.IsTrue(records[^1].Pending.ContainsKey(0xC83D));
        var pending = records[^1];
        bus.AfterTransfer = null;
        bus.Registers[0xC83D] = 60;
        int writes = bus.ValueWrites;
        Assert.ThrowsExactly<IOException>(() => bus.Create(records.Add).OpenForRecovery(pending));
        Assert.AreEqual(writes, bus.ValueWrites);
        bus.Registers[0xC83D] = 41;
        var recovery = bus.Create(records.Add);
        recovery.OpenForRecovery(pending);
        FinishMapping(recovery);
        Assert.AreEqual((byte)1, bus.Registers[0x1060]);
        Assert.AreEqual((byte)0, bus.Registers[0xC83C]);
        Assert.AreEqual((byte)0, bus.Registers[0xC83D]);
        Assert.IsTrue(records[^1].Released);
        Assert.AreEqual(0, bus.UnmappedTargetReads);
        recovery.Dispose();
    }

    [TestMethod]
    public void Mapping_lease_restore_lost_ack_reconciles_unmapped_state_without_reading_targets()
    {
        var bus = new PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
        bus.Registers[0x1060] = 1;
        var records = new List<FanEcRecoveryCheckpoint>();
        var transport = bus.Create(records.Add);
        transport.SetTargets(40, 41);
        bus.AfterTransfer = _ => { if ((bus.Registers[0x1060] & 0x80) == 0) throw new IOException("fakeRestoreLostAck"); };
        Assert.ThrowsExactly<IOException>(() => FinishMapping(transport));
        Assert.IsFalse(records[^1].Released);
        Assert.IsTrue(records[^1].MappingLease!.Pending.Length > 0);
        bus.AfterTransfer = null;
        int writes = bus.ValueWrites;
        var recovery = bus.Create(records.Add);
        recovery.OpenForRecovery(records[^1]);
        FinishMapping(recovery);
        Assert.AreEqual(writes, bus.ValueWrites);
        Assert.IsTrue(records[^1].Released);
        Assert.AreEqual(0, bus.UnmappedTargetReads);
        recovery.Dispose();
    }
}
