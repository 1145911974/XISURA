using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class FractionalCpuReadbackTests
{
    private static float[] Table(float spl = 40.5f, float sppt = 58.5f)
    {
        var table = new float[64];
        table[0] = 40.500004f; table[2] = sppt; table[4] = 40.500004f;
        table[6] = spl; table[8] = 108.00001f; table[10] = 99; table[62] = 160.001f;
        return table;
    }

    [TestMethod]
    public void Fractional_spl_preserves_exact_power_and_other_readable_limits()
    {
        var snapshot = RyzenSmuAdvancedLimits.DecodeSnapshot(0x540108, Table());
        Assert.AreEqual(40.5d, snapshot.OemSplWatts);
        Assert.AreEqual(58.5d, snapshot.Limits.FastPptWatts);
        Assert.AreEqual(99, snapshot.Limits.Mp1TemperatureC);
        Assert.AreEqual(108000, snapshot.Limits.TdcCurrentMilliamps);
    }

    [TestMethod]
    public async Task Fractional_oem_originals_are_rejected_before_bus_access_or_writes()
    {
        foreach (var table in new[] { Table(), Table(45, 58.5f) })
        {
            var bus = new Bus();
            var transport = Transport(table, bus);
            Assert.AreEqual(99, await transport.ReadFieldAsync(CpuTuningField.TemperatureLimitC, CancellationToken.None));
            Assert.IsNull(await transport.ReadFieldAsync(CpuTuningField.SpptWatts, CancellationToken.None));
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                transport.CaptureTransactionAsync([new(99, 60, 75, null, null, null, null, null)], CancellationToken.None));
            Assert.AreEqual("cpuPowerModeIndependentProtocolUnverified", error.Message);
            await transport.RestoreTransactionAsync(CancellationToken.None);
            Assert.AreEqual(0, bus.Reads);
            Assert.AreEqual(0, bus.Writes);
        }
    }

    [TestMethod]
    public async Task Integer_oem_originals_keep_integer_field_readback_but_capture_remains_blocked()
    {
        var bus = new Bus();
        var transport = Transport(Table(45.000004f, 60.000004f), bus);
        Assert.AreEqual(45, await transport.ReadFieldAsync(CpuTuningField.SplWatts, CancellationToken.None));
        Assert.AreEqual(60, await transport.ReadFieldAsync(CpuTuningField.SpptWatts, CancellationToken.None));
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            transport.CaptureTransactionAsync([new(99, 60, 75, null, null, null, null, null)], CancellationToken.None));
        Assert.AreEqual("cpuPowerModeIndependentProtocolUnverified", error.Message);
        Assert.AreEqual(0, bus.Reads);
        Assert.AreEqual(0, bus.Writes);
    }

    [TestMethod]
    public void Invalid_or_unknown_pm_tables_remain_rejected()
    {
        foreach (float spl in new[] { float.NaN, float.PositiveInfinity, 0f, 251f })
            Assert.ThrowsExactly<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540108, Table(spl)));
        Assert.ThrowsExactly<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540104, Table()));
    }

    [TestMethod]
    public void V35_known_layout_reads_only_documented_fields_and_keeps_oem_original_unknown()
    {
        var table = Table();
        table[0] = table[4] = table[6] = table[62] = float.NaN;
        table[2] = 120.00001f; table[61] = 160.00001f;
        var snapshot = RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table);
        Assert.AreEqual(120d, snapshot.Limits.PptWatts);
        Assert.AreEqual(108000, snapshot.Limits.TdcCurrentMilliamps);
        Assert.AreEqual(160000, snapshot.Limits.EdcCurrentMilliamps);
        Assert.AreEqual(99, snapshot.Limits.Mp1TemperatureC);
        Assert.AreEqual<double?>(null, snapshot.OemSplWatts);
        Assert.IsNull(snapshot.Limits.StapmWatts);
        Assert.IsNull(snapshot.Limits.FastPptWatts);
        Assert.IsNull(snapshot.Limits.SlowPptWatts);
        Assert.IsNull(snapshot.Limits.VrmCurrentMilliamps);
        Assert.IsNull(snapshot.Limits.RsmuTemperatureC);
        foreach (int slot in new[] { 2, 8, 10, 61 })
        {
            float original = table[slot];
            table[slot] = float.NaN;
            Assert.ThrowsExactly<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table));
            table[slot] = original;
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table.AsSpan(0, 61)));
    }

    private static WindowsCpuTuningTransport Transport(float[] table, Bus bus) => new(
        new MiCommonInterfaceClient(bus, bus), new("root\\WMI", "MICommonInterface", "MI", 250, 251, 23, "test"),
        null!, smuLimits: new Smu(table));

    private sealed class Smu(float[] table) : ISmuCpuLimitTransport
    {
        public CpuSmuLimitSnapshot ReadLimitSnapshot(CancellationToken token) => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540108, table);
        public void WriteAdvancedLimit(RyzenSmuLimitWrite write, CancellationToken token) =>
            throw new InvalidOperationException("No hardware writes allowed in readback tests");
    }

    private sealed class Bus : IMiReadTransport, IMiWriteTransport
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        { Reads++; return Task.FromResult<byte[]?>([0, 0, 0, 0, 0]); }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        { Writes++; throw new InvalidOperationException("No hardware writes allowed in readback tests"); }
    }
}
