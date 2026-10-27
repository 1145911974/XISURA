using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class BldingLifecycleTests
{
    [TestMethod]
    public void Recovery_open_retains_original_baseline_without_value_writes_and_clear_preserves_unrelated_bits()
    {
        var bus = new PortBus();
        bus.Registers[0x1060] = 0xA1;
        FanEcRecoveryCheckpoint? saved = null;
        bus.Create(checkpoint => saved = checkpoint).SetTargets(40, 41);
        var original = saved!.Baseline.ToDictionary(pair => pair.Key, pair => pair.Value);
        bus.Registers[0x1060] ^= 0x10;
        bus.Registers[0x0B20] |= 0x40;
        int writes = bus.ValueWrites;
        var checkpoints = new List<FanEcRecoveryCheckpoint>();
        var recovery = bus.Create(checkpoint => checkpoints.Add(checkpoint));
        recovery.OpenForRecovery(saved);
        Assert.AreEqual(writes, bus.ValueWrites, "Recovery open must not request new fan targets or set EC initialization.");
        Assert.ThrowsExactly<IOException>(() => recovery.SetTargets(42, 43));
        Assert.AreEqual(writes, bus.ValueWrites);
        Assert.IsTrue(recovery.HasWritten);
        recovery.Clear();
        Assert.IsFalse(recovery.HasWritten);
        Assert.AreEqual((byte)0, bus.Registers[0xC83C]);
        Assert.AreEqual((byte)0, bus.Registers[0xC83D]);
        Assert.AreEqual((byte)0xB1, bus.Registers[0x1060]);
        Assert.AreEqual((byte)0x40, bus.Registers[0x0B20]);
        Assert.IsTrue(checkpoints.Last().Released);
        CollectionAssert.AreEquivalent(original.ToArray(), checkpoints.Last().Baseline.ToArray());
        int releasedWrites = bus.ValueWrites;
        recovery.Dispose();
        Assert.AreEqual(releasedWrites, bus.ValueWrites);
        Assert.AreEqual(1, bus.Shutdowns);
        var reopened = bus.Create(checkpoint => checkpoints.Add(checkpoint));
        reopened.OpenForRecovery(checkpoints.Last());
        reopened.Clear();
        reopened.Dispose();
        Assert.AreEqual(releasedWrites, bus.ValueWrites);
        Assert.IsTrue(checkpoints.Last().Released);
    }

    [TestMethod]
    public void Recovery_open_refuses_foreign_targets_and_storage_failure_without_value_writes_or_shutdown()
    {
        foreach (bool storageFailure in new[] { false, true })
        {
            var bus = new PortBus();
            FanEcRecoveryCheckpoint? saved = null;
            bus.Create(checkpoint => saved = checkpoint).SetTargets(40, 41);
            if (!storageFailure) bus.Registers[0xC83C] = 42;
            int writes = bus.ValueWrites;
            var recovery = bus.Create(_ => { if (storageFailure) throw new IOException("diskUnavailable"); });
            Assert.ThrowsExactly<IOException>(() => recovery.OpenForRecovery(saved!));
            Assert.AreEqual(writes, bus.ValueWrites);
            Assert.AreEqual(0, bus.Shutdowns);
            Assert.IsTrue(recovery.HasWritten);
        }
    }

    [TestMethod]
    public void Recovery_open_reconciles_pending_before_or_attempted_values_and_unchanged_capture_never_clears_external_targets()
    {
        foreach (byte observed in new byte[] { 40, 42 })
        {
            var bus = new PortBus();
            FanEcRecoveryCheckpoint? saved = null;
            bus.Create(checkpoint => saved = checkpoint).SetTargets(40, 41);
            var pending = saved! with { Expected = new(saved!.Expected) { [0xC83C] = 42 },
                Pending = new() { [0xC83C] = [40, 42] } };
            bus.Registers[0xC83C] = observed;
            var recovery = bus.Create(_ => { });
            recovery.OpenForRecovery(pending);
            recovery.Clear();
            Assert.AreEqual((byte)0, bus.Registers[0xC83C]);
            recovery.Dispose();
        }
        var untouched = new PortBus();
        untouched.Registers[0xC83C] = 30;
        untouched.Registers[0xC83D] = 31;
        var baseline = new Dictionary<ushort, byte>(untouched.Registers);
        FanEcRecoveryCheckpoint? final = null;
        var noWrite = untouched.Create(checkpoint => final = checkpoint);
        noWrite.OpenForRecovery(new(baseline, new(baseline), new(), false));
        noWrite.Clear();
        noWrite.Dispose();
        Assert.AreEqual(0, untouched.ValueWrites);
        Assert.AreEqual((byte)30, untouched.Registers[0xC83C]);
        Assert.IsTrue(final!.Unchanged);
    }

    [TestMethod]
    public void Recovery_open_rejects_invalid_or_unjournaled_checkpoint_before_any_port_io()
    {
        var bus = new PortBus();
        int transfers = bus.Transfers;
        var baseline = new Dictionary<ushort, byte>(bus.Registers);
        var invalid = new FanEcRecoveryCheckpoint(baseline, new(baseline), new(), true);
        invalid.Expected.Remove(0xC83C);
        Assert.ThrowsExactly<InvalidDataException>(() => bus.Create(_ => { }).OpenForRecovery(invalid));
        Assert.ThrowsExactly<InvalidDataException>(() => bus.Create().OpenForRecovery(new(baseline, new(baseline), new(), false)));
        Assert.AreEqual(transfers, bus.Transfers);
        Assert.AreEqual(0, bus.ValueWrites);
    }
}
