using System.Management;
using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public sealed record WindowsPowerFrequencyValue(int Ac, int Dc);

public sealed record WindowsPowerBoostValue(int AcMode, int DcMode)
{
    public bool IsEnabled => WindowsPowerBoostMode.IsEnabled(AcMode) && WindowsPowerBoostMode.IsEnabled(DcMode);
}

public interface IPboScalarTransport
{
    int Read(CancellationToken cancellationToken);
    void Write(int scalar, CancellationToken cancellationToken);
}

public sealed class WindowsCpuTuningTransport(
    MiCommonInterfaceClient miClient,
    VerifiedWmiBinding cpuPowerBinding,
    IWindowsPowerSettingsTransport powerSettings,
    ICurveOptimizerTransport? curveOptimizer = null,
    IPboScalarTransport? pboScalar = null,
    ISmuCpuLimitTransport? smuLimits = null,
    Func<bool>? hardwareAccessAllowed = null,
    Func<bool>? hardwareReadAllowed = null) : ICpuTuningTransport
{
    private void EnsureHardwareAccess(bool readOnly = false)
    {
        var allowed = readOnly ? hardwareReadAllowed ?? hardwareAccessAllowed : hardwareAccessAllowed;
        if (allowed?.Invoke() == false) throw new HardwareLifecycleInterruptedException();
    }

    private async Task AccessHardwareAsync(Func<Task> call)
    {
        EnsureHardwareAccess();
        try { await call(); }
        finally { EnsureHardwareAccess(); }
    }

    private async Task<T> AccessHardwareAsync<T>(Func<Task<T>> call, bool readOnly = false)
    {
        EnsureHardwareAccess(readOnly);
        try { return await call(); }
        finally { EnsureHardwareAccess(readOnly); }
    }

    private void AccessHardware(Action call)
    {
        EnsureHardwareAccess();
        try { call(); }
        finally { EnsureHardwareAccess(); }
    }

    private T AccessHardware<T>(Func<T> call, bool readOnly = false)
    {
        EnsureHardwareAccess(readOnly);
        try { return call(); }
        finally { EnsureHardwareAccess(readOnly); }
    }

    private readonly Dictionary<CpuTuningField, WindowsPowerSettingsSnapshot> powerSnapshots = [];
    private CpuSmuLimitSnapshot? originalSmuLimits;
    private bool? originalCustomMode;
    private bool oemAttempted;
    private WindowsPowerSettingsSnapshot? originalModePowerSettings;
    private InvalidOperationException? unsupportedOemBaseline;
    public string? ActivePowerSchemeName { get; private set; }
    public bool? OemCustomPowerMode { get; private set; }

    public Task CaptureTransactionAsync(IReadOnlyList<CpuTuningPlan> plans, CancellationToken token) =>
        CaptureTransactionAsync(plans, false, token);

    public async Task CaptureTransactionAsync(IReadOnlyList<CpuTuningPlan> plans, bool captureNativeModeEffects, CancellationToken token)
    {
        EnsureHardwareAccess(readOnly: true);
        powerSnapshots.Clear();
        originalSmuLimits = null;
        originalCustomMode = null;
        oemAttempted = false;
        originalModePowerSettings = null;
        if (captureNativeModeEffects)
        {
            var power = await AccessHardwareAsync(() => powerSettings.ReadAsync(token), readOnly: true);
            if (power.AcMinActiveCoresPercent is null || power.DcMinActiveCoresPercent is null)
                throw new InvalidOperationException("nativeModeWindowsOriginalUnavailable");
            originalModePowerSettings = power;
            foreach (var field in WindowsModeFields) powerSnapshots[field] = power;
        }
        if (!plans.Any(plan => plan.TemperatureLimitC.HasValue || plan.SplWatts.HasValue || plan.SpptWatts.HasValue)) return;
        originalSmuLimits = ReadSmuSnapshot(token);
        var mode = await AccessHardwareAsync(() => miClient.ReadAsync(cpuPowerBinding, token), readOnly: true);
        originalCustomMode = mode.Quality == DataQuality.Good ? MiCpuPowerCodec.ReadCustomMode(mode.Payload ?? []) : null;
        if (originalCustomMode is null) throw new InvalidOperationException("cpuPowerCustomModeReadFailed");
    }

    private CpuSmuLimitSnapshot ReadSmuSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (unsupportedOemBaseline is not null) throw unsupportedOemBaseline;
        try
        {
            if (smuLimits is null) throw new InvalidOperationException("oemCpuLimitOriginalUnavailable");
            return AccessHardware(() => smuLimits.ReadLimitSnapshot(token), readOnly: true);
        }
        catch (InvalidOperationException error) when (error.Message == "advancedCpuPmTableUnsupported")
        {
            // The transport is recreated when hardware is reinitialized; transient errors are not cached.
            unsupportedOemBaseline = error;
            throw;
        }
    }

    public Task RestoreTransactionAsync(CancellationToken token) => RestoreTransactionAsync(null, token);

    public async Task RestoreTransactionAsync(Func<CancellationToken, Task>? restoreNativeMode, CancellationToken token)
    {
        EnsureHardwareAccess();
        if (!oemAttempted && originalModePowerSettings is null && restoreNativeMode is null) return;
        var failures = new List<Exception>();
        try
        {
            if (originalSmuLimits is not null || oemAttempted)
                await RestoreOemTransactionAsync(restoreNativeMode, token);
            else if (restoreNativeMode is not null)
                await AccessHardwareAsync(() => restoreNativeMode(token));
        }
        catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        if (originalModePowerSettings is { } power)
        {
            foreach (var field in WindowsModeFields)
            {
                try { await AccessHardwareAsync(() => powerSettings.RestoreAsync(field, power, token)); }
                catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
            }
            try
            {
                if (((await AccessHardwareAsync(() => powerSettings.ReadAsync(token))) with { ActiveSchemeName = null }) !=
                    (power with { ActiveSchemeName = null }))
                    throw new InvalidOperationException("nativeModeWindowsRestoreMismatch");
            }
            catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("cpuNativeModeTransactionRestoreFailed", new AggregateException(failures));
        originalModePowerSettings = null;
        oemAttempted = false;
    }

    private static readonly CpuTuningField[] WindowsModeFields =
        [CpuTuningField.WindowsPowerSchemeId, CpuTuningField.MaxFrequencyMhz,
         CpuTuningField.BoostEnabled, CpuTuningField.CoreParkingPercent];

    private async Task RestoreOemTransactionAsync(Func<CancellationToken, Task>? restoreNativeMode, CancellationToken token)
    {
        EnsureHardwareAccess();
        if (originalSmuLimits is null || originalCustomMode is null || smuLimits is null)
            throw new InvalidOperationException("oemCpuLimitOriginalUnavailable");
        var failures = new List<Exception>();
        // Recover the gate before the native mode; re-enabling CMEN can select Turbo.
        try
        {
            await RestoreOemPowerFieldsAsync(token);
        }
        catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        try
        {
            var currentMode = await AccessHardwareAsync(() => miClient.ReadAsync(cpuPowerBinding, token));
            if (currentMode.Quality != DataQuality.Good) throw new InvalidOperationException("cpuPowerCustomModeReadFailed");
            if (MiCpuPowerCodec.ReadCustomMode(currentMode.Payload ?? []) != originalCustomMode)
            {
                var modeWrite = await AccessHardwareAsync(() => miClient.WriteOnceAsync(cpuPowerBinding, MiCpuPowerCodec.CustomMode(originalCustomMode.Value), token));
                if (modeWrite.Quality != DataQuality.Good) throw new InvalidOperationException("cpuPowerCustomModeRestoreFailed");
            }
        }
        catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        try { if (restoreNativeMode is not null) await AccessHardwareAsync(() => restoreNativeMode(token)); }
        catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        try
        {
            // Stock mode selection resets the OEM SPL slot, which has no SMU-only setter.
            if (originalCustomMode.Value)
            {
                await RestoreOemPowerFieldsAsync(token);
            }
            foreach (var field in new[] { CpuTuningField.StapmWatts, CpuTuningField.FastPptWatts, CpuTuningField.SlowPptWatts,
                CpuTuningField.VrmCurrentMilliamps, CpuTuningField.EdcCurrentMilliamps, CpuTuningField.Mp1TemperatureC })
                AccessHardware(() => smuLimits.WriteAdvancedLimit(RyzenSmuAdvancedLimits.BuildField(field,
                    RyzenSmuAdvancedLimits.ReadField(originalSmuLimits.Limits, field), restoring: true), token));
            var mode = await AccessHardwareAsync(() => miClient.ReadAsync(cpuPowerBinding, token));
            if (mode.Quality != DataQuality.Good || MiCpuPowerCodec.ReadCustomMode(mode.Payload ?? []) != originalCustomMode ||
                AccessHardware(() => smuLimits.ReadLimitSnapshot(token)) != originalSmuLimits)
                throw new InvalidOperationException("oemCpuLimitRestoreMismatch");
        }
        catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private async Task RestoreOemPowerFieldsAsync(CancellationToken token)
    {
        (CpuTuningField Field, object? Value)[] fields = [
            (CpuTuningField.SplWatts, originalSmuLimits!.OemSplWatts),
            (CpuTuningField.SpptWatts, checked((int)originalSmuLimits.Limits.FastPptWatts!.Value)),
            (CpuTuningField.TemperatureLimitC, originalSmuLimits.Limits.Mp1TemperatureC!.Value)];
        await PerformanceController.OrderPowerFieldsAsync(fields, this, token);
        foreach (var (field, value) in fields)
            await WriteMiPowerFieldAsync(field, (int)value!, token);
    }

    public async Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken cancellationToken)
    {
        EnsureHardwareAccess(readOnly: true);
        cancellationToken.ThrowIfCancellationRequested();
        return field switch
        {
            CpuTuningField.TemperatureLimitC or CpuTuningField.SplWatts or CpuTuningField.SpptWatts =>
                await ReadMiPowerFieldAsync(field, cancellationToken),
            CpuTuningField.MaxFrequencyMhz or CpuTuningField.CoreParkingPercent or
                CpuTuningField.BoostEnabled or CpuTuningField.WindowsPowerSchemeId =>
                await ReadWindowsPowerFieldAsync(field, cancellationToken),
            CpuTuningField.EnabledCoreCount => AccessHardware(ReadPhysicalCoreCount, readOnly: true),
            CpuTuningField.NegativeCurveOptimizer or CpuTuningField.CurveOptimizerAll =>
                curveOptimizer is IPerCoreCurveOptimizerTransport perCore ? AccessHardware(() => perCore.ReadPerCore(cancellationToken), readOnly: true) :
                curveOptimizer is not null ? await AccessHardwareAsync(() => curveOptimizer.ReadAsync(cancellationToken), readOnly: true) : null,
            CpuTuningField.PboScalar => pboScalar is null ? null : AccessHardware(() => pboScalar.Read(cancellationToken), readOnly: true),
            CpuTuningField.PerCoreCurveOptimizer =>
                curveOptimizer is IPerCoreCurveOptimizerTransport cores ? AccessHardware(() => cores.ReadPerCore(cancellationToken), readOnly: true) : null,
            CpuTuningField.StapmWatts or CpuTuningField.FastPptWatts or CpuTuningField.SlowPptWatts or
            CpuTuningField.PptWatts or CpuTuningField.VrmCurrentMilliamps or CpuTuningField.TdcCurrentMilliamps or
            CpuTuningField.EdcCurrentMilliamps or CpuTuningField.Mp1TemperatureC or CpuTuningField.RsmuTemperatureC =>
                smuLimits is null ? null : RyzenSmuAdvancedLimits.ReadField(ReadSmuSnapshot(cancellationToken).Limits, field),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    public async Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
    {
        EnsureHardwareAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (field is CpuTuningField.WindowsPowerSchemeId or CpuTuningField.MaxFrequencyMhz or CpuTuningField.CoreParkingPercent or CpuTuningField.BoostEnabled &&
            !powerSnapshots.ContainsKey(field))
            powerSnapshots[field] = await AccessHardwareAsync(() => powerSettings.ReadAsync(cancellationToken));
        switch (field)
        {
            case CpuTuningField.TemperatureLimitC:
            case CpuTuningField.SplWatts:
            case CpuTuningField.SpptWatts:
                oemAttempted = true;
                await WriteMiPowerFieldAsync(field, RequireInt(value, field), cancellationToken);
                break;
            case CpuTuningField.WindowsPowerSchemeId:
                await AccessHardwareAsync(() => powerSettings.SetSchemeAsync(RequireGuid(value, field), cancellationToken));
                break;
            case CpuTuningField.MaxFrequencyMhz:
                var frequency = RequireFrequency(value, field);
                await AccessHardwareAsync(() => powerSettings.SetMaxFrequencyAsync(frequency.Ac, frequency.Dc, cancellationToken));
                break;
            case CpuTuningField.CoreParkingPercent:
                if (value is not WindowsCoreParkingValue parking)
                    throw new InvalidOperationException("coreParkingValueUnavailable");
                await AccessHardwareAsync(() => powerSettings.SetCoreParkingAsync(parking.Ac, parking.Dc, cancellationToken));
                break;
            case CpuTuningField.BoostEnabled:
                await AccessHardwareAsync(() => powerSettings.SetBoostAsync(RequireBool(value, field), cancellationToken));
                break;
            case CpuTuningField.EnabledCoreCount:
                throw new InvalidOperationException("cpuCoreCountWriteUnavailable");
            case CpuTuningField.NegativeCurveOptimizer:
            case CpuTuningField.CurveOptimizerAll:
                if (curveOptimizer is null) throw new InvalidOperationException("curveOptimizerUnavailable");
                await AccessHardwareAsync(() => curveOptimizer.WriteAsync(RequireInt(value, field), cancellationToken));
                break;
            case CpuTuningField.PboScalar:
                if (pboScalar is null) throw new InvalidOperationException("pboScalarUnavailable");
                AccessHardware(() => pboScalar.Write(RequireInt(value, field), cancellationToken));
                break;
            case CpuTuningField.PerCoreCurveOptimizer:
                if (curveOptimizer is not IPerCoreCurveOptimizerTransport cores || value is not IReadOnlyDictionary<int,int> targets)
                    throw new InvalidOperationException("curveOptimizerUnavailable");
                AccessHardware(() => cores.WritePerCore(targets, cancellationToken));
                break;
            case CpuTuningField.StapmWatts:
            case CpuTuningField.FastPptWatts:
            case CpuTuningField.SlowPptWatts:
            case CpuTuningField.PptWatts:
            case CpuTuningField.VrmCurrentMilliamps:
            case CpuTuningField.TdcCurrentMilliamps:
            case CpuTuningField.EdcCurrentMilliamps:
            case CpuTuningField.Mp1TemperatureC:
            case CpuTuningField.RsmuTemperatureC:
                if (smuLimits is null) throw new InvalidOperationException("advancedCpuProtocolUnavailable");
                AccessHardware(() => smuLimits.WriteAdvancedLimit(RyzenSmuAdvancedLimits.BuildField(field, value, restoring: false), cancellationToken));
                break;
            case CpuTuningField.OverclockEnabled:
            case CpuTuningField.OcClockMhz:
            case CpuTuningField.OcVoltageMillivolts:
            case CpuTuningField.PerCoreOcClockMhz:
                throw new InvalidOperationException("advancedCpuProtocolUnavailable");
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    public async Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
    {
        EnsureHardwareAccess();
        cancellationToken.ThrowIfCancellationRequested();
        switch (field)
        {
            case CpuTuningField.TemperatureLimitC:
            case CpuTuningField.SplWatts:
            case CpuTuningField.SpptWatts:
                if (value is int previousPower)
                    await WriteMiPowerFieldAsync(field, previousPower, cancellationToken);
                break;
            case CpuTuningField.WindowsPowerSchemeId:
            case CpuTuningField.MaxFrequencyMhz:
            case CpuTuningField.CoreParkingPercent:
            case CpuTuningField.BoostEnabled:
                if (!powerSnapshots.TryGetValue(field, out var snapshot))
                    throw new InvalidOperationException("powerSnapshotUnavailable");
                await AccessHardwareAsync(() => powerSettings.RestoreAsync(field, snapshot, cancellationToken));
                break;
            case CpuTuningField.EnabledCoreCount:
                throw new InvalidOperationException("cpuCoreCountWriteUnavailable");
            case CpuTuningField.NegativeCurveOptimizer:
            case CpuTuningField.CurveOptimizerAll:
            case CpuTuningField.PerCoreCurveOptimizer:
                if (curveOptimizer is IPerCoreCurveOptimizerTransport perCore && value is IReadOnlyDictionary<int, int> previousCores)
                    AccessHardware(() => perCore.RestorePerCore(previousCores, cancellationToken));
                else if (curveOptimizer is not null && value is int previous)
                    await AccessHardwareAsync(() => curveOptimizer.WriteAsync(previous, cancellationToken));
                else throw new InvalidOperationException("curveOptimizerRestoreUnavailable");
                break;
            case CpuTuningField.PboScalar:
                if (pboScalar is null || value is not int previousScalar)
                    throw new InvalidOperationException("pboScalarRestoreUnavailable");
                AccessHardware(() => pboScalar.Write(previousScalar, cancellationToken));
                if (AccessHardware(() => pboScalar.Read(cancellationToken)) != previousScalar)
                    throw new InvalidOperationException("pboScalarRestoreMismatch");
                break;
            case CpuTuningField.StapmWatts:
            case CpuTuningField.FastPptWatts:
            case CpuTuningField.SlowPptWatts:
            case CpuTuningField.PptWatts:
            case CpuTuningField.VrmCurrentMilliamps:
            case CpuTuningField.TdcCurrentMilliamps:
            case CpuTuningField.EdcCurrentMilliamps:
            case CpuTuningField.Mp1TemperatureC:
            case CpuTuningField.RsmuTemperatureC:
                if (smuLimits is null) throw new InvalidOperationException("advancedCpuProtocolUnavailable");
                AccessHardware(() => smuLimits.WriteAdvancedLimit(RyzenSmuAdvancedLimits.BuildField(field, value, restoring: true), cancellationToken));
                if (!IsReadBackMatch(field, value, await ReadFieldAsync(field, cancellationToken)))
                    throw new InvalidOperationException("advancedCpuLimitRestoreMismatch");
                break;
            case CpuTuningField.OverclockEnabled:
            case CpuTuningField.OcClockMhz:
            case CpuTuningField.OcVoltageMillivolts:
            case CpuTuningField.PerCoreOcClockMhz:
                throw new InvalidOperationException("advancedCpuProtocolUnavailable");
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    public bool IsMiCpuPowerProtocolAvailable(CancellationToken cancellationToken)
    {
        OemCustomPowerMode = null;
        var response = AccessHardwareAsync(() => miClient.ReadAsync(cpuPowerBinding, cancellationToken), readOnly: true).GetAwaiter().GetResult();
        OemCustomPowerMode = response.Quality == DataQuality.Good
            ? MiCpuPowerCodec.ReadCustomMode(response.Payload ?? []) : null;
        return response.Quality == DataQuality.Good && response.Payload is { Length: > 4 };
    }

    public Task WriteConfirmedOemLimitAsync(
        CpuTuningField field,
        int value,
        CancellationToken cancellationToken)
    {
        if (field is not (CpuTuningField.TemperatureLimitC or CpuTuningField.SplWatts or CpuTuningField.SpptWatts))
            throw new ArgumentOutOfRangeException(nameof(field), field, "Field is not an OEM CPU limit.");
        int minimum = field == CpuTuningField.TemperatureLimitC ? 40 : 45;
        int maximum = field == CpuTuningField.TemperatureLimitC ? 100 : 75;
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), value, "OEM CPU limit is outside the software write range.");
        return WriteMiPowerFieldAsync(field, value, cancellationToken);
    }

    public bool IsReadBackRequired(CpuTuningField field) => true;

    public bool IsReadBackMatch(CpuTuningField field, object? requested, object? readBack) => field switch
    {
        CpuTuningField.NegativeCurveOptimizer or CpuTuningField.CurveOptimizerAll =>
            readBack is IReadOnlyDictionary<int, int> actual && requested is int target
                ? actual.Count == 8 && actual.Values.All(value => value == target)
                : Equals(requested, readBack),
        CpuTuningField.MaxFrequencyMhz => readBack is WindowsPowerFrequencyValue actualFrequency &&
            requested is WindowsPowerFrequencyValue targetFrequency &&
            actualFrequency == targetFrequency,
        CpuTuningField.CoreParkingPercent => readBack is WindowsCoreParkingValue actualParking &&
            requested is WindowsCoreParkingValue targetParking && actualParking == targetParking,
        CpuTuningField.PerCoreCurveOptimizer => requested is IReadOnlyDictionary<int, int> targets &&
            readBack is IReadOnlyDictionary<int, int> cores && cores.Count == 8 &&
            targets.All(pair => cores.TryGetValue(pair.Key, out int actual) && actual == pair.Value),
        CpuTuningField.BoostEnabled => readBack is WindowsPowerBoostValue boost &&
            requested is bool targetBoost && boost.IsEnabled == targetBoost,
        _ => Equals(requested, readBack)
    };

    private async Task<int?> ReadMiPowerFieldAsync(CpuTuningField field, CancellationToken cancellationToken)
    {
        if (smuLimits is not null)
        {
            var state = ReadSmuSnapshot(cancellationToken);
            return field switch
            {
                CpuTuningField.TemperatureLimitC => state.Limits.Mp1TemperatureC,
                CpuTuningField.SplWatts => state.OemSplWatts,
                CpuTuningField.SpptWatts when state.Limits.FastPptWatts is double watts && watts == Math.Round(watts) => (int)watts,
                _ => null
            };
        }
        var response = await AccessHardwareAsync(() => miClient.ReadAsync(cpuPowerBinding, cancellationToken), readOnly: true);
        var value = MiCpuPowerCodec.Read(response.Payload ?? [], field);
        return response.Quality == DataQuality.Good && value is int parsed
            ? parsed
            : null;
    }

    public async Task PrepareOemLimitsAsync(IReadOnlyList<CpuTuningPlan> plans, CancellationToken cancellationToken)
    {
        EnsureHardwareAccess();
        if (!plans.Any(plan => plan.TemperatureLimitC.HasValue || plan.SplWatts.HasValue || plan.SpptWatts.HasValue)) return;
        oemAttempted = true;
        await EnableMiCustomPowerModeAsync(cancellationToken);
    }

    private async Task EnableMiCustomPowerModeAsync(CancellationToken cancellationToken)
    {
        // Another console or a firmware mode change can clear CMEN between presets.
        var mode = await AccessHardwareAsync(() => miClient.ReadAsync(cpuPowerBinding, cancellationToken), readOnly: true);
        var enabled = mode.Quality == DataQuality.Good
            ? MiCpuPowerCodec.ReadCustomMode(mode.Payload ?? []) : null;
        if (enabled is null)
            throw new InvalidOperationException("cpuPowerCustomModeReadFailed");
        if (enabled == false)
        {
            var modeWrite = await AccessHardwareAsync(() => miClient.WriteOnceAsync(
                cpuPowerBinding, MiCpuPowerCodec.CustomMode(true), cancellationToken));
            if (modeWrite.Quality != DataQuality.Good)
                throw new InvalidOperationException("cpuPowerCustomModeWriteFailed");
            var readBack = await AccessHardwareAsync(() => miClient.ReadAsync(cpuPowerBinding, cancellationToken), readOnly: true);
            if (readBack.Quality != DataQuality.Good ||
                MiCpuPowerCodec.ReadCustomMode(readBack.Payload ?? []) != true)
                throw new InvalidOperationException("cpuPowerCustomModeNotEnabled");
        }

    }

    private async Task WriteMiPowerFieldAsync(CpuTuningField field, int value, CancellationToken cancellationToken)
    {
        await EnableMiCustomPowerModeAsync(cancellationToken);
        var write = await AccessHardwareAsync(() => miClient.WriteOnceAsync(
            cpuPowerBinding, MiCpuPowerCodec.Encode(field, value), cancellationToken));
        if (write.Quality != DataQuality.Good)
            throw new InvalidOperationException("cpuPowerWriteFailed");
    }

    public async Task<WindowsPowerSettingsSnapshot> ReadWindowsPowerSettingsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await AccessHardwareAsync(() => powerSettings.ReadAsync(cancellationToken), readOnly: true);
        ActivePowerSchemeName = snapshot.ActiveSchemeName;
        return snapshot;
    }
    public async Task RestoreCapturedBoostModesAsync(int ac, int dc, CancellationToken token)
    {
        if (ac is < 0 or > 6 || dc is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(ac));
        var current = await AccessHardwareAsync(() => powerSettings.ReadAsync(token));
        await AccessHardwareAsync(() => powerSettings.RestoreAsync(CpuTuningField.BoostEnabled, current with { AcBoostMode = ac, DcBoostMode = dc }, token));
        var after = await AccessHardwareAsync(() => powerSettings.ReadAsync(token));
        if (after.ActiveSchemeId != current.ActiveSchemeId || after.AcBoostMode != ac || after.DcBoostMode != dc)
            throw new IOException("boostBaselineRestoreMismatch");
    }

    private async Task<object?> ReadWindowsPowerFieldAsync(
        CpuTuningField field,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadWindowsPowerSettingsAsync(cancellationToken);
        return field switch
        {
            CpuTuningField.WindowsPowerSchemeId => snapshot.ActiveSchemeId,
            CpuTuningField.MaxFrequencyMhz => new WindowsPowerFrequencyValue(
                snapshot.AcMaxFrequencyMhz, snapshot.DcMaxFrequencyMhz),
            CpuTuningField.CoreParkingPercent => snapshot.AcMinActiveCoresPercent is int acParking &&
                snapshot.DcMinActiveCoresPercent is int dcParking
                    ? new WindowsCoreParkingValue(acParking, dcParking) : null,
            CpuTuningField.BoostEnabled => new WindowsPowerBoostValue(
                snapshot.AcBoostMode, snapshot.DcBoostMode),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    private static int ReadPhysicalCoreCount()
    {
        using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
        var count = searcher.Get()
            .Cast<ManagementObject>()
            .Select(item => item["NumberOfCores"])
            .OfType<uint>()
            .Sum(value => checked((int)value));
        return count > 0 ? count : throw new InvalidOperationException("cpuCoreCountReadFailed");
    }

    private static int RequireInt(object? value, CpuTuningField field) =>
        value is int parsed ? parsed : throw new ArgumentException("Expected Int32.", field.ToString());

    private static WindowsPowerFrequencyValue RequireFrequency(object? value, CpuTuningField field) =>
        value switch
        {
            WindowsPowerFrequencyValue frequency => frequency,
            int legacy => new WindowsPowerFrequencyValue(legacy, legacy),
            _ => throw new ArgumentException("Expected AC/DC frequency values.", field.ToString())
        };

    private static bool RequireBool(object? value, CpuTuningField field) =>
        value is bool parsed ? parsed : throw new ArgumentException("Expected Boolean.", field.ToString());

    private static Guid RequireGuid(object? value, CpuTuningField field) =>
        value is Guid parsed && parsed != Guid.Empty
            ? parsed
            : throw new ArgumentException("Expected a non-empty Guid.", field.ToString());
}
