using System.Reflection;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed partial class BldingLifecycleTests
{
    [TestMethod]
    public void Paused_entry_rejects_cached_open_read_write_clear_and_shutdown_without_port_io()
    {
        var bus = new PortBus { AccessAllowed = false, ReadAllowed = false };
        var transport = bus.Create();
        foreach (Action operation in new Action[] { transport.Open, () => transport.ReadControlState(),
                     () => transport.SetTargets(40, 40), transport.Clear, transport.Dispose })
            Paused(operation);
        Assert.AreEqual(0, bus.Transfers);
        Assert.AreEqual(0, bus.Shutdowns);
        Assert.IsTrue(Field<bool>(transport, "initialized"));
        bus.AccessAllowed = bus.ReadAllowed = true;
        SetField(transport, "ready", false);
        Assert.AreEqual("bldingInitializationRecoveryRequired", Assert.ThrowsExactly<IOException>(transport.Open).Message);
        Assert.AreEqual("bldingInitializationRecoveryRequired",
            Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 40)).Message);
        SetField(transport, "signatureVerified", false);
        Assert.AreEqual("bldingEcSignatureNotVerified", Assert.ThrowsExactly<IOException>(transport.Clear).Message);
        Assert.AreEqual("bldingEcSignatureNotVerified",
            Assert.ThrowsExactly<IOException>(() => transport.ReadControlState()).Message);
        Assert.AreEqual(0, bus.Transfers);
    }

    [TestMethod]
    public void Pause_during_selection_finishes_one_register_and_prevents_the_next_transaction()
    {
        foreach (bool write in new[] { false, true })
        {
            var bus = new PortBus();
            bus.AfterTransfer = count => { if (count == 1) bus.AccessAllowed = bus.ReadAllowed = false; };
            var transport = bus.Create();
            Paused(write ? () => transport.SetTargets(40, 41) : () => transport.ReadControlState());
            Assert.AreEqual(12, bus.Transfers, "An already-started indexed-port sequence must finish unchanged.");
            Assert.AreEqual(0, bus.ValueWrites, "A pause in the new ownership capture must prevent the first value write.");
            Assert.IsFalse(transport.HasWritten);
        }

        var writing = new PortBus();
        // Four fields plus a final mapping recheck precede the first target transaction.
        writing.AfterTransfer = count => { if (count == 5 * 12 + 1) writing.AccessAllowed = writing.ReadAllowed = false; };
        var writingTransport = writing.Create();
        Paused(() => writingTransport.SetTargets(40, 41));
        Assert.AreEqual(6 * 12, writing.Transfers, "The in-flight value transaction still finishes after the capture.");
        Assert.AreEqual(1, writing.ValueWrites);
        Assert.IsTrue(writingTransport.HasWritten);

        var failing = new PortBus { FailTransfer = 5 * 12 + 1 };
        failing.AfterTransfer = count => { if (count == failing.FailTransfer) failing.AccessAllowed = failing.ReadAllowed = false; };
        var failedTransport = failing.Create();
        Paused(() => failedTransport.SetTargets(40, 41));
        Assert.IsTrue(failedTransport.HasWritten, "A failing selector cannot prove the write transaction was untouched.");
        Assert.AreEqual(5 * 12 + 1, failing.Transfers);
    }

    [TestMethod]
    public void Recovery_read_permission_never_authorizes_initialization_control_or_native_shutdown()
    {
        var bus = new PortBus { AccessAllowed = false, ReadAllowed = true };
        var transport = bus.Create();
        _ = transport.ReadControlState();
        Assert.AreEqual(5, bus.ValueReads);
        Assert.AreEqual(0, bus.ValueWrites);
        foreach (Action operation in new Action[] { transport.Open, () => transport.SetTargets(40, 40),
                     transport.Clear, transport.Dispose }) Paused(operation);
        Assert.AreEqual(60, bus.Transfers);
        Assert.AreEqual(0, bus.Shutdowns);
        bus.ReadAllowed = false;
        Paused(() => transport.ReadControlState());
        Assert.AreEqual(60, bus.Transfers);
    }

    [TestMethod]
    public void Interrupted_clear_preserves_snapshot_and_shutdown_is_not_repeated_after_completion()
    {
        foreach (int pauseAtWrite in new[] { 1, 3 })
        {
            var bus = new PortBus();
            var transport = bus.Create();
            transport.SetTargets(40, 41);
            Assert.AreEqual("bldingRecoveryRequired", Assert.ThrowsExactly<IOException>(transport.Dispose).Message);
            Assert.AreEqual(0, bus.Shutdowns);
            SetField(transport, "previousInitialization", (byte)0x81);
            bus.AfterTransfer = _ =>
            {
                if (bus.ValueWrites == 4 + pauseAtWrite) bus.AccessAllowed = bus.ReadAllowed = false;
            };
            Paused(transport.Clear);
            Assert.IsTrue(transport.HasWritten);
            Assert.AreEqual((byte)0x81, Field<byte?>(transport, "previousInitialization"));
            Assert.AreEqual(4 + pauseAtWrite, bus.ValueWrites);
            Paused(transport.Dispose);
            Assert.AreEqual(0, bus.Shutdowns);
            bus.AccessAllowed = bus.ReadAllowed = true;
            bus.AfterTransfer = null;
            transport.Clear();
            Assert.IsFalse(transport.HasWritten);
            Assert.IsNull(Field<byte?>(transport, "previousInitialization"));
            Assert.AreEqual((byte)0x81, bus.Registers[0x1060]);
            transport.Dispose();
            Assert.AreEqual(1, bus.Shutdowns);
        }

        var closing = new PortBus();
        var closingTransport = closing.Create();
        closing.OnShutdown = () => closing.AccessAllowed = closing.ReadAllowed = false;
        Paused(closingTransport.Dispose);
        Assert.IsFalse(Field<bool>(closingTransport, "initialized"));
        closing.AccessAllowed = closing.ReadAllowed = true;
        closingTransport.Dispose();
        Assert.AreEqual(1, closing.Shutdowns);
    }

    [TestMethod]
    public void Managed_ec_external_changes_block_rewrite_and_clear_without_value_writes()
    {
        foreach (ushort address in new ushort[] { 0x1060, 0x0B20, 0xC83C, 0xC83D })
        {
            var bus = new PortBus { ConsumeFanRequests = address == 0x0B20 };
            var transport = bus.Create();
            transport.SetTargets(40, 41);
            byte owned = bus.Registers[address];
            bus.Registers[address] ^= address == 0x1060 ? (byte)0x80 : address == 0x0B20 ? (byte)0x02 : (byte)0x01;
            int writes = bus.ValueWrites;
            Assert.AreEqual("bldingFanOwnershipChanged", Assert.ThrowsExactly<IOException>(() => transport.SetTargets(42, 43)).Message);
            Assert.AreEqual("bldingFanOwnershipChanged", Assert.ThrowsExactly<IOException>(transport.Clear).Message);
            Assert.AreEqual(writes, bus.ValueWrites);
            Assert.IsTrue(transport.HasWritten);
            Assert.ThrowsExactly<IOException>(transport.Dispose);
            bus.Registers[address] = owned;
            transport.Clear();
            transport.Dispose();
        }
    }

    [TestMethod]
    public void Partial_ec_write_keeps_owned_intermediates_and_rejects_unrelated_changes()
    {
        var bus = new PortBus();
        var transport = bus.Create();
        transport.SetTargets(40, 41);
        int writes = bus.ValueWrites;
        bus.AfterTransfer = _ => { if (bus.ValueWrites == writes + 1) throw new IOException("fakePartialWrite"); };
        Assert.AreEqual("fakePartialWrite", Assert.ThrowsExactly<IOException>(() => transport.SetTargets(42, 43)).Message);
        bus.AfterTransfer = null;
        Assert.AreEqual((byte)43, bus.Registers[0xC83D]);
        Assert.IsTrue(transport.HasWritten);
        bus.Registers[0xC83C] = 37;
        writes = bus.ValueWrites;
        Assert.AreEqual("bldingFanOwnershipChanged", Assert.ThrowsExactly<IOException>(transport.Clear).Message);
        Assert.AreEqual(writes, bus.ValueWrites);
        bus.Registers[0xC83C] = 40;
        transport.Clear();
        Assert.AreEqual((byte)0, bus.Registers[0xC83C]);
        Assert.AreEqual((byte)0, bus.Registers[0xC83D]);
        Assert.IsFalse(transport.HasWritten);
        transport.Dispose();
    }

    [TestMethod]
    public void Ec_clear_verifies_both_targets_and_preserves_unrelated_register_bits()
    {
        var bus = new PortBus();
        var transport = bus.Create();
        transport.SetTargets(40, 41);
        bus.Registers[0x0B20] |= 0x40;
        bus.Registers[0x1060] ^= 0x01;
        transport.SetTargets(42, 43);
        bus.IgnoreValueWrite = (address, value) => address == 0xC83D && value == 0;
        Assert.ThrowsExactly<IOException>(transport.Clear);
        Assert.IsTrue(transport.HasWritten);
        Assert.AreEqual((byte)43, bus.Registers[0xC83D]);
        bus.IgnoreValueWrite = null;
        transport.Clear();
        Assert.IsFalse(transport.HasWritten);
        Assert.AreEqual((byte)0x40, bus.Registers[0x0B20]);
        Assert.AreEqual((byte)0x80, bus.Registers[0x1060]);
        transport.Dispose();

        var released = new PortBus();
        var releasedTransport = released.Create();
        releasedTransport.SetTargets(40, 41);
        released.Registers[0xC83C] = released.Registers[0xC83D] = released.Registers[0x0B20] = 0;
        int writes = released.ValueWrites;
        releasedTransport.Clear();
        releasedTransport.Clear();
        Assert.AreEqual(writes, released.ValueWrites, "Already released EC targets need no value writes.");
        Assert.IsFalse(releasedTransport.HasWritten);
        releasedTransport.Dispose();
    }

    [TestMethod]
    public void Automatic_ceiling_reacquisition_preserves_external_targets_and_keeps_mapping_enabled()
    {
        var bus = new PortBus();
        bus.Registers[0x1060] = 0x81;
        var transport = bus.Create();
        transport.SetTargets(40, 41);
        Assert.AreEqual((byte)0x81, bus.Registers[0x1060]);
        transport.Clear();
        Assert.AreEqual((byte)0x81, bus.Registers[0x1060]);
        int writes = bus.ValueWrites;
        bus.Registers[0xC83D] = 37;
        Assert.AreEqual("bldingFanOwnershipChanged", Assert.ThrowsExactly<IOException>(() => transport.SetTargets(42, 43)).Message);
        Assert.AreEqual(writes, bus.ValueWrites);
        Assert.AreEqual((byte)37, bus.Registers[0xC83D]);
        bus.Registers[0xC83D] = 0;
        transport.SetTargets(42, 43);
        Assert.AreEqual((byte)0x81, bus.Registers[0x1060]);
        transport.Clear();
        Assert.AreEqual((byte)0x81, bus.Registers[0x1060]);
        transport.Dispose();
    }

    [TestMethod]
    public void Firmware_request_acknowledgement_preserves_target_ownership_and_detects_new_requests()
    {
        var bus = new PortBus { ConsumeFanRequests = true };
        var transport = bus.Create();
        transport.SetTargets(40, 41);
        Assert.AreEqual((byte)0, bus.Registers[0x0B20]);
        transport.SetTargets(42, 43);
        Assert.AreEqual((byte)42, bus.Registers[0xC83C]);
        Assert.AreEqual((byte)43, bus.Registers[0xC83D]);
        bus.Registers[0x0B20] = 0x02;
        int writes = bus.ValueWrites;
        Assert.AreEqual("bldingFanOwnershipChanged", Assert.ThrowsExactly<IOException>(transport.Clear).Message);
        Assert.AreEqual(writes, bus.ValueWrites);
        bus.Registers[0x0B20] = 0;
        transport.Clear();
        Assert.IsFalse(transport.HasWritten);
        transport.Dispose();
    }

    private static void Paused(Action action) =>
        Assert.AreEqual("hardwareAccessPaused", Assert.ThrowsExactly<IOException>(action).Message);

    private static T? Field<T>(object target, string name) =>
        (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    [TestMethod]
    public void Ec_checkpoints_precede_value_writes_and_keep_the_original_register_baseline()
    {
        var bus = new PortBus();
        bus.Registers[0x1060] = 0x81;
        bus.Registers[0xC83C] = 20;
        bus.Registers[0xC83D] = 21;
        var baseline = new Dictionary<ushort, byte>(bus.Registers);
        var records = new List<FanEcRecoveryCheckpoint>();
        int writeCheckpoints = 0;
        var transport = bus.Create(record =>
        {
            records.Add(record);
            if (record.Pending.Count != 0)
            {
                Assert.AreEqual(bus.ValueWrites, writeCheckpoints++, "Durable checkpoint must precede each EC value transaction.");
                Assert.IsFalse(record.Released);
            }
        });
        transport.SetTargets(40, 41);
        Assert.IsTrue(records.Count > 0);
        Assert.AreEqual(bus.ValueWrites, writeCheckpoints);
        CollectionAssert.AreEquivalent(baseline.ToArray(), records[0].Baseline.ToArray());
        transport.Clear();
        Assert.IsTrue(records[^1].Released);
        Assert.AreEqual((byte)0x81, records[^1].Baseline[0x1060]);
        Assert.AreEqual((byte)0x81, records[^1].Expected[0x1060]);
        Assert.AreEqual(0, records[^1].Pending.Count);
        Assert.AreEqual((byte)0, records[^1].Expected[0xC83C]);
        Assert.AreEqual((byte)0, records[^1].Expected[0xC83D]);
        Assert.AreEqual((byte)20, records[0].Expected[0xC83C], "Earlier snapshots must remain detached.");
        transport.Dispose();
        Assert.IsTrue(records[^1].Released, "Disposal must retain the verified release instead of relabelling it as untouched.");
    }

    [TestMethod]
    public void Failed_ec_checkpoint_prevents_the_next_value_write_and_keeps_recovery_state()
    {
        var bus = new PortBus();
        bool fail = true;
        var transport = bus.Create(record => { if (fail && record.Pending.Count > 0) throw new IOException("checkpointStorageUnavailable"); });
        Assert.AreEqual("checkpointStorageUnavailable", Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 41)).Message);
        Assert.AreEqual(0, bus.ValueWrites);
        Assert.IsFalse(transport.HasWritten);
        fail = false;
        transport.SetTargets(40, 41);
        fail = true;
        int writes = bus.ValueWrites;
        Assert.ThrowsExactly<IOException>(transport.Clear);
        Assert.AreEqual(writes, bus.ValueWrites);
        Assert.IsTrue(transport.HasWritten);
        fail = false;
        transport.Clear();
        Assert.IsFalse(transport.HasWritten);
    }

    [TestMethod]
    public void Failed_release_checkpoint_retains_original_initialization_until_retry()
    {
        var bus = new PortBus();
        bus.Registers[0x1060] = 0x81;
        bool fail = true;
        var transport = bus.Create(record => { if (fail && record.Released) throw new IOException("releaseCheckpointUnavailable"); });
        transport.SetTargets(40, 41);
        Assert.ThrowsExactly<IOException>(transport.Clear);
        Assert.IsTrue(transport.HasWritten);
        Assert.AreEqual((byte)0x81, Field<byte?>(transport, "previousInitialization"));
        fail = false;
        transport.Clear();
        Assert.IsFalse(transport.HasWritten);
    }

    [TestMethod]
    public void Checkpoint_ack_failure_before_value_write_preserves_external_targets_and_records_unchanged_on_disposal()
    {
        var bus = new PortBus();
        bus.Registers[0x1060] = 0x81;
        bus.Registers[0xC83C] = 32;
        bus.Registers[0xC83D] = 35;
        var original = new Dictionary<ushort, byte>(bus.Registers);
        var records = new List<FanEcRecoveryCheckpoint>();
        var transport = bus.Create(record =>
        {
            records.Add(record);
            if (record.Pending.Count > 0) throw new IOException("checkpointAckLostAfterPersist");
        });
        Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 41));
        Assert.AreEqual(0, bus.ValueWrites);
        Assert.IsTrue(records[^1].Pending.Count > 0);
        transport.Dispose();
        Assert.AreEqual(0, bus.ValueWrites);
        CollectionAssert.AreEquivalent(original.ToArray(), bus.Registers.ToArray());
        Assert.IsTrue(records[^1].Unchanged);
        Assert.IsFalse(records[^1].Released, "External manual targets were preserved; this is not an EC-auto release.");
        Assert.AreEqual(0, records[^1].Pending.Count);
        Assert.AreEqual(1, bus.Shutdowns);
    }

    [TestMethod]
    public void Unchanged_checkpoint_storage_failure_or_external_change_prevents_driver_shutdown()
    {
        foreach (bool external in new[] { false, true })
        {
            var bus = new PortBus();
            var transport = bus.Create(record =>
            {
                if (record.Pending.Count > 0 || record.Unchanged && !external) throw new IOException("storageUnavailable");
            });
            Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 41));
            if (external) bus.Registers[0xC83D] = 37;
            Assert.ThrowsExactly<IOException>(transport.Dispose);
            Assert.AreEqual(0, bus.ValueWrites);
            Assert.AreEqual(0, bus.Shutdowns);
            Assert.IsTrue(Field<bool>(transport, "initialized"));
        }
    }

    internal sealed class PortBus
    {
        public bool AccessAllowed = true, ReadAllowed = true;
        public int Transfers, ValueReads, ValueWrites, Shutdowns;
        public int? FailTransfer;
        public Action<int>? AfterTransfer;
        public Action? OnShutdown;
        public Func<ushort, byte, bool>? IgnoreValueWrite;
        public bool ConsumeFanRequests;
        public bool MappingGatesTargets, ThrowOnUnmappedTargetRead;
        public int UnmappedTargetReads;
        public readonly Dictionary<ushort, byte> Registers = new()
        {
            [0x1060] = 0x81, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0
        };
        private byte index, selector, high, low;
        private ushort Address => (ushort)((high << 8) | low);

        public BldingFanEcTransport Create(Action<FanEcRecoveryCheckpoint>? checkpoint = null, Func<bool>? allowed = null, Func<bool>? readAllowed = null) =>
            new(Read, Write, () => { Shutdowns++; OnShutdown?.Invoke(); },
                () => AccessAllowed && (allowed?.Invoke() ?? true), () => ReadAllowed && (readAllowed?.Invoke() ?? true), checkpoint);

        private byte Read(ushort port)
        {
            Assert.AreEqual((ushort)0x4F, port);
            ValueReads++;
            byte value;
            if (MappingGatesTargets && (Registers[0x1060] & 0x80) == 0 && Address is 0xC83C or 0xC83D)
            {
                UnmappedTargetReads++;
                if (ThrowOnUnmappedTargetRead) throw new IOException("fakeUnmappedTarget");
                value = 0;
            }
            else value = Registers[Address];
            Transfer();
            return value;
        }

        private void Write(ushort port, byte value)
        {
            if (port == 0x4E) index = value;
            else if (index == 0x2E) selector = value;
            else if (selector == 0x11) high = value;
            else if (selector == 0x10) low = value;
            else
            {
                if (IgnoreValueWrite?.Invoke(Address, value) != true)
                    Registers[Address] = ConsumeFanRequests && Address == 0x0B20 ? (byte)(value & ~0x0A) : value;
                ValueWrites++;
            }
            Transfer();
        }

        private void Transfer()
        {
            Transfers++;
            AfterTransfer?.Invoke(Transfers);
            if (Transfers == FailTransfer) throw new IOException("fakePortFailure");
        }
    }
}
