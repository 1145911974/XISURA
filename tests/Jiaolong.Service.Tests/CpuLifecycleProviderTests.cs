using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CpuLifecycleProviderTests
{
    [TestMethod]
    public void Paused_cpu_transaction_returns_recovery_without_post_transaction_telemetry()
    {
        using var provider = new WindowsHomeHardwareProvider();
        using var cancellation = new CancellationTokenSource();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(WindowsHomeHardwareProvider);
        // This placeholder is never called; all transaction I/O uses the fake below.
        type.GetField("cpuTuningTransport", flags)!.SetValue(provider, new WindowsCpuTuningTransport(null!, null!, null!));
        var fake = new PausingCpuTransport(() =>
        {
            provider.SetBackgroundWorkPaused(true);
            cancellation.Cancel(); // Any extra telemetry read fails before accessing a real device.
        });
        type.GetField("performanceController", flags)!.SetValue(provider,
            new PerformanceController(cpuTransport: fake, hardwareAccessAllowed: () => !cancellation.IsCancellationRequested));
        var command = new SetCpuTuningCommand(Guid.NewGuid(), new(null, null, null, null, false, null, null, null), true);
        var result = (CommandResult)type.GetMethod("ExecuteCpuTuningLocked", flags)!.Invoke(provider, [command, cancellation.Token])!;
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
        Assert.IsNull(result.VerifiedState);
        Assert.AreEqual(1, fake.Writes);
        Assert.AreEqual(1, fake.Reads);
    }

    private sealed class PausingCpuTransport(Action pause) : ICpuTuningTransport
    {
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken token)
        { Reads++; return Task.FromResult<object?>(true); }
        public Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken token)
        { Writes++; pause(); return Task.CompletedTask; }
        public Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken token) =>
            throw new AssertFailedException("Paused recovery must not access hardware.");
    }
}
