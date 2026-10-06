using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public interface IPerformanceController
{
    Task<PerformanceMode> ReadModeAsync(CancellationToken cancellationToken);
    Task WriteModeAsync(PerformanceMode mode, CancellationToken cancellationToken);
}

public interface IPerformanceTransport
{
    Task<PerformanceMode> ReadModeAsync(CancellationToken cancellationToken);
    Task WriteModeAsync(PerformanceMode mode, CancellationToken cancellationToken);
}

public enum CpuTuningField
{
    TemperatureLimitC,
    SplWatts,
    SpptWatts,
    MaxFrequencyMhz,
    CoreParkingPercent,
    BoostEnabled,
    EnabledCoreCount,
    WindowsPowerSchemeId,
    NegativeCurveOptimizer,
    StapmWatts,
    FastPptWatts,
    SlowPptWatts,
    PptWatts,
    VrmCurrentMilliamps,
    TdcCurrentMilliamps,
    EdcCurrentMilliamps,
    Mp1TemperatureC,
    RsmuTemperatureC,
    PboScalar,
    OverclockEnabled,
    OcClockMhz,
    OcVoltageMillivolts,
    PerCoreOcClockMhz,
    CurveOptimizerAll,
    PerCoreCurveOptimizer
}

public interface ICpuTuningTransport
{
    Task CaptureTransactionAsync(IReadOnlyList<CpuTuningPlan> plans, CancellationToken cancellationToken) => Task.CompletedTask;
    Task CaptureTransactionAsync(IReadOnlyList<CpuTuningPlan> plans, bool captureNativeModeEffects,
        CancellationToken cancellationToken) => CaptureTransactionAsync(plans, cancellationToken);
    Task RestoreTransactionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    async Task RestoreTransactionAsync(Func<CancellationToken, Task>? restoreNativeMode, CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        try { if (restoreNativeMode is not null) await restoreNativeMode(cancellationToken); }
        catch (Exception error) { failures.Add(error); }
        try { await RestoreTransactionAsync(cancellationToken); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken cancellationToken);
    Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken);
    Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken);
    Task WriteConfirmedOemLimitAsync(CpuTuningField field, int value, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("oemCpuLimitWriteUnavailable"));
    bool IsReadBackRequired(CpuTuningField field) => true;
    bool IsReadBackMatch(CpuTuningField field, object? requested, object? readBack) => Equals(requested, readBack);
}

public sealed record CpuTuningTransactionResult(
    Guid OperationId,
    CommandState State,
    IReadOnlyDictionary<CpuTuningField, object?> FinalValues,
    ServiceError? Error,
    bool HardwareReadBackConfirmed = true);

public sealed class PerformanceController(
    IPerformanceTransport? transport = null,
    ICpuTuningTransport? cpuTransport = null) : IPerformanceController
{
    public async Task<CpuTuningTransactionResult> ApplyCpuOemLimitAsync(
        Guid operationId,
        CpuTuningField field,
        int value,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        if (cpuTransport is null) return Rejected(operationId, ErrorCode.CapabilityUnavailable);
        int minimum = field == CpuTuningField.TemperatureLimitC ? 40 : 45;
        int maximum = field switch
        {
            CpuTuningField.TemperatureLimitC => 100,
            CpuTuningField.SplWatts or CpuTuningField.SpptWatts => 75,
            _ => 0
        };
        if (maximum == 0)
            return Rejected(operationId, ErrorCode.CapabilityUnavailable);
        if (value < minimum || value > maximum)
            return Rejected(operationId, ErrorCode.ValidationFailed);

        try
        {
            var empty = new CpuTuningPlan(null, null, null, null, null, null, null, null);
            var plan = field switch
            {
                CpuTuningField.TemperatureLimitC => empty with { TemperatureLimitC = value },
                CpuTuningField.SplWatts => empty with { SplWatts = value },
                _ => empty with { SpptWatts = value }
            };
            var result = await ApplyCpuTuningAsync(plan, true, cancellationToken);
            return result with { OperationId = operationId,
                Error = result.Error is null ? null : result.Error with { CorrelationId = operationId } };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new CpuTuningTransactionResult(
                operationId, CommandState.RecoveryRequired, new Dictionary<CpuTuningField, object?>(),
                ServiceError.Create(ErrorCode.HardwareWriteFailed, operationId, false),
                HardwareReadBackConfirmed: false);
        }
    }

    public Task<PerformanceMode> ReadModeAsync(CancellationToken cancellationToken) =>
        transport?.ReadModeAsync(cancellationToken)
        ?? Task.FromException<PerformanceMode>(new InvalidOperationException("performanceModeUnavailable"));

    public async Task WriteModeAsync(PerformanceMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (transport is null) throw new InvalidOperationException("performanceModeUnavailable");
        _ = await transport.ReadModeAsync(cancellationToken);
        await transport.WriteModeAsync(mode, cancellationToken);
        var readBack = await transport.ReadModeAsync(cancellationToken);
        if (readBack != mode) throw new InvalidOperationException("performanceModeReadbackMismatch");
    }

    public Task<CpuTuningTransactionResult> ApplyCpuTuningAsync(
        CpuTuningPlan plan,
        bool isAcConnected,
        CancellationToken cancellationToken) => ApplyCpuTuningBatchAsync([plan], isAcConnected, cancellationToken);

    public async Task<CpuTuningTransactionResult> ApplyCpuTuningBatchAsync(
        IReadOnlyList<CpuTuningPlan> plans,
        bool isAcConnected,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? prepareNativeMode = null,
        Func<CancellationToken, Task>? restoreNativeMode = null,
        Func<CancellationToken, Task<bool>>? verifyNativeMode = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var operationId = Guid.NewGuid();
        if (cpuTransport is null)
        {
            return Rejected(operationId, ErrorCode.CapabilityUnavailable);
        }

        var validation = CommandValidation.Validate(new SetCpuTuningBatchCommand(operationId, plans.ToArray(), true));
        if (validation is not null)
        {
            return Rejected(operationId, validation.Code);
        }
        if ((prepareNativeMode is null) != (restoreNativeMode is null) ||
            (prepareNativeMode is not null && verifyNativeMode is null))
            return Rejected(operationId, ErrorCode.ValidationFailed);

        if (!isAcConnected && (prepareNativeMode is not null || plans.Any(HasHighPowerField)))
        {
            return Rejected(operationId, ErrorCode.ValidationFailed);
        }

        var requested = plans.SelectMany(Fields).ToArray();
        if (requested.Length == 0)
        {
            return Rejected(operationId, ErrorCode.ValidationFailed);
        }

        var snapshots = new Dictionary<CpuTuningField, object?>();
        var attempted = new List<CpuTuningField>();
        var nativeModeAttempted = false;
        var hardwareReadBackConfirmed = true;
        var failureStage = "Capture";
        ServiceError Failure(ErrorCode code, Exception error) => ServiceError.Create(code, operationId, false) with
        { Details = new() { ["stage"] = failureStage, ["cause"] = error.Message } };
        try
        {
            await cpuTransport.CaptureTransactionAsync(plans, prepareNativeMode is not null, cancellationToken);
            foreach (var (field, value) in requested)
            {
                snapshots[field] = await cpuTransport.ReadFieldAsync(field, cancellationToken);
                if (cpuTransport.IsReadBackRequired(field) && snapshots[field] is null)
                {
                    return new CpuTuningTransactionResult(
                        operationId,
                        CommandState.Rejected,
                        new Dictionary<CpuTuningField, object?>(snapshots),
                        ServiceError.Create(ErrorCode.HardwareReadFailed, operationId, false),
                        HardwareReadBackConfirmed: false);
                }
            }

            if (prepareNativeMode is not null)
            {
                failureStage = "NativeMode";
                nativeModeAttempted = true;
                await prepareNativeMode(cancellationToken);
            }
            // Mode selection can reset the burst ceiling; order against its fresh hardware value.
            await OrderPowerFieldsAsync(requested, cpuTransport, cancellationToken);
            foreach (var (field, value) in requested)
            {
                failureStage = field.ToString();
                // Firmware mode selection may reset values: compare fresh readback, not the rollback snapshot.
                if (cpuTransport.IsReadBackRequired(field) &&
                    cpuTransport.IsReadBackMatch(field, value,
                        prepareNativeMode is not null || attempted.Count > 0
                            ? await cpuTransport.ReadFieldAsync(field, cancellationToken) : snapshots[field]))
                    continue;
                attempted.Add(field);
                await cpuTransport.WriteFieldAsync(field, value, cancellationToken);
                if (!cpuTransport.IsReadBackRequired(field))
                {
                    hardwareReadBackConfirmed = false;
                    continue;
                }

                var readBack = await cpuTransport.ReadFieldAsync(field, cancellationToken);
                if (!cpuTransport.IsReadBackMatch(field, value, readBack))
                {
                    throw new CpuTuningReadBackException($"expected {value}; got {readBack}");
                }
            }

            failureStage = "NativeModeVerify";
            if (verifyNativeMode is not null && !await verifyNativeMode(cancellationToken))
                throw new CpuTuningReadBackException("nativeModeMismatch");
            var finalValues = requested.ToDictionary(
                item => item.Field,
                item => item.Value);
            return new CpuTuningTransactionResult(
                operationId, CommandState.Applied, finalValues, null, hardwareReadBackConfirmed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (nativeModeAttempted || attempted.Count > 0)
                await RestoreAsync(attempted, snapshots, CancellationToken.None, nativeModeAttempted ? restoreNativeMode : null);
            throw;
        }
        catch (CpuTuningReadBackException error)
        {
            var rollbackFailed = !await RestoreAsync(attempted, snapshots, CancellationToken.None,
                nativeModeAttempted ? restoreNativeMode : null);
            return new CpuTuningTransactionResult(
                operationId,
                CommandState.RolledBack,
                new Dictionary<CpuTuningField, object?>(snapshots),
                Failure(
                    rollbackFailed ? ErrorCode.RollbackFailed : ErrorCode.ReadBackMismatch,
                    error));
        }
        catch (Exception error)
        {
            if (attempted.Count == 0 && !nativeModeAttempted)
                return new CpuTuningTransactionResult(operationId, CommandState.Rejected, snapshots,
                    Failure(ErrorCode.HardwareReadFailed, error), false);
            var rollbackFailed = !await RestoreAsync(attempted, snapshots, CancellationToken.None,
                nativeModeAttempted ? restoreNativeMode : null);
            return new CpuTuningTransactionResult(
                operationId,
                CommandState.RolledBack,
                new Dictionary<CpuTuningField, object?>(snapshots),
                Failure(
                    rollbackFailed ? ErrorCode.RollbackFailed : ErrorCode.HardwareWriteFailed,
                    error));
        }
    }

    private async Task<bool> RestoreAsync(
        IEnumerable<CpuTuningField> fields,
        IReadOnlyDictionary<CpuTuningField, object?> snapshots,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? restoreNativeMode = null)
    {
        var succeeded = true;
        var restoring = fields.Reverse().Select(field => (Field: field, Value: snapshots[field])).ToArray();
        try { await OrderPowerFieldsAsync(restoring, cpuTransport!, cancellationToken); }
        catch { succeeded = false; }
        foreach (var (field, _) in restoring)
        {
            try
            {
                await cpuTransport!.RestoreFieldAsync(field, snapshots[field], cancellationToken);
            }
            catch
            {
                succeeded = false;
            }
        }

        try { await cpuTransport!.RestoreTransactionAsync(restoreNativeMode, cancellationToken); }
        catch { succeeded = false; }

        return succeeded;
    }

    internal static async Task OrderPowerFieldsAsync(
        (CpuTuningField Field, object? Value)[] fields, ICpuTuningTransport transport, CancellationToken token)
    {
        int spl = Array.FindIndex(fields, item => item.Field == CpuTuningField.SplWatts);
        int sppt = Array.FindIndex(fields, item => item.Field == CpuTuningField.SpptWatts);
        if (spl < 0 || sppt < 0 || fields[spl].Value is not int targetSpl) return;
        if (await transport.ReadFieldAsync(CpuTuningField.SpptWatts, token) is not int currentSppt)
            throw new InvalidOperationException("cpuBurstCeilingReadFailed");
        // Raising SPL above the current burst ceiling is clamped by firmware. Lower SPL first
        // when reducing the pair; reverse rollback alone is insufficient after a mode reset.
        bool burstFirst = targetSpl > currentSppt;
        if (burstFirst ? spl < sppt : sppt < spl)
            (fields[spl], fields[sppt]) = (fields[sppt], fields[spl]);
    }

    private static bool HasHighPowerField(CpuTuningPlan plan) =>
        plan.SplWatts is not null
        || plan.SpptWatts is not null
        || plan.MaxFrequencyMhz is not null
        || plan.AcMaxFrequencyMhz is not null
        || plan.DcMaxFrequencyMhz is not null
        || plan.BoostEnabled is true
        || plan.WindowsPowerSchemeId is not null
        || plan.Advanced is not null;

    private static IEnumerable<(CpuTuningField Field, object? Value)> Fields(CpuTuningPlan plan)
    {
        if (plan.TemperatureLimitC is { } temperature) yield return (CpuTuningField.TemperatureLimitC, temperature);
        if (plan.WindowsPowerSchemeId is { } scheme) yield return (CpuTuningField.WindowsPowerSchemeId, scheme);
        if (plan.SplWatts is { } spl) yield return (CpuTuningField.SplWatts, spl);
        if (plan.SpptWatts is { } sppt) yield return (CpuTuningField.SpptWatts, sppt);
        var ac = plan.AcMaxFrequencyMhz;
        var dc = plan.DcMaxFrequencyMhz;
        if (ac is not null || dc is not null)
        {
            var legacy = plan.MaxFrequencyMhz ?? ac ?? dc ?? throw new InvalidOperationException("frequencyValueMissing");
            yield return (CpuTuningField.MaxFrequencyMhz,
                new WindowsPowerFrequencyValue(ac ?? legacy, dc ?? legacy));
        }
        else if (plan.MaxFrequencyMhz is { } frequency)
        {
            yield return (CpuTuningField.MaxFrequencyMhz, new WindowsPowerFrequencyValue(frequency, frequency));
        }
        if (plan.BoostEnabled is { } boost) yield return (CpuTuningField.BoostEnabled, boost);
        if (plan.AcMinActiveCoresPercent is int acParking && plan.DcMinActiveCoresPercent is int dcParking)
            yield return (CpuTuningField.CoreParkingPercent, new WindowsCoreParkingValue(acParking, dcParking));
        if (plan.EnabledCoreCount is { } cores) yield return (CpuTuningField.EnabledCoreCount, cores);
        if (plan.NegativeCurveOptimizer is { } curve) yield return (CpuTuningField.NegativeCurveOptimizer, curve);
        if (plan.Advanced is { } advanced)
        {
            if (advanced.StapmWatts is { } stapm) yield return (CpuTuningField.StapmWatts, stapm);
            if (advanced.FastPptWatts is { } fastPpt) yield return (CpuTuningField.FastPptWatts, fastPpt);
            if (advanced.SlowPptWatts is { } slowPpt) yield return (CpuTuningField.SlowPptWatts, slowPpt);
            if (advanced.PptWatts is { } ppt) yield return (CpuTuningField.PptWatts, ppt);
            if (advanced.VrmCurrentMilliamps is { } vrm) yield return (CpuTuningField.VrmCurrentMilliamps, vrm);
            if (advanced.TdcCurrentMilliamps is { } tdc) yield return (CpuTuningField.TdcCurrentMilliamps, tdc);
            if (advanced.EdcCurrentMilliamps is { } edc) yield return (CpuTuningField.EdcCurrentMilliamps, edc);
            if (advanced.Mp1TemperatureC is { } mp1) yield return (CpuTuningField.Mp1TemperatureC, mp1);
            if (advanced.RsmuTemperatureC is { } rsmu) yield return (CpuTuningField.RsmuTemperatureC, rsmu);
            if (advanced.PboScalar is { } scalar) yield return (CpuTuningField.PboScalar, scalar);
            if (advanced.CurveOptimizerAll is { } curveAll) yield return (CpuTuningField.CurveOptimizerAll, curveAll);
            if (advanced.PerCoreCurveOptimizer is { Count: > 0 } perCoreCurve) yield return (CpuTuningField.PerCoreCurveOptimizer, perCoreCurve);
        }
    }

    private static CpuTuningTransactionResult Rejected(Guid operationId, ErrorCode code) =>
        new(operationId, CommandState.Rejected,
            new Dictionary<CpuTuningField, object?>(),
            ServiceError.Create(code, operationId, false));

    private sealed class CpuTuningReadBackException : Exception
    {
        public CpuTuningReadBackException(string message) : base(message) { }
    }
}
