using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass, DoNotParallelize]
public sealed class BldingPawnIoIntegrationTests
{
    [TestMethod]
    public void Production_mapped_ports_reject_target_writes_without_a_durable_checkpoint()
    {
        var ports = new Ports();
        var transport = ports.Create();
        transport.Open();
        Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 41));
        Assert.AreEqual(0, ports.ValueWrites);
        Assert.AreEqual(0L, transport.ValueWriteAttempts);
        Assert.IsFalse(transport.HasWritten);
        transport.Dispose();
        Assert.AreEqual(0, ports.ValueWrites);
        Assert.AreEqual(1, ports.Disposals);
    }

    [TestMethod]
    public void Production_ports_require_chip_mapping_and_durable_checkpoint_before_any_value_write()
    {
        foreach (bool badChip in new[] { true, false })
        {
            var ports = new Ports();
            ports.Registers[badChip ? (ushort)0x2000 : (ushort)0x1060] = 0;
            int checkpoints = 0;
            // Unmapped production ports may acquire a lease only with a durable checkpoint writer.
            var transport = ports.Create(badChip ? _ => checkpoints++ : null);
            Assert.AreEqual(0, ports.Transfers + ports.Opens + ports.Acquisitions);
            Assert.ThrowsExactly<IOException>(transport.Open);
            Assert.AreEqual(0, ports.ValueWrites);
            Assert.AreEqual(0, checkpoints);
            Assert.AreEqual(1, ports.Disposals);
            if (!badChip) Assert.IsFalse(ports.ReadAddresses.Any(a => a is 0xC83C or 0xC83D));
        }

        var good = new Ports();
        var records = new List<FanEcRecoveryCheckpoint>();
        bool rejectPending = true;
        var opened = good.Create(record =>
        {
            if (rejectPending && record.Pending.Count > 0) throw new IOException("fakeCheckpointUnavailable");
            records.Add(record);
        });
        Assert.AreEqual(0, good.Transfers + good.Opens + good.Acquisitions);
        opened.Open();
        opened.Open();
        Assert.AreEqual(1, good.Opens);
        Assert.AreEqual((byte)0x81, records[0].Baseline[0x1060]);
        Assert.AreEqual((byte)0, records[0].Baseline[0xC83C]);
        Assert.AreEqual(0, good.ValueWrites);
        Assert.ThrowsExactly<IOException>(() => opened.SetTargets(40, 41));
        Assert.AreEqual(0L, opened.ValueWriteAttempts);
        Assert.IsFalse(opened.HasWritten);
        Assert.AreEqual(0, good.ValueWrites);
        rejectPending = false;
        opened.Dispose();
        Assert.IsTrue(records.Last().Unchanged);
        Assert.AreEqual(0, good.ValueWrites);
        Assert.AreEqual(1, good.Disposals);
    }

    [TestMethod]
    public void Bus_then_ec_gate_covers_open_and_register_boundaries_while_wait_failures_do_no_io()
    {
        var rejected = new Ports { FailAcquire = true };
        Assert.ThrowsExactly<IOException>(rejected.Create().Open);
        Assert.AreEqual(0, rejected.Transfers + rejected.Opens);

        var afterWait = new Ports();
        afterWait.AfterAcquire = () => afterWait.Allowed = false;
        Assert.ThrowsExactly<IOException>(afterWait.Create().Open);
        Assert.AreEqual(0, afterWait.Transfers + afterWait.Opens);
        Assert.IsFalse(afterWait.BusHeld);

        var duringRegister = new Ports();
        duringRegister.AfterTransfer = count => { if (count == 1) duringRegister.Allowed = false; };
        Assert.ThrowsExactly<IOException>(duringRegister.Create().Open);
        Assert.AreEqual(12, duringRegister.Transfers, "Cancellation must finish the current indexed read.");
        CollectionAssert.AreEqual(new ushort[] { 0x2000 }, duringRegister.ReadAddresses.ToArray());
        Assert.AreEqual(0, duringRegister.ValueWrites);
        Assert.AreEqual(1, duringRegister.Disposals);
        Assert.IsFalse(duringRegister.BusHeld);

        var normal = new Ports();
        var transport = normal.Create(_ => { });
        transport.Open();
        transport.ReadControlState();
        transport.SetTargets(40, 41);
        transport.Clear();
        transport.Dispose();
        Assert.AreEqual(1, normal.Opens);
        Assert.AreEqual(1, normal.Disposals);
        Assert.AreEqual(7, normal.ValueWrites);
        Assert.IsFalse(normal.BusHeld);
    }

    [TestMethod]
    public void Archived_pending_recovery_keeps_backend_until_release_checkpoint_and_closes_only_its_ports()
    {
        var baseline = new Dictionary<ushort, byte>
        {
            [0x1060] = 0x81, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0
        };
        var checkpoint = new FanEcRecoveryCheckpoint(baseline,
            new(baseline) { [0x0B20] = 0x0A, [0xC83C] = 42, [0xC83D] = 41 },
            new() { [0xC83C] = [40, 42] }, false);
        var ports = new Ports();
        ports.Registers[0x0B20] = 0x4A;
        ports.Registers[0xC83C] = 40;
        ports.Registers[0xC83D] = 41;
        bool rejectReleased = true;
        FanEcRecoveryCheckpoint? saved = null;
        var transport = ports.Create(record =>
        {
            if (rejectReleased && record.Released) throw new IOException("fakeReleaseCheckpointUnavailable");
            saved = record;
        });
        transport.OpenForRecovery(checkpoint);
        Assert.AreEqual(0, ports.ValueWrites);
        Assert.IsTrue(transport.HasWritten);
        Assert.ThrowsExactly<IOException>(transport.Dispose);
        Assert.AreEqual(0, ports.Disposals);
        Assert.ThrowsExactly<IOException>(transport.Clear);
        Assert.IsTrue(transport.HasWritten);
        Assert.ThrowsExactly<IOException>(transport.Dispose);
        Assert.AreEqual(0, ports.Disposals);
        rejectReleased = false;
        transport.Clear();
        Assert.IsTrue(saved!.Released);
        Assert.AreEqual((byte)0x40, ports.Registers[0x0B20]);
        Assert.AreEqual((byte)0x81, ports.Registers[0x1060]);
        Assert.AreEqual(3, ports.ValueWrites);
        var independent = new Ports();
        var other = independent.Create();
        other.Open();
        transport.Dispose();
        Assert.AreEqual(1, ports.Disposals);
        Assert.AreEqual(0, independent.Disposals);
        other.ReadControlState();
        other.Dispose();

        var failedOpen = new Ports { FailOpen = true };
        var failed = failedOpen.Create();
        Assert.ThrowsExactly<IOException>(failed.Open);
        failed.Dispose();
        Assert.AreEqual(1, failedOpen.Disposals);
        Assert.AreEqual(0, failedOpen.Transfers + failedOpen.ValueWrites);
    }

    private sealed class Ports : IFanEcPortAccess
    {
        internal bool Allowed = true, FailAcquire, FailOpen, BusHeld;
        internal int Transfers, Opens, Acquisitions, ValueWrites, Disposals;
        internal Action? AfterAcquire;
        internal Action<int>? AfterTransfer;
        internal readonly List<ushort> ReadAddresses = new();
        internal readonly Dictionary<ushort, byte> Registers = new()
        {
            [0x2000] = 0x55, [0x1060] = 0x81, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0
        };
        private byte index, selector, high, low;
        private ushort Address => (ushort)((high << 8) | low);

        internal BldingFanEcTransport Create(Action<FanEcRecoveryCheckpoint>? checkpoint = null) =>
            new(this, () => Allowed, () => Allowed, checkpoint);

        public IDisposable AcquireBus()
        {
            Assert.IsFalse(EcHeld(), "ISA must be acquired before EC.");
            Acquisitions++;
            if (FailAcquire) throw new IOException("fakeIsaUnavailable");
            Assert.IsFalse(BusHeld);
            BusHeld = true;
            AfterAcquire?.Invoke();
            return new Lease(() => { Assert.IsFalse(EcHeld()); BusHeld = false; });
        }

        public void Open()
        {
            AssertLocked();
            Opens++;
            if (FailOpen) throw new IOException("fakeModuleOpenFailure");
        }

        public byte ReadByte(ushort port)
        {
            AssertLocked();
            Assert.AreEqual((ushort)0x4F, port);
            ReadAddresses.Add(Address);
            byte value = Registers[Address];
            Transfer();
            return value;
        }

        public void WriteByte(ushort port, byte value)
        {
            AssertLocked();
            if (port == 0x4E) index = value;
            else if (index == 0x2E) selector = value;
            else if (selector == 0x11) high = value;
            else if (selector == 0x10) low = value;
            else { Registers[Address] = value; ValueWrites++; }
            Transfer();
        }

        private void Transfer() { Transfers++; AfterTransfer?.Invoke(Transfers); }
        private void AssertLocked() { Assert.IsTrue(BusHeld); Assert.IsTrue(EcHeld()); }
        private static bool EcHeld()
        {
            bool held = false;
            Exception? failure = null;
            int callerThread = Environment.CurrentManagedThreadId;
            var probe = new Thread(() =>
            {
                try
                {
                    Assert.AreNotEqual(callerThread, Environment.CurrentManagedThreadId);
                    using var mutex = new Mutex(false, @"Global\Access_EC");
                    bool acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { acquired = true; throw; }
                        held = !acquired;
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
                }
                catch (Exception error) { failure = error; }
            });
            probe.Start();
            probe.Join();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return held;
        }

        public void Dispose() { AssertLocked(); Disposals++; }
        private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
