using System.Reflection;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class GpuClockProbeRetryTests
{
    [TestMethod]
    public void Failed_probe_retries_after_cooldown_and_success_stays_cached()
    {
        var clock = new ManualClock();
        var attempts = 0;
        using var provider = new WindowsHomeHardwareProvider(clock, _ => ++attempts <= 2
            ? Task.FromException<NvidiaClockDevice>(new IOException("temporaryProbeFailure"))
            : Task.FromResult(new NvidiaClockDevice("GPU-test", 300, 2400)));

        var failed = Read(provider);
        Assert.AreEqual("gpuClockRangeUnavailable", failed.Error);
        clock.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.AreSame(failed, Read(provider));
        Assert.AreEqual(1, attempts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual("gpuClockRangeUnavailable", Read(provider).Error);
        Assert.AreEqual(2, attempts);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.AreEqual("gpuClockRangeUnavailable", Read(provider).Error);
        Assert.AreEqual(2, attempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = Read(provider);
        Assert.IsNull(recovered.Error);
        Assert.AreEqual(300, recovered.MinimumMhz);
        Assert.AreEqual(2400, recovered.MaximumMhz);
        Assert.IsFalse(recovered.HasSubmission);
        Assert.IsNull(recovered.SubmittedMhz);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreSame(recovered, Read(provider));
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public void Cancelled_probe_does_not_cache_a_failure_or_delay_recovery()
    {
        var clock = new ManualClock();
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        using var provider = new WindowsHomeHardwareProvider(clock, token =>
        {
            attempts++;
            if (attempts == 1) return Task.FromException<NvidiaClockDevice>(new IOException("temporaryProbeFailure"));
            if (attempts == 2)
            {
                cancellation.Cancel();
                return Task.FromCanceled<NvidiaClockDevice>(token);
            }
            return Task.FromResult(new NvidiaClockDevice("GPU-test", 300, 2400));
        });

        Assert.AreEqual("gpuClockRangeUnavailable", Read(provider).Error);
        clock.Advance(TimeSpan.FromSeconds(10));
        var cancelled = Assert.Throws<TargetInvocationException>(() => Read(provider, cancellation.Token));
        Assert.IsInstanceOfType<OperationCanceledException>(cancelled.InnerException);
        Assert.IsNull(Read(provider).Error);
        Assert.AreEqual(3, attempts);
    }

    private static GpuClockLimitState Read(WindowsHomeHardwareProvider provider, CancellationToken token = default) =>
        (GpuClockLimitState)typeof(WindowsHomeHardwareProvider)
            .GetMethod("ReadGpuClockLimitLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider, [token])!;

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }
}
