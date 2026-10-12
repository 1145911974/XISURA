using System.Reflection;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class BldingLifecycleTests
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
            Assert.AreEqual(write ? 1 : 0, bus.ValueWrites);
            Assert.AreEqual(write, transport.HasWritten);
        }

        var failing = new PortBus { FailTransfer = 1 };
        failing.AfterTransfer = _ => failing.AccessAllowed = failing.ReadAllowed = false;
        var failedTransport = failing.Create();
        Paused(() => failedTransport.SetTargets(40, 41));
        Assert.IsTrue(failedTransport.HasWritten, "A failing selector cannot prove the write transaction was untouched.");
        Assert.AreEqual(1, failing.Transfers);
    }

    [TestMethod]
    public void Recovery_read_permission_never_authorizes_initialization_control_or_native_shutdown()
    {
        var bus = new PortBus { AccessAllowed = false, ReadAllowed = true };
        var transport = bus.Create();
        _ = transport.ReadControlState();
        Assert.AreEqual(4, bus.ValueReads);
        Assert.AreEqual(0, bus.ValueWrites);
        foreach (Action operation in new Action[] { transport.Open, () => transport.SetTargets(40, 40),
                     transport.Clear, transport.Dispose }) Paused(operation);
        Assert.AreEqual(48, bus.Transfers);
        Assert.AreEqual(0, bus.Shutdowns);
        bus.ReadAllowed = false;
        Paused(() => transport.ReadControlState());
        Assert.AreEqual(48, bus.Transfers);
    }

    [TestMethod]
    public void Interrupted_clear_preserves_snapshot_and_shutdown_is_not_repeated_after_completion()
    {
        foreach (int pauseAtWrite in new[] { 1, 4 })
        {
            var bus = new PortBus();
            var transport = bus.Create();
            transport.SetTargets(40, 41);
            Assert.AreEqual("bldingRecoveryRequired", Assert.ThrowsExactly<IOException>(transport.Dispose).Message);
            Assert.AreEqual(0, bus.Shutdowns);
            SetField(transport, "previousInitialization", (byte)0x01);
            bus.AfterTransfer = _ =>
            {
                if (bus.ValueWrites == 4 + pauseAtWrite) bus.AccessAllowed = bus.ReadAllowed = false;
            };
            Paused(transport.Clear);
            Assert.IsTrue(transport.HasWritten);
            Assert.AreEqual((byte)0x01, Field<byte?>(transport, "previousInitialization"));
            Assert.AreEqual(4 + pauseAtWrite, bus.ValueWrites);
            Paused(transport.Dispose);
            Assert.AreEqual(0, bus.Shutdowns);
            bus.AccessAllowed = bus.ReadAllowed = true;
            bus.AfterTransfer = null;
            transport.Clear();
            Assert.IsFalse(transport.HasWritten);
            Assert.IsNull(Field<byte?>(transport, "previousInitialization"));
            Assert.AreEqual((byte)0x01, bus.Registers[0x1060]);
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

    private static void Paused(Action action) =>
        Assert.AreEqual("hardwareAccessPaused", Assert.ThrowsExactly<IOException>(action).Message);

    private static T? Field<T>(object target, string name) =>
        (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class PortBus
    {
        public bool AccessAllowed = true, ReadAllowed = true;
        public int Transfers, ValueReads, ValueWrites, Shutdowns;
        public int? FailTransfer;
        public Action<int>? AfterTransfer;
        public Action? OnShutdown;
        public readonly Dictionary<ushort, byte> Registers = new()
        {
            [0x1060] = 0x81, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0
        };
        private byte index, selector, high, low;
        private ushort Address => (ushort)((high << 8) | low);

        public BldingFanEcTransport Create() => new(Read, Write, () => { Shutdowns++; OnShutdown?.Invoke(); },
            () => AccessAllowed, () => ReadAllowed);

        private byte Read(ushort port)
        {
            Assert.AreEqual((ushort)0x4F, port);
            ValueReads++;
            var value = Registers[Address];
            Transfer();
            return value;
        }

        private void Write(ushort port, byte value)
        {
            if (port == 0x4E) index = value;
            else if (index == 0x2E) selector = value;
            else if (selector == 0x11) high = value;
            else if (selector == 0x10) low = value;
            else { Registers[Address] = value; ValueWrites++; }
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
