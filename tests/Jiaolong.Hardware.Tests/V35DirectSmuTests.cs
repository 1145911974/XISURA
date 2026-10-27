using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class V35DirectSmuTests
{
    [TestMethod]
    public async Task Direct_limits_use_same_mp1_messages_for_write_and_fractional_restore_without_oem_access()
    {
        var smu = new Smu();
        var transport = new WindowsCpuTuningTransport(null!, null!, null!, smuLimits: smu);
        await transport.CaptureTransactionAsync([new(null, null, null, null, null, null, null, null)
            { Advanced = new(PptWatts: 60.125, Mp1TemperatureC: 80) }], CancellationToken.None);
        await transport.WriteFieldAsync(CpuTuningField.PptWatts, 60.125d, CancellationToken.None);
        await transport.WriteFieldAsync(CpuTuningField.Mp1TemperatureC, 80, CancellationToken.None);
        await transport.WriteFieldAsync(CpuTuningField.PptWatts, 120d, CancellationToken.None);
        foreach (double invalid in new[] { 44.999, 120.001, 60.0005, double.NaN, double.PositiveInfinity })
            await Assert.ThrowsAsync<ArgumentException>(async () => await transport.WriteFieldAsync(CpuTuningField.PptWatts, invalid, CancellationToken.None));
        await transport.RestoreFieldAsync(CpuTuningField.PptWatts, 120.125d, CancellationToken.None);
        await transport.RestoreFieldAsync(CpuTuningField.Mp1TemperatureC, 99, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { new RyzenSmuLimitWrite(true, 0x3E, 60125), new(true, 0x3F, 80),
            new(true, 0x3E, 120000), new(true, 0x3E, 120125), new(true, 0x3F, 99) }, smu.Writes.ToArray());
        Assert.AreEqual(120.125d, await transport.ReadFieldAsync(CpuTuningField.PptWatts, CancellationToken.None));
        Assert.AreEqual(99, await transport.ReadFieldAsync(CpuTuningField.Mp1TemperatureC, CancellationToken.None));
        smu.Version = 0x540108;
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.WriteFieldAsync(CpuTuningField.PptWatts, 120d, CancellationToken.None));
        Assert.AreEqual(5, smu.Writes.Count);
    }

    [TestMethod]
    public void V35_power_capture_rejects_values_that_cannot_represent_exact_integer_milliwatts()
    {
        var table = Smu.Table(120.00001f);
        Assert.AreEqual(120d, RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table).Limits.PptWatts);
        foreach (float raw in new[] { 120.0004f, float.NaN, float.PositiveInfinity })
        {
            table[2] = raw;
            Assert.ThrowsExactly<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table));
        }
    }

    private sealed class Smu : ISmuCpuLimitTransport
    {
        private readonly float[] table = Table(120.125f);
        public uint Version { get; set; } = 0x540105;
        public List<RyzenSmuLimitWrite> Writes { get; } = [];
        public bool FailAfterTdcOnce { get; set; }
        public bool FailAfterEdcOnce { get; set; }
        internal static float[] Table(float ppt)
        {
            var values = new float[64];
            values[2] = ppt; values[8] = 108; values[10] = 99; values[61] = 160;
            return values;
        }
        public CpuSmuLimitSnapshot ReadLimitSnapshot(CancellationToken token) => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table) with { PmTableVersion = Version };
        public void WriteAdvancedLimit(RyzenSmuLimitWrite write, CancellationToken token)
        {
            Writes.Add(write);
            if (write.Command == 0x3F) table[10] = write.Argument;
            else if (write.Command == 0x3C && write.Mp1)
            {
                table[8] = write.Argument / 1000f;
                if (FailAfterTdcOnce) { FailAfterTdcOnce = false; throw new IOException("tdcWriteInterrupted"); }
            }
            else if (write.Command == 0x3D && write.Mp1)
            {
                table[61] = write.Argument / 1000f;
                if (FailAfterEdcOnce) { FailAfterEdcOnce = false; throw new IOException("edcWriteInterrupted"); }
            }
            else table[2] = write.Argument / 1000f;
        }
    }

    [TestMethod]
    public async Task V35_tdc_uses_exact_integer_milliamps_and_same_mp1_route_for_write_restore_and_rollback()
    {
        var table = Smu.Table(120);
        Assert.AreEqual(108000, RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table).Limits.TdcCurrentMilliamps);
        table[8] = MathF.BitIncrement(108f);
        Assert.AreEqual(108000, RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table).Limits.TdcCurrentMilliamps);
        foreach (float raw in new[] { 108.0004f, float.NaN, float.PositiveInfinity, .9f, 200.001f })
        {
            table[8] = raw;
            Assert.ThrowsExactly<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table));
        }
        var smu = new Smu();
        var transport = new WindowsCpuTuningTransport(null!, null!, null!, smuLimits: smu);
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null) { Advanced = new(TdcCurrentMilliamps: 107999) };
        var controller = new PerformanceController(cpuTransport: transport);
        Assert.AreEqual(CommandState.Applied, (await controller.ApplyCpuTuningAsync(plan, true, CancellationToken.None)).State);
        Assert.AreEqual(107999, await transport.ReadFieldAsync(CpuTuningField.TdcCurrentMilliamps, CancellationToken.None));
        await transport.RestoreFieldAsync(CpuTuningField.TdcCurrentMilliamps, 108000, CancellationToken.None);
        smu.FailAfterTdcOnce = true;
        Assert.AreEqual(CommandState.RolledBack, (await controller.ApplyCpuTuningAsync(plan, true, CancellationToken.None)).State);
        Assert.AreEqual(108000, await transport.ReadFieldAsync(CpuTuningField.TdcCurrentMilliamps, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { new RyzenSmuLimitWrite(true, 0x3C, 107999), new(true, 0x3C, 108000),
            new(true, 0x3C, 107999), new(true, 0x3C, 108000) }, smu.Writes.ToArray());
        Assert.AreEqual(120.125, await transport.ReadFieldAsync(CpuTuningField.PptWatts, CancellationToken.None));
        foreach (int invalid in new[] { 999, 200001 })
            await Assert.ThrowsAsync<ArgumentException>(async () => await transport.WriteFieldAsync(CpuTuningField.TdcCurrentMilliamps, invalid, CancellationToken.None));
    }
    [TestMethod]
    public async Task V35_edc_uses_exact_integer_milliamps_and_same_mp1_route_for_write_restore_and_rollback()
    {
        var table = Smu.Table(120);
        Assert.AreEqual(160000, RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table).Limits.EdcCurrentMilliamps);
        table[61] = MathF.BitIncrement(160f);
        Assert.AreEqual(160000, RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table).Limits.EdcCurrentMilliamps);
        foreach (float raw in new[] { 160.0004f, float.NaN, float.PositiveInfinity, .9f, 200.001f })
        {
            table[61] = raw;
            Assert.ThrowsExactly<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodeSnapshot(0x540105, table));
        }
        var smu = new Smu();
        var transport = new WindowsCpuTuningTransport(null!, null!, null!, smuLimits: smu);
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null) { Advanced = new(EdcCurrentMilliamps: 159999) };
        var controller = new PerformanceController(cpuTransport: transport);
        Assert.AreEqual(CommandState.Applied, (await controller.ApplyCpuTuningAsync(plan, true, CancellationToken.None)).State);
        Assert.AreEqual(159999, await transport.ReadFieldAsync(CpuTuningField.EdcCurrentMilliamps, CancellationToken.None));
        await transport.RestoreFieldAsync(CpuTuningField.EdcCurrentMilliamps, 160000, CancellationToken.None);
        smu.FailAfterEdcOnce = true;
        Assert.AreEqual(CommandState.RolledBack, (await controller.ApplyCpuTuningAsync(plan, true, CancellationToken.None)).State);
        Assert.AreEqual(160000, await transport.ReadFieldAsync(CpuTuningField.EdcCurrentMilliamps, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { new RyzenSmuLimitWrite(true, 0x3D, 159999), new(true, 0x3D, 160000),
            new(true, 0x3D, 159999), new(true, 0x3D, 160000) }, smu.Writes.ToArray());
        Assert.AreEqual(120.125, await transport.ReadFieldAsync(CpuTuningField.PptWatts, CancellationToken.None));
        foreach (int invalid in new[] { 999, 200001 })
            await Assert.ThrowsAsync<ArgumentException>(async () => await transport.WriteFieldAsync(CpuTuningField.EdcCurrentMilliamps, invalid, CancellationToken.None));
    }

}
