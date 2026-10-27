using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CpuModeIndependentWriteTests
{
    [TestMethod]
    public async Task Unknown_firmware_prepare_rejects_before_any_mode_or_limit_write()
    {
        var bus = new CpuBus();
        var transport = Transport(bus);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            transport.PrepareOemLimitsAsync([new(75, null, 60, null, null, null, null, null)], CancellationToken.None));
        Assert.AreEqual("cpuPowerModeIndependentProtocolUnverified", error.Message);
        Assert.AreEqual(0, bus.Writes.Count);
    }

    [TestMethod]
    public async Task Unknown_firmware_field_writes_reject_before_any_mode_or_limit_write()
    {
        foreach (var (field, value) in new[] { (CpuTuningField.TemperatureLimitC, 75), (CpuTuningField.SpptWatts, 60), (CpuTuningField.SplWatts, 45) })
        {
            var bus = new CpuBus();
            var transport = Transport(bus);
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                transport.WriteFieldAsync(field, value, CancellationToken.None));
            Assert.AreEqual("cpuPowerModeIndependentProtocolUnverified", error.Message);
            Assert.AreEqual(0, bus.Writes.Count);
        }
    }

    [TestMethod]
    public async Task Blocked_oem_calls_preserve_lifecycle_cancellation_and_do_not_start_recovery()
    {
        var bus = new CpuBus();
        var transport = Transport(bus);
        foreach (var field in new[] { CpuTuningField.TemperatureLimitC, CpuTuningField.SpptWatts, CpuTuningField.SplWatts })
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                transport.WriteFieldAsync(field, 60, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                transport.RestoreFieldAsync(field, 60, CancellationToken.None));
        }
        await transport.RestoreTransactionAsync(CancellationToken.None);
        var paused = Transport(bus, () => false);
        await Assert.ThrowsExactlyAsync<HardwareLifecycleInterruptedException>(() =>
            paused.WriteFieldAsync(CpuTuningField.TemperatureLimitC, 75, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<HardwareLifecycleInterruptedException>(() =>
            paused.PrepareOemLimitsAsync([new(75, null, null, null, null, null, null, null)], CancellationToken.None));
        await Assert.ThrowsExactlyAsync<HardwareLifecycleInterruptedException>(() =>
            paused.CaptureTransactionAsync([new(75, null, null, null, null, null, null, null)], CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            transport.WriteFieldAsync(CpuTuningField.TemperatureLimitC, 75, cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            transport.PrepareOemLimitsAsync([new(75, null, null, null, null, null, null, null)], cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            transport.CaptureTransactionAsync([new(75, null, null, null, null, null, null, null)], cancellation.Token));
        Assert.AreEqual(0, bus.Reads);
        Assert.AreEqual(0, bus.Writes.Count);

        var smu = new CpuSmu();
        var power = new BaselinePower();
        var original = power.Snapshot;
        var captured = Transport(bus, smu: smu, power: power);
        await captured.CaptureTransactionAsync([new(null, null, null, 3500, null, null, null, null)], true, CancellationToken.None);
        await captured.WriteFieldAsync(CpuTuningField.MaxFrequencyMhz, new WindowsPowerFrequencyValue(3500, 2500), CancellationToken.None);
        var changed = power.Snapshot;
        int nativeCalls = 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                captured.CaptureTransactionAsync([new(75, null, 60, null, null, null, null, null)], true, CancellationToken.None));
            Assert.AreEqual("cpuPowerModeIndependentProtocolUnverified", error.Message);
            Assert.AreEqual(changed, power.Snapshot);
            Assert.AreEqual(0, power.Restores);
        }
        Assert.AreEqual(0, nativeCalls);
        await captured.RestoreFieldAsync(CpuTuningField.MaxFrequencyMhz,
            new WindowsPowerFrequencyValue(original.AcMaxFrequencyMhz, original.DcMaxFrequencyMhz), CancellationToken.None);
        await captured.RestoreTransactionAsync(_ => { nativeCalls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.AreEqual(1, nativeCalls);
        Assert.AreEqual(original, power.Snapshot, "Rejected OEM capture must preserve the exact prior Windows snapshot.");
        Assert.AreEqual(1, power.Restores, "Only the explicit write is restored.");
        await captured.RestoreTransactionAsync(CancellationToken.None);
        Assert.AreEqual(1, power.Restores, "Only successful Windows recovery releases its snapshot.");
        Assert.AreEqual(0, bus.Reads, "OEM preflight must reject before querying or changing CMEN.");
        Assert.AreEqual(0, bus.Writes.Count);
        Assert.AreEqual(0, smu.Reads);
        Assert.AreEqual(0, smu.Writes);
    }

    private static WindowsCpuTuningTransport Transport(CpuBus bus, Func<bool>? allowed = null, ISmuCpuLimitTransport? smu = null,
        IWindowsPowerSettingsTransport? power = null) => new(
        new MiCommonInterfaceClient(bus, bus),
        new VerifiedWmiBinding("root\\WMI", "MICommonInterface", @"ACPI\PNP0C14\MIFS_0", 250, 251, 23, "MRID6_23_P_V39"), power!,
        smuLimits: smu, hardwareAccessAllowed: allowed);

    private sealed class BaselinePower : IWindowsPowerSettingsTransport
    {
        public WindowsPowerSettingsSnapshot Snapshot { get; private set; } = new(Guid.NewGuid(), 4800, 3100, 4, 1)
            { AcMinActiveCoresPercent = 17, DcMinActiveCoresPercent = 10 };
        public int Restores { get; private set; }
        public Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task RestoreAsync(CpuTuningField field, WindowsPowerSettingsSnapshot original, CancellationToken token)
        { Restores++; Snapshot = original; return Task.CompletedTask; }
        public Task SetMaxFrequencyAsync(int ac, int dc, CancellationToken token)
        { Snapshot = Snapshot with { AcMaxFrequencyMhz = ac, DcMaxFrequencyMhz = dc }; return Task.CompletedTask; }
        public Task SetSchemeAsync(Guid value, CancellationToken token) => throw new AssertFailedException();
        public Task SetBoostAsync(bool value, CancellationToken token) => throw new AssertFailedException();
        public Task SetCoreParkingAsync(int ac, int dc, CancellationToken token) => throw new AssertFailedException();
    }

    private sealed class CpuSmu : ISmuCpuLimitTransport
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public CpuSmuLimitSnapshot ReadLimitSnapshot(CancellationToken token)
        {
            Reads++;
            return new(new(StapmWatts: 45, FastPptWatts: 60, SlowPptWatts: 45,
                VrmCurrentMilliamps: 200000, EdcCurrentMilliamps: 180000, Mp1TemperatureC: 85), 45);
        }
        public void WriteAdvancedLimit(RyzenSmuLimitWrite write, CancellationToken token) => Writes++;
    }

    private sealed class CpuBus : IMiReadTransport, IMiWriteTransport
    {
        public bool Enabled { get; private set; }
        public int Reads { get; private set; }
        public List<byte[]> Writes { get; } = [];
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        {
            Reads++;
            return Task.FromResult<byte[]?>([0, 0, 0, 0, Enabled ? (byte)1 : (byte)0]);
        }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            Writes.Add(request.ToArray());
            if (request.Span[4] == 1) Enabled = true;
            return Task.FromResult<byte[]?>([]);
        }
    }
}
