using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CpuPowerOrderingTests
{
    [TestMethod]
    public async Task Raising_and_lowering_power_preserve_valid_intermediate_limits()
    {
        foreach (bool raising in new[] { true, false })
        {
            var transport = new PowerPair(raising ? 55 : 65, raising ? 55 : 70);
            var controller = new PerformanceController(cpuTransport: transport);
            var result = await controller.ApplyCpuTuningBatchAsync([
                new CpuTuningPlan(null, raising ? 65 : null, raising ? null : 55, null, null, null, null, null),
                new CpuTuningPlan(null, raising ? null : 55, raising ? 70 : null, null, null, null, null, null)
            ], true, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.AreEqual(raising ? CpuTuningField.SpptWatts : CpuTuningField.SplWatts, transport.Writes[0]);
            Assert.AreEqual(raising ? 65 : 55, transport.Spl);
            Assert.AreEqual(raising ? 70 : 55, transport.Sppt);
        }
    }

    [TestMethod]
    public async Task Native_reset_uses_fresh_ceiling_and_failure_restores_original_pair_safely()
    {
        var transport = new PowerPair(55, 55) { FailBoost = true };
        var result = await new PerformanceController(cpuTransport: transport).ApplyCpuTuningBatchAsync([
            new CpuTuningPlan(null, 60, 65, null, null, null, null, null),
            new CpuTuningPlan(null, null, null, null, false, null, null, null)
        ], true, CancellationToken.None,
            prepareNativeMode: _ => { transport.Spl = 65; transport.Sppt = 75; return Task.CompletedTask; },
            restoreNativeMode: _ => Task.CompletedTask,
            verifyNativeMode: _ => Task.FromResult(true));
        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(Jiaolong.Contracts.Errors.ErrorCode.HardwareWriteFailed, result.Error!.Code);
        Assert.AreEqual(CpuTuningField.SplWatts, transport.Writes[0]);
        Assert.AreEqual(55, transport.Spl);
        Assert.AreEqual(55, transport.Sppt);
    }

    private sealed class PowerPair(int spl, int sppt) : ICpuTuningTransport
    {
        public int Spl { get; set; } = spl;
        public int Sppt { get; set; } = sppt;
        public bool FailBoost { get; init; }
        public List<CpuTuningField> Writes { get; } = [];
        public Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken token) =>
            Task.FromResult<object?>(field == CpuTuningField.SplWatts ? Spl : field == CpuTuningField.SpptWatts ? Sppt : true);
        public Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken token)
        {
            Writes.Add(field);
            if (field == CpuTuningField.BoostEnabled && FailBoost) throw new InvalidOperationException("lateWriteFailure");
            return Set(field, value);
        }
        public Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken token) => Set(field, value);
        private Task Set(CpuTuningField field, object? value)
        {
            int nextSpl = field == CpuTuningField.SplWatts ? (int)value! : Spl;
            int nextSppt = field == CpuTuningField.SpptWatts ? (int)value! : Sppt;
            if (nextSpl > nextSppt) throw new InvalidOperationException("SPL exceeds current SPPT");
            Spl = nextSpl;
            Sppt = nextSppt;
            return Task.CompletedTask;
        }
    }
}
