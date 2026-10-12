using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CpuTransportLifecycleTests
{
    [TestMethod]
    public async Task Cpu_transport_stops_internal_capture_enable_and_recovery_calls_at_pause()
    {
        foreach (string stage in new[] { "windows-restore", "windows-restore-error", "oem-capture", "oem-enable", "oem-restore" })
        {
            var trace = new Trace();
            var power = new FakePower(trace);
            var bus = new FakeBus(trace) { Enabled = stage != "oem-enable" };
            var smu = new FakeSmu(trace);
            var binding = new VerifiedWmiBinding("root\\WMI", "MICommonInterface", "MI", 250, 251, 23, "verified");
            var transport = new WindowsCpuTuningTransport(new MiCommonInterfaceClient(bus, bus), binding, power,
                smuLimits: smu, hardwareAccessAllowed: () => trace.Allowed);
            var windows = new CpuTuningPlan(null, null, null, 3800, null, null, null, null);
            var oem = new CpuTuningPlan(75, 45, 60, null, null, null, null, null);
            Func<Task> operation;
            if (stage.StartsWith("windows-restore", StringComparison.Ordinal))
            {
                await transport.CaptureTransactionAsync([windows], true, CancellationToken.None);
                trace.PauseOn = "power.restore";
                power.FailRestore = stage == "windows-restore-error";
                operation = () => transport.RestoreTransactionAsync(_ => Task.CompletedTask, CancellationToken.None);
            }
            else if (stage == "oem-capture")
            {
                trace.PauseOn = "smu.read";
                operation = () => transport.CaptureTransactionAsync([oem], true, CancellationToken.None);
            }
            else if (stage == "oem-enable")
            {
                trace.PauseOn = "mi.enable";
                operation = () => transport.WriteFieldAsync(CpuTuningField.TemperatureLimitC, 75, CancellationToken.None);
            }
            else
            {
                await transport.CaptureTransactionAsync([oem], true, CancellationToken.None);
                trace.PauseOn = "mi.limit";
                operation = () => transport.RestoreTransactionAsync(_ =>
                { trace.Record("native.restore"); return Task.CompletedTask; }, CancellationToken.None);
            }
            trace.Calls.Clear();
            Exception? failure = null;
            try { await operation(); }
            catch (Exception error) { failure = error; }
            Assert.IsNotNull(failure, stage);
            Assert.AreEqual("hardwareAccessPaused", failure.Message, stage);
            Assert.AreEqual("paused", trace.Calls.Last(), "No internal hardware call after pause: " + stage);
            if (stage.StartsWith("windows-restore", StringComparison.Ordinal))
            {
                Assert.AreEqual(1, power.Restores);
                trace.Allowed = true;
                trace.PauseOn = null;
                power.FailRestore = false;
                await transport.RestoreTransactionAsync(_ => Task.CompletedTask, CancellationToken.None);
                Assert.AreEqual(5, power.Restores, "Interrupted recovery must retain its original snapshot.");
                await transport.RestoreTransactionAsync(CancellationToken.None);
                Assert.AreEqual(5, power.Restores, "Only successful recovery releases the snapshot.");
            }
        }
    }

    private sealed class Trace
    {
        public bool Allowed { get; set; } = true;
        public string? PauseOn { get; set; }
        public List<string> Calls { get; } = [];
        public void Record(string call)
        {
            Calls.Add(call);
            if (call == PauseOn) { Allowed = false; Calls.Add("paused"); }
        }
    }

    private sealed class FakePower(Trace trace) : IWindowsPowerSettingsTransport
    {
        private readonly WindowsPowerSettingsSnapshot snapshot = new(Guid.NewGuid(), 3800, 3000, 2, 0)
        { AcMinActiveCoresPercent = 17, DcMinActiveCoresPercent = 10 };
        public bool FailRestore { get; set; }
        public int Restores { get; private set; }
        public Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken token)
        { trace.Record("power.read"); return Task.FromResult(snapshot); }
        public Task RestoreAsync(CpuTuningField field, WindowsPowerSettingsSnapshot original, CancellationToken token)
        {
            Restores++;
            trace.Record("power.restore");
            if (FailRestore) throw new InvalidOperationException("simulatedRestoreFailure");
            return Task.CompletedTask;
        }
        public Task SetSchemeAsync(Guid value, CancellationToken token) => throw new AssertFailedException();
        public Task SetMaxFrequencyAsync(int ac, int dc, CancellationToken token) => throw new AssertFailedException();
        public Task SetBoostAsync(bool value, CancellationToken token) => throw new AssertFailedException();
        public Task SetCoreParkingAsync(int ac, int dc, CancellationToken token) => throw new AssertFailedException();
    }

    private sealed class FakeBus(Trace trace) : IMiReadTransport, IMiWriteTransport
    {
        public bool Enabled { get; set; }
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        { trace.Record("mi.read"); return Task.FromResult<byte[]?>([0, 0, 0, 0, Enabled ? (byte)1 : (byte)0]); }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            if (request.Span[4] == 1) { Enabled = true; trace.Record("mi.enable"); }
            else trace.Record("mi.limit");
            return Task.FromResult<byte[]?>([]);
        }
    }

    private sealed class FakeSmu(Trace trace) : ISmuCpuLimitTransport
    {
        public CpuSmuLimitSnapshot ReadLimitSnapshot(CancellationToken token)
        {
            trace.Record("smu.read");
            return new(new(StapmWatts: 45, FastPptWatts: 60, SlowPptWatts: 45,
                VrmCurrentMilliamps: 200000, EdcCurrentMilliamps: 180000, Mp1TemperatureC: 85), 45);
        }
        public void WriteAdvancedLimit(RyzenSmuLimitWrite write, CancellationToken token) => trace.Record("smu.write");
    }
}
