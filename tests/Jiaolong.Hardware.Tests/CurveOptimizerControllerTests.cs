using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;

namespace Jiaolong.Hardware.Tests;

[TestClass]
[TestCategory("HardwareWrite")]
public sealed class CurveOptimizerControllerTests
{
    [TestMethod]
    public void Curve_optimizer_is_never_positive_or_below_negative_thirty()
    {
        foreach (var (value, valid) in new[] { (5, false), (0, true), (-5, true), (-30, true), (-31, false) })
        {
            Assert.AreEqual(valid,
                CurveOptimizerController.Validate(value, minimum: -30, maximum: 0).IsValid,
                value.ToString());
        }
    }

    [TestMethod]
    public async Task Positive_curve_optimizer_never_reaches_transport()
    {
        var transport = new RecordingCurveTransport();
        var result = await new CurveOptimizerController(transport).ApplyAsync(5, CancellationToken.None);

        Assert.IsFalse(result.Applied);
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public async Task All_core_curve_requires_hardware_readback_and_zero_is_an_explicit_write()
    {
        var hardware = new RecordingCurveTransport();
        var transport = new WindowsCpuTuningTransport(null!, null!, null!, hardware);
        var controller = new PerformanceController(cpuTransport: transport);
        foreach (int target in new[] { -5, 0 })
        {
            var result = await controller.ApplyCpuTuningAsync(
                new CpuTuningPlan(null, null, null, null, null, null, null, target), true, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.IsTrue(result.HardwareReadBackConfirmed);
            Assert.AreEqual(target, hardware.CurrentValue);
        }
        Assert.AreEqual(2, hardware.WriteCount);
        Assert.AreEqual(4, hardware.ReadCount);
    }

    [TestMethod]
    public async Task Failed_first_all_core_write_restores_the_real_original_value_without_writing_zero()
    {
        var hardware = new RecordingCurveTransport { FailWrites = true };
        var transport = new WindowsCpuTuningTransport(null!, null!, null!, hardware);
        var result = await new PerformanceController(cpuTransport: transport).ApplyCpuTuningAsync(
            new CpuTuningPlan(null, null, null, null, null, null, null, -5), true, CancellationToken.None);
        Assert.AreEqual(ErrorCode.HardwareWriteFailed, result.Error?.Code);
        Assert.AreEqual(2, hardware.WriteCount);
        Assert.AreEqual(-30, hardware.CurrentValue);
        Assert.IsTrue(hardware.ReadCount >= 1);
    }

    private sealed class RecordingCurveTransport : ICurveOptimizerTransport
    {
        public int WriteCount { get; private set; }
        public int ReadCount { get; private set; }
        public bool FailWrites { get; init; }
        public int CurrentValue { get; private set; } = -30;
        public Task<int> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(CurrentValue);
        }
        public Task WriteAsync(int value, CancellationToken cancellationToken)
        {
            WriteCount++;
            if (FailWrites && WriteCount == 1) throw new InvalidOperationException("smuRejected");
            CurrentValue = value;
            return Task.CompletedTask;
        }
        public Task RestoreAsync(int value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
