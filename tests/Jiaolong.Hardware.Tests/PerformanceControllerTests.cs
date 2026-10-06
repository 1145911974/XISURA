using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
[TestCategory("HardwareWrite")]
public sealed class PerformanceControllerTests
{
    [TestMethod]
    public async Task Batch_skips_confirmed_equal_values_but_rechecks_after_native_mode_reset()
    {
        var unchanged = new CpuTuningFixture(CpuTuningField.SplWatts);
        var plan = new CpuTuningPlan(null, null, null, 4500, true, null, null, null);
        var result = await unchanged.Controller.ApplyCpuTuningBatchAsync([plan], true, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(0, unchanged.WriteCount);
        Assert.IsTrue(result.HardwareReadBackConfirmed);

        var reset = new CpuTuningFixture(CpuTuningField.SplWatts);
        result = await reset.Controller.ApplyCpuTuningBatchAsync([plan], true, CancellationToken.None,
            prepareNativeMode: _ => { reset.SimulateNativeCpuReset(); return Task.CompletedTask; },
            restoreNativeMode: _ => Task.CompletedTask,
            verifyNativeMode: _ => Task.FromResult(true));
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(2, reset.WriteCount);
        Assert.AreEqual(new WindowsPowerFrequencyValue(4500, 4500), reset.CurrentValues[CpuTuningField.MaxFrequencyMhz]);
        Assert.AreEqual(true, reset.CurrentValues[CpuTuningField.BoostEnabled]);
    }

    [TestMethod]
    public async Task Each_cpu_field_snapshots_and_rolls_back_without_touching_other_fields()
    {
        foreach (var field in CpuTuningFixture.IndependentlyWritableFields)
        {
            var fixture = new CpuTuningFixture(field);
            var result = await fixture.Controller.ApplyCpuTuningAsync(
                fixture.Plan, isAcConnected: true, CancellationToken.None);

            Assert.AreEqual(CommandState.RolledBack, result.State, field.ToString());
            Assert.AreEqual(fixture.OriginalValues[field], result.FinalValues[field], field.ToString());
            CollectionAssert.AreEqual(
                fixture.OriginalValues.ToArray(), fixture.CurrentValues.ToArray(), field.ToString());
        }
        var batch = new CpuTuningFixture(CpuTuningField.SplWatts);
        var batched = await batch.Controller.ApplyCpuTuningBatchAsync([
            new CpuTuningPlan(null, null, null, 4600, null, null, null, null),
            new CpuTuningPlan(null, 70, null, null, null, null, null, null)
        ], true, CancellationToken.None);
        Assert.AreEqual(CommandState.RolledBack, batched.State);
        Assert.AreEqual("SplWatts", batched.Error!.Details["stage"]);
        Assert.AreEqual("simulatedWriteFailure", batched.Error.Details["cause"]);
        Assert.AreEqual(2, batch.WriteCount);
        CollectionAssert.AreEqual(batch.OriginalValues.ToArray(), batch.CurrentValues.ToArray());
        var nativeBatch = new CpuTuningFixture(CpuTuningField.SplWatts);
        var mode = PerformanceMode.Balanced;
        nativeBatch.OemRestore = () => mode = PerformanceMode.Turbo;
        var nativeResult = await nativeBatch.Controller.ApplyCpuTuningBatchAsync([
            new CpuTuningPlan(null, 60, null, 4000, null, null, null, null)
        ], true, CancellationToken.None,
            prepareNativeMode: _ =>
            {
                nativeBatch.Events.Add("native.prepare");
                mode = PerformanceMode.Turbo;
                return Task.CompletedTask;
            },
            restoreNativeMode: _ =>
            {
                nativeBatch.Events.Add("native.restore");
                mode = PerformanceMode.Balanced;
                nativeBatch.SimulateNativeCpuReset();
                return Task.CompletedTask;
            }, verifyNativeMode: _ => Task.FromResult(mode == PerformanceMode.Turbo));
        Assert.AreEqual(CommandState.RolledBack, nativeResult.State);
        Assert.AreEqual(PerformanceMode.Balanced, mode);
        Assert.IsTrue(nativeBatch.CapturedNativeModeEffects);
        Assert.IsTrue(nativeBatch.Events.IndexOf("capture") < nativeBatch.Events.IndexOf("native.prepare"));
        Assert.IsTrue(nativeBatch.Events.IndexOf("read:MaxFrequencyMhz") < nativeBatch.Events.IndexOf("native.prepare"));
        Assert.IsTrue(nativeBatch.Events.IndexOf("oem.cache.restore") < nativeBatch.Events.IndexOf("native.restore"));
        Assert.IsTrue(nativeBatch.Events.IndexOf("native.restore") < nativeBatch.Events.IndexOf("transaction.restore"));
        Assert.AreEqual("transaction.restore", nativeBatch.Events[^1]);
        CollectionAssert.AreEqual(nativeBatch.OriginalValues.ToArray(), nativeBatch.CurrentValues.ToArray());
        var recoveryFailure = new CpuTuningFixture(CpuTuningField.SplWatts);
        var failedRecovery = await recoveryFailure.Controller.ApplyCpuTuningBatchAsync([recoveryFailure.Plan], true, CancellationToken.None,
            prepareNativeMode: _ => Task.CompletedTask,
            restoreNativeMode: _ => throw new InvalidOperationException("nativeRestoreFailed"),
            verifyNativeMode: _ => Task.FromResult(true));
        Assert.AreEqual(CommandState.RolledBack, failedRecovery.State);
        Assert.AreEqual(ErrorCode.RollbackFailed, failedRecovery.Error!.Code);
        Assert.AreEqual("SplWatts", failedRecovery.Error.Details["stage"]);
        Assert.AreEqual("simulatedWriteFailure", failedRecovery.Error.Details["cause"]);
        Assert.AreEqual("transaction.restore", recoveryFailure.Events[^1]);
        CollectionAssert.AreEqual(recoveryFailure.OriginalValues.ToArray(), recoveryFailure.CurrentValues.ToArray());
        // Even a partial firmware write must recover when no CPU field has been attempted yet.
        var prepareFailure = new CpuTuningFixture(CpuTuningField.SplWatts);
        var restored = false;
        var prepareResult = await prepareFailure.Controller.ApplyCpuTuningBatchAsync([prepareFailure.Plan], true, CancellationToken.None,
            prepareNativeMode: _ => throw new InvalidOperationException("partialNativeModeWrite"),
            restoreNativeMode: _ => { restored = true; return Task.CompletedTask; },
            verifyNativeMode: _ => Task.FromResult(true));
        Assert.AreEqual(CommandState.RolledBack, prepareResult.State);
        Assert.AreEqual(0, prepareFailure.WriteCount);
        Assert.IsTrue(restored);
        // The final mode readback also participates in the transaction, after all CPU writes.
        foreach (var finalModeMatches in new[] { true, false })
        {
            var success = new CpuTuningFixture(CpuTuningField.EnabledCoreCount);
            var recovered = false;
            var applied = await success.Controller.ApplyCpuTuningBatchAsync([
                new CpuTuningPlan(null, null, null, null, false, null, null, null)
            ], true, CancellationToken.None, prepareNativeMode: _ => Task.CompletedTask,
                restoreNativeMode: _ => { recovered = true; return Task.CompletedTask; },
                verifyNativeMode: _ => Task.FromResult(finalModeMatches));
            Assert.AreEqual(finalModeMatches ? CommandState.Applied : CommandState.RolledBack, applied.State);
            Assert.AreEqual(!finalModeMatches, recovered);
            Assert.AreEqual(finalModeMatches ? false : true, success.CurrentValues[CpuTuningField.BoostEnabled]);
        }
    }

    [TestMethod]
    public async Task Cpu_high_power_fields_are_rejected_on_dc_before_transport()
    {
        var transport = new CpuTuningFixture(CpuTuningField.SplWatts);
        var result = await transport.Controller.ApplyCpuTuningAsync(
            transport.Plan, isAcConnected: false, CancellationToken.None);

        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual(0, transport.WriteCount);
        var prepared = false;
        var native = await transport.Controller.ApplyCpuTuningBatchAsync([transport.Plan], false, CancellationToken.None,
            prepareNativeMode: _ => { prepared = true; return Task.CompletedTask; },
            restoreNativeMode: _ => Task.CompletedTask, verifyNativeMode: _ => Task.FromResult(true));
        Assert.AreEqual(CommandState.Rejected, native.State);
        Assert.IsFalse(prepared);
    }

    [TestMethod]
    public async Task Cpu_power_write_is_rejected_before_write_when_hardware_snapshot_is_unavailable()
    {
        var transport = new MissingPowerSnapshotTransport();
        var controller = new PerformanceController(cpuTransport: transport);
        var result = await controller.ApplyCpuTuningAsync(
            new CpuTuningPlan(90, null, null, null, null, null, null, null),
            isAcConnected: true,
            CancellationToken.None);

        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual(ErrorCode.HardwareReadFailed, result.Error?.Code);
        Assert.AreEqual(0, transport.WriteCount);
        var prepared = false;
        var native = await controller.ApplyCpuTuningBatchAsync([
            new CpuTuningPlan(90, null, null, null, null, null, null, null)
        ], true, CancellationToken.None,
            prepareNativeMode: _ => { prepared = true; return Task.CompletedTask; },
            restoreNativeMode: _ => Task.CompletedTask, verifyNativeMode: _ => Task.FromResult(true));
        Assert.AreEqual(CommandState.Rejected, native.State);
        Assert.IsFalse(prepared);
    }

    [TestMethod]
    public async Task Cpu_cancellation_restores_the_snapshot_with_an_uncancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancelOnWriteTransport(cancellation);
        var controller = new PerformanceController(cpuTransport: transport);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            _ = await controller.ApplyCpuTuningAsync(
                new CpuTuningPlan(90, null, null, null, null, null, null, null),
                isAcConnected: true,
                cancellation.Token);
        });

        Assert.AreEqual(95, transport.Value);
        Assert.AreEqual(1, transport.RestoreCount);
    }

    [TestMethod]
    public async Task Cpu_frequency_transaction_keeps_distinct_ac_and_dc_values()
    {
        var fixture = new CpuTuningFixture(CpuTuningField.MaxFrequencyMhz);
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null)
        {
            AcMaxFrequencyMhz = 4_700,
            DcMaxFrequencyMhz = 2_200
        };

        var result = await fixture.Controller.ApplyCpuTuningAsync(
            plan, isAcConnected: true, CancellationToken.None);

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(new WindowsPowerFrequencyValue(4_500, 4_500), result.FinalValues[CpuTuningField.MaxFrequencyMhz]);
    }

    private sealed class CpuTuningFixture
    {
        public static readonly CpuTuningField[] IndependentlyWritableFields =
        [
            CpuTuningField.TemperatureLimitC,
            CpuTuningField.SplWatts,
            CpuTuningField.SpptWatts,
            CpuTuningField.MaxFrequencyMhz,
            CpuTuningField.BoostEnabled,
            CpuTuningField.EnabledCoreCount,
            CpuTuningField.WindowsPowerSchemeId,
            CpuTuningField.NegativeCurveOptimizer
        ];

        private readonly CpuTuningField failOn;
        private readonly Dictionary<CpuTuningField, object?> values = new()
        {
            [CpuTuningField.TemperatureLimitC] = 95,
            [CpuTuningField.SplWatts] = 45,
            [CpuTuningField.SpptWatts] = 70,
            [CpuTuningField.MaxFrequencyMhz] = new WindowsPowerFrequencyValue(4500, 4500),
            [CpuTuningField.BoostEnabled] = true,
            [CpuTuningField.EnabledCoreCount] = 8,
            [CpuTuningField.WindowsPowerSchemeId] = Guid.Empty,
            [CpuTuningField.NegativeCurveOptimizer] = 0
        };

        public CpuTuningFixture(CpuTuningField failOn)
        {
            this.failOn = failOn;
            OriginalValues = new Dictionary<CpuTuningField, object?>(values);
            Transport = new RecordingCpuTransport(this);
            Controller = new PerformanceController(cpuTransport: Transport);
            Plan = failOn switch
            {
                CpuTuningField.TemperatureLimitC => new(90, null, null, null, null, null, null, null),
                CpuTuningField.SplWatts => new(null, 50, null, null, null, null, null, null),
                CpuTuningField.SpptWatts => new(null, null, 80, null, null, null, null, null),
                CpuTuningField.MaxFrequencyMhz => new(null, null, null, 4400, null, null, null, null),
                CpuTuningField.BoostEnabled => new(null, null, null, null, false, null, null, null),
                CpuTuningField.EnabledCoreCount => new(null, null, null, null, null, 6, null, null),
                CpuTuningField.WindowsPowerSchemeId => new(null, null, null, null, null, null, Guid.NewGuid(), null),
                CpuTuningField.NegativeCurveOptimizer => new(null, null, null, null, null, null, null, -5),
                _ => throw new ArgumentOutOfRangeException(nameof(failOn))
            };
        }

        public CpuTuningPlan Plan { get; }
        public Dictionary<CpuTuningField, object?> OriginalValues { get; }
        public Dictionary<CpuTuningField, object?> CurrentValues => new(values);
        private RecordingCpuTransport Transport { get; }
        public PerformanceController Controller { get; }
        public int WriteCount => Transport.WriteCount;
        public List<string> Events { get; } = [];
        public bool CapturedNativeModeEffects { get; private set; }
        public Action? OemRestore { get; set; }
        public void SimulateNativeCpuReset()
        {
            foreach (var field in values.Keys.ToArray()) values[field] = null;
        }

        private sealed class RecordingCpuTransport(CpuTuningFixture fixture) : ICpuTuningTransport
        {
            public int WriteCount { get; private set; }

            public Task CaptureTransactionAsync(IReadOnlyList<CpuTuningPlan> plans, bool captureNativeModeEffects, CancellationToken token)
            {
                fixture.CapturedNativeModeEffects = captureNativeModeEffects;
                fixture.Events.Add("capture");
                return Task.CompletedTask;
            }

            public Task RestoreTransactionAsync(CancellationToken token) => RestoreTransactionAsync(null, token);

            public async Task RestoreTransactionAsync(Func<CancellationToken, Task>? restoreNativeMode, CancellationToken token)
            {
                fixture.Events.Add("oem.cache.restore");
                fixture.OemRestore?.Invoke();
                Exception? modeError = null;
                try { if (restoreNativeMode is not null) await restoreNativeMode(token); }
                catch (Exception error) { modeError = error; }
                fixture.Events.Add("transaction.restore");
                if (fixture.CapturedNativeModeEffects)
                    foreach (var (field, value) in fixture.OriginalValues) fixture.values[field] = value;
                if (modeError is not null) throw modeError;
            }

            public Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken cancellationToken)
            {
                fixture.Events.Add($"read:{field}");
                return Task.FromResult(fixture.values[field]);
            }

            public Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
            {
                WriteCount++;
                fixture.Events.Add($"write:{field}");
                if (field == fixture.failOn) throw new InvalidOperationException("simulatedWriteFailure");
                fixture.values[field] = value;
                return Task.CompletedTask;
            }

            public Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
            {
                fixture.Events.Add($"restore:{field}");
                fixture.values[field] = value;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class MissingPowerSnapshotTransport : ICpuTuningTransport
    {
        public int WriteCount { get; private set; }

        public Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(null);

        public Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CancelOnWriteTransport(CancellationTokenSource cancellation) : ICpuTuningTransport
    {
        public int Value { get; private set; } = 95;
        public int RestoreCount { get; private set; }

        public Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(Value);
        }

        public Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
        {
            Value = (int)value!;
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = (int)value!;
            RestoreCount++;
            return Task.CompletedTask;
        }
    }
}
