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
    ISmuCpuLimitTransport? smuLimits = null) : ICpuTuningTransport
{
    private readonly Dictionary<CpuTuningField, WindowsPowerSettingsSnapshot> powerSnapshots = [];
    private CpuSmuLimitSnapshot? originalSmuLimits;
    private bool? originalCustomMode;
    private bool oemAttempted;
    private WindowsPowerSettingsSnapshot? originalModePowerSettings;
    public string? ActivePowerSchemeName { get; private set; }
    public bool? OemCustomPowerMode { get; private set; }

    public Task CaptureTransactionAsync(IReadOnlyList<CpuTuningPlan> plans, CancellationToken token) =>
        CaptureTransactionAsync(plans, false, token);

    public async Task CaptureTransactionAsync(IReadOnlyList<CpuTuningPlan> plans, bool captureNativeModeEffects, CancellationToken token)
    {
        powerSnapshots.Clear();
        originalSmuLimits = null;
        originalCustomMode = null;
        oemAttempted = false;
        originalModePowerSettings = null;
        if (captureNativeModeEffects)
        {
            var power = await powerSettings.ReadAsync(token);
            if (power.AcMinActiveCoresPercent is null || power.DcMinActiveCoresPercent is null)
                throw new InvalidOperationException("nativeModeWindowsOriginalUnavailable");
            originalModePowerSettings = power;
            foreach (var field in WindowsModeFields) powerSnapshots[field] = power;
        }
        if (!captureNativeModeEffects &&
            !plans.Any(plan => plan.TemperatureLimitC.HasValue || plan.SplWatts.HasValue || plan.SpptWatts.HasValue)) return;
        originalSmuLimits = smuLimits?.ReadLimitSnapshot(token) ??
            throw new InvalidOperationException("oemCpuLimitOriginalUnavailable");
        var mode = await miClient.ReadAsync(cpuPowerBinding, token);
        originalCustomMode = mode.Quality == DataQuality.Good ? MiCpuPowerCodec.ReadCustomMode(mode.Payload ?? []) : null;
        if (originalCustomMode is null) throw new InvalidOperationException("cpuPowerCustomModeReadFailed");
    }

    public Task RestoreTransactionAsync(CancellationToken token) => RestoreTransactionAsync(null, token);

    public async Task RestoreTransactionAsync(Func<CancellationToken, Task>? restoreNativeMode, CancellationToken token)
    {
        if (!oemAttempted && originalModePowerSettings is null && restoreNativeMode is null) return;
        var failures = new List<Exception>();
        try { await RestoreOemTransactionAsync(restoreNativeMode, token); }
        catch (Exception error) { failures.Add(error); }
        if (originalModePowerSettings is { } power)
        {
            foreach (var field in WindowsModeFields)
            {
                try { await powerSettings.RestoreAsync(field, power, token); }
                catch (Exception error) { failures.Add(error); }
            }
            try
            {
                if (((await powerSettings.ReadAsync(token)) with { ActiveSchemeName = null }) !=
                    (power with { ActiveSchemeName = null }))
                    throw new InvalidOperationException("nativeModeWindowsRestoreMismatch");
            }
            catch (Exception error) { failures.Add(error); }
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
        if (originalSmuLimits is null || originalCustomMode is null || smuLimits is null)
            throw new InvalidOperationException("oemCpuLimitOriginalUnavailable");
        var failures = new List<Exception>();
        // Recover the gate before the native mode; re-enabling CMEN can select Turbo.
        try
        {
            await RestoreOemPowerFieldsAsync(token);
        }
        catch (Exception error) { failures.Add(error); }
        try
        {
            var currentMode = await miClient.ReadAsync(cpuPowerBinding, token);
            if (currentMode.Quality != DataQuality.Good) throw new InvalidOperationException("cpuPowerCustomModeReadFailed");
            if (MiCpuPowerCodec.ReadCustomMode(currentMode.Payload ?? []) != originalCustomMode)
            {
                var modeWrite = await miClient.WriteOnceAsync(cpuPowerBinding, MiCpuPowerCodec.CustomMode(originalCustomMode.Value), token);
                if (modeWrite.Quality != DataQuality.Good) throw new InvalidOperationException("cpuPowerCustomModeRestoreFailed");
            }
        }
        catch (Exception error) { failures.Add(error); }
        try { if (restoreNativeMode is not null) await restoreNativeMode(token); }
        catch (Exception error) { failures.Add(error); }
        try
        {
            // Stock mode selection resets the OEM SPL slot, which has no SMU-only setter.
            if (originalCustomMode.Value)
            {
                await RestoreOemPowerFieldsAsync(token);
            }
            foreach (var field in new[] { CpuTuningField.StapmWatts, CpuTuningField.FastPptWatts, CpuTuningField.SlowPptWatts,
                CpuTuningField.VrmCurrentMilliamps, CpuTuningField.EdcCurrentMilliamps, CpuTuningField.Mp1TemperatureC })
                smuLimits.WriteAdvancedLimit(RyzenSmuAdvancedLimits.BuildField(field,
                    RyzenSmuAdvancedLimits.ReadField(originalSmuLimits.Limits, field), restoring: true), token);
            var mode = await miClient.ReadAsync(cpuPowerBinding, token);
            if (mode.Quality != DataQuality.Good || MiCpuPowerCodec.ReadCustomMode(mode.Payload ?? []) != originalCustomMode ||
                smuLimits.ReadLimitSnapshot(token) != originalSmuLimits)
                throw new InvalidOperationException("oemCpuLimitRestoreMismatch");
        }
        catch (Exception error) { failures.Add(error); }
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
        cancellationToken.ThrowIfCancellationRequested();
        return field switch
        {
            CpuTuningField.TemperatureLimitC or CpuTuningField.SplWatts or CpuTuningField.SpptWatts =>
                await ReadMiPowerFieldAsync(field, cancellationToken),
            CpuTuningField.MaxFrequencyMhz or CpuTuningField.CoreParkingPercent or
                CpuTuningField.BoostEnabled or CpuTuningField.WindowsPowerSchemeId =>
                await ReadWindowsPowerFieldAsync(field, cancellationToken),
            CpuTuningField.EnabledCoreCount => ReadPhysicalCoreCount(),
            CpuTuningField.NegativeCurveOptimizer or CpuTuningField.CurveOptimizerAll =>
                curveOptimizer is IPerCoreCurveOptimizerTransport perCore ? perCore.ReadPerCore(cancellationToken) :
                curveOptimizer is not null ? await curveOptimizer.ReadAsync(cancellationToken) : null,
            CpuTuningField.PboScalar => pboScalar?.Read(cancellationToken),
            CpuTuningField.PerCoreCurveOptimizer =>
                curveOptimizer is IPerCoreCurveOptimizerTransport cores ? cores.ReadPerCore(cancellationToken) : null,
            CpuTuningField.StapmWatts or CpuTuningField.FastPptWatts or CpuTuningField.SlowPptWatts or
            CpuTuningField.PptWatts or CpuTuningField.VrmCurrentMilliamps or CpuTuningField.TdcCurrentMilliamps or
            CpuTuningField.EdcCurrentMilliamps or CpuTuningField.Mp1TemperatureC or CpuTuningField.RsmuTemperatureC =>
                smuLimits is null ? null : RyzenSmuAdvancedLimits.ReadField(smuLimits.ReadLimitSnapshot(cancellationToken).Limits, field),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    public async Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (field is CpuTuningField.WindowsPowerSchemeId or CpuTuningField.MaxFrequencyMhz or CpuTuningField.CoreParkingPercent or CpuTuningField.BoostEnabled &&
            !powerSnapshots.ContainsKey(field))
            powerSnapshots[field] = await powerSettings.ReadAsync(cancellationToken);
        switch (field)
        {
            case CpuTuningField.TemperatureLimitC:
            case CpuTuningField.SplWatts:
            case CpuTuningField.SpptWatts:
                oemAttempted = true;
                await WriteMiPowerFieldAsync(field, RequireInt(value, field), cancellationToken);
                break;
            case CpuTuningField.WindowsPowerSchemeId:
                await powerSettings.SetSchemeAsync(RequireGuid(value, field), cancellationToken);
                break;
            case CpuTuningField.MaxFrequencyMhz:
                var frequency = RequireFrequency(value, field);
                await powerSettings.SetMaxFrequencyAsync(frequency.Ac, frequency.Dc, cancellationToken);
                break;
            case CpuTuningField.CoreParkingPercent:
                if (value is not WindowsCoreParkingValue parking)
                    throw new InvalidOperationException("coreParkingValueUnavailable");
                await powerSettings.SetCoreParkingAsync(parking.Ac, parking.Dc, cancellationToken);
                break;
            case CpuTuningField.BoostEnabled:
                await powerSettings.SetBoostAsync(RequireBool(value, field), cancellationToken);
                break;
            case CpuTuningField.EnabledCoreCount:
                throw new InvalidOperationException("cpuCoreCountWriteUnavailable");
            case CpuTuningField.NegativeCurveOptimizer:
            case CpuTuningField.CurveOptimizerAll:
                if (curveOptimizer is null) throw new InvalidOperationException("curveOptimizerUnavailable");
                await curveOptimizer.WriteAsync(RequireInt(value, field), cancellationToken);
                break;
            case CpuTuningField.PboScalar:
                if (pboScalar is null) throw new InvalidOperationException("pboScalarUnavailable");
                pboScalar.Write(RequireInt(value, field), cancellationToken);
                break;
            case CpuTuningField.PerCoreCurveOptimizer:
                if (curveOptimizer is not IPerCoreCurveOptimizerTransport cores || value is not IReadOnlyDictionary<int,int> targets)
                    throw new InvalidOperationException("curveOptimizerUnavailable");
                cores.WritePerCore(targets, cancellationToken);
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
                smuLimits.WriteAdvancedLimit(RyzenSmuAdvancedLimits.BuildField(field, value, restoring: false), cancellationToken);
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
                await powerSettings.RestoreAsync(field, snapshot, cancellationToken);
                break;
            case CpuTuningField.EnabledCoreCount:
                throw new InvalidOperationException("cpuCoreCountWriteUnavailable");
            case CpuTuningField.NegativeCurveOptimizer:
            case CpuTuningField.CurveOptimizerAll:
            case CpuTuningField.PerCoreCurveOptimizer:
                if (curveOptimizer is IPerCoreCurveOptimizerTransport perCore && value is IReadOnlyDictionary<int, int> previousCores)
                    perCore.RestorePerCore(previousCores, cancellationToken);
                else if (curveOptimizer is not null && value is int previous)
                    await curveOptimizer.WriteAsync(previous, cancellationToken);
                else throw new InvalidOperationException("curveOptimizerRestoreUnavailable");
                break;
            case CpuTuningField.PboScalar:
                if (pboScalar is null || value is not int previousScalar)
                    throw new InvalidOperationException("pboScalarRestoreUnavailable");
                pboScalar.Write(previousScalar, cancellationToken);
                if (pboScalar.Read(cancellationToken) != previousScalar)
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
                smuLimits.WriteAdvancedLimit(RyzenSmuAdvancedLimits.BuildField(field, value, restoring: true), cancellationToken);
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
        var response = miClient.ReadAsync(cpuPowerBinding, cancellationToken).GetAwaiter().GetResult();
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
            var state = smuLimits.ReadLimitSnapshot(cancellationToken);
            return field switch
            {
                CpuTuningField.TemperatureLimitC => state.Limits.Mp1TemperatureC,
                CpuTuningField.SplWatts => state.OemSplWatts,
                CpuTuningField.SpptWatts when state.Limits.FastPptWatts is double watts && watts == Math.Round(watts) => (int)watts,
                _ => null
            };
        }
        var response = await miClient.ReadAsync(cpuPowerBinding, cancellationToken);
        var value = MiCpuPowerCodec.Read(response.Payload ?? [], field);
        return response.Quality == DataQuality.Good && value is int parsed
            ? parsed
            : null;
    }

    public async Task PrepareOemLimitsAsync(IReadOnlyList<CpuTuningPlan> plans, CancellationToken cancellationToken)
    {
        if (!plans.Any(plan => plan.TemperatureLimitC.HasValue || plan.SplWatts.HasValue || plan.SpptWatts.HasValue)) return;
        oemAttempted = true;
        await EnableMiCustomPowerModeAsync(cancellationToken);
    }

    private async Task EnableMiCustomPowerModeAsync(CancellationToken cancellationToken)
    {
        // Another console or a firmware mode change can clear CMEN between presets.
        var mode = await miClient.ReadAsync(cpuPowerBinding, cancellationToken);
        var enabled = mode.Quality == DataQuality.Good
            ? MiCpuPowerCodec.ReadCustomMode(mode.Payload ?? []) : null;
        if (enabled is null)
            throw new InvalidOperationException("cpuPowerCustomModeReadFailed");
        if (enabled == false)
        {
            var modeWrite = await miClient.WriteOnceAsync(
                cpuPowerBinding, MiCpuPowerCodec.CustomMode(true), cancellationToken);
            if (modeWrite.Quality != DataQuality.Good)
                throw new InvalidOperationException("cpuPowerCustomModeWriteFailed");
            var readBack = await miClient.ReadAsync(cpuPowerBinding, cancellationToken);
            if (readBack.Quality != DataQuality.Good ||
                MiCpuPowerCodec.ReadCustomMode(readBack.Payload ?? []) != true)
                throw new InvalidOperationException("cpuPowerCustomModeNotEnabled");
        }

    }

    private async Task WriteMiPowerFieldAsync(CpuTuningField field, int value, CancellationToken cancellationToken)
    {
        await EnableMiCustomPowerModeAsync(cancellationToken);
        var write = await miClient.WriteOnceAsync(
            cpuPowerBinding, MiCpuPowerCodec.Encode(field, value), cancellationToken);
        if (write.Quality != DataQuality.Good)
            throw new InvalidOperationException("cpuPowerWriteFailed");
    }

    public async Task<WindowsPowerSettingsSnapshot> ReadWindowsPowerSettingsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await powerSettings.ReadAsync(cancellationToken);
        ActivePowerSchemeName = snapshot.ActiveSchemeName;
        return snapshot;
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
