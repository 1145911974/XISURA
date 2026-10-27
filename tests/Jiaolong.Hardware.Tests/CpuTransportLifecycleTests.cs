using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CpuTransportLifecycleTests
{
    [TestMethod]
    public async Task Cpu_transport_stops_internal_capture_and_windows_recovery_calls_at_pause()
    {
        foreach (string stage in new[] { "windows-read", "windows-read-error" })
        {
            var trace = new Trace();
            var power = new FakePower(trace);
            var bus = new FakeBus(trace) { Enabled = true };
            var smu = new FakeSmu(trace);
            var binding = new VerifiedWmiBinding("root\\WMI", "MICommonInterface", "MI", 250, 251, 23, "verified");
            var transport = new WindowsCpuTuningTransport(new MiCommonInterfaceClient(bus, bus), binding, power,
                smuLimits: smu, hardwareAccessAllowed: () => trace.Allowed);
            var windows = new CpuTuningPlan(null, null, null, 3800, null, null, null, null);
            await transport.CaptureTransactionAsync([windows], true, CancellationToken.None);
            trace.PauseOn = "power.read";
            power.FailRead = stage == "windows-read-error";
            Func<Task> operation = () => transport.RestoreTransactionAsync(_ => Task.CompletedTask, CancellationToken.None);
            trace.Calls.Clear();
            Exception? failure = null;
            try { await operation(); }
            catch (Exception error) { failure = error; }
            Assert.IsNotNull(failure, stage);
            Assert.AreEqual("hardwareAccessPaused", failure.Message, stage);
            Assert.AreEqual("paused", trace.Calls.Last(), "No internal hardware call after pause: " + stage);
            Assert.AreEqual(0, power.Restores);
            trace.Allowed = true;
            trace.PauseOn = null;
            power.FailRead = false;
            var original = power.Snapshot;
            power.Snapshot = original with { DcMaxFrequencyMhz = 4123 };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => transport.RestoreTransactionAsync(CancellationToken.None));
            Assert.AreEqual(4123, power.Snapshot.DcMaxFrequencyMhz, "Interrupted recovery retains its original capture without owning drift.");
            power.Snapshot = original;
            await transport.RestoreTransactionAsync(CancellationToken.None);
            int calls = trace.Calls.Count;
            await transport.RestoreTransactionAsync(CancellationToken.None);
            Assert.AreEqual(calls, trace.Calls.Count, "Only successful recovery releases the snapshot.");
            Assert.AreEqual(0, power.Restores);
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
        public WindowsPowerSettingsSnapshot Snapshot { get; set; } = new(Guid.NewGuid(), 3800, 3000, 2, 0)
        { AcMinActiveCoresPercent = 17, DcMinActiveCoresPercent = 10 };
        public bool FailRead { get; set; }
        public int Restores { get; private set; }
        public Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken token)
        {
            trace.Record("power.read");
            if (FailRead) throw new InvalidOperationException("simulatedReadFailure");
            return Task.FromResult(Snapshot);
        }
        public Task RestoreAsync(CpuTuningField field, WindowsPowerSettingsSnapshot original, CancellationToken token)
        {
            Restores++;
            trace.Record("power.restore");
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
