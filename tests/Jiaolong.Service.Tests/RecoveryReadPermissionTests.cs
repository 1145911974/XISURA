using System.Reflection;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class RecoveryReadPermissionTests
{
    [TestMethod]
    public async Task Recovery_reads_preserve_cpu_capability_but_never_allow_writes_or_stale_contexts()
    {
        using var provider = new WindowsHomeHardwareProvider();
        var lifecycle = new PowerLifecycleState(provider);
        var power = new FakePower();
        var transport = new WindowsCpuTuningTransport(null!, null!, power,
            hardwareAccessAllowed: () => !lifecycle.IsPaused, hardwareReadAllowed: () => lifecycle.IsReadAllowed);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WindowsHomeHardwareProvider).GetField("cpuTuningTransport", flags)!.SetValue(provider, transport);
        var reader = typeof(WindowsHomeHardwareProvider).GetMethod("ReadCpuTuningState", flags)!;
        object? Read() => reader.Invoke(provider, [CancellationToken.None, false]);
        long version = lifecycle.Pause();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> captured;
        using (lifecycle.AllowRecoveryReads(version))
        {
            Assert.IsTrue(lifecycle.IsPaused, "Background work stays paused during recovery diagnostics.");
            Assert.IsNotNull(Read(), "An authorized recovery read must not downgrade CPU capability.");
            Assert.AreEqual(1, power.Reads);
            provider.SetBackgroundWorkPaused(true);
            var manualPause = Assert.ThrowsExactly<TargetInvocationException>(() => Read());
            Assert.AreEqual("hardwareAccessPaused", manualPause.InnerException?.Message);
            Assert.AreEqual(1, power.Reads);
            provider.SetBackgroundWorkPaused(false);
            Exception? writeFailure = null;
            try { await transport.WriteFieldAsync(CpuTuningField.BoostEnabled, false, CancellationToken.None); }
            catch (Exception error) { writeFailure = error; }
            Assert.AreEqual("hardwareAccessPaused", writeFailure?.Message);
            Assert.AreEqual(0, power.Writes);
            captured = Task.Run(async () => { await release.Task; return lifecycle.IsReadAllowed; });
        }
        release.SetResult();
        Assert.IsFalse(await captured, "Scope disposal revokes permission in captured execution contexts.");
        using (lifecycle.AllowRecoveryReads(version))
        {
            long newer = lifecycle.Pause();
            Assert.IsFalse(lifecycle.IsReadAllowed);
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Read());
            Assert.AreEqual("hardwareAccessPaused", error.InnerException?.Message);
            Assert.AreEqual(1, power.Reads, "A new suspend blocks the next diagnostic read.");
            Assert.IsFalse(lifecycle.Resume(version));
            Assert.IsTrue(lifecycle.Resume(newer));
        }
        Assert.IsNotNull(Read());
        Assert.AreEqual(2, power.Reads);
        long readingVersion = lifecycle.Pause();
        using (lifecycle.AllowRecoveryReads(readingVersion))
        {
            power.AfterRead = () => lifecycle.Pause();
            var interrupted = Assert.ThrowsExactly<TargetInvocationException>(() => Read());
            Assert.AreEqual("hardwareAccessPaused", interrupted.InnerException?.Message);
            Assert.AreEqual(3, power.Reads, "Interrupted reads must propagate instead of returning partial CPU state.");
        }
    }

    private sealed class FakePower : IWindowsPowerSettingsTransport
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Action? AfterRead { get; set; }
        public Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken token)
        { Reads++; AfterRead?.Invoke(); return Task.FromResult(new WindowsPowerSettingsSnapshot(Guid.NewGuid(), 3800, 3000, 2, 0)); }
        public Task SetBoostAsync(bool value, CancellationToken token) { Writes++; return Task.CompletedTask; }
        public Task SetSchemeAsync(Guid value, CancellationToken token) => throw new AssertFailedException();
        public Task SetMaxFrequencyAsync(int ac, int dc, CancellationToken token) => throw new AssertFailedException();
        public Task SetCoreParkingAsync(int ac, int dc, CancellationToken token) => throw new AssertFailedException();
        public Task RestoreAsync(CpuTuningField field, WindowsPowerSettingsSnapshot original, CancellationToken token) => throw new AssertFailedException();
    }
}
