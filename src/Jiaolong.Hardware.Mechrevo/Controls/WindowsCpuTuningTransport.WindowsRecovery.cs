namespace Jiaolong.Hardware.Mechrevo.Controls;

public sealed partial class WindowsCpuTuningTransport
{
    private sealed record ScopedPowerRecovery(WindowsPowerSettingsSnapshot Original, List<WindowsPowerSettingsSnapshot> Known);
    private readonly Dictionary<(Guid Scheme, CpuTuningField Field), ScopedPowerRecovery> scopedWindowsRecovery = [];
    private WindowsPowerSettingsSnapshot? scopedWindowsSelection;
    private readonly HashSet<Guid> scopedWindowsSelections = [];

    private void ClearScopedWindowsRecovery()
    { scopedWindowsRecovery.Clear(); scopedWindowsSelections.Clear(); scopedWindowsSelection = null; }

    private void RememberScopedWindowsSelection(WindowsPowerSettingsSnapshot snapshot)
    {
        if (powerSettings is not IWindowsPowerSettingsRecoveryTransport || scopedWindowsSelection is not null) return;
        scopedWindowsSelection = snapshot;
        scopedWindowsSelections.Add(snapshot.ActiveSchemeId);
    }

    private void TrackScopedWindowsWrite(CpuTuningField field, object? value, WindowsPowerSettingsSnapshot before)
    {
        if (powerSettings is not IWindowsPowerSettingsRecoveryTransport) return;
        RememberScopedWindowsSelection(before);
        if (field == CpuTuningField.WindowsPowerSchemeId)
        { scopedWindowsSelections.Add(RequireGuid(value, field)); return; }
        var target = field switch
        {
            CpuTuningField.MaxFrequencyMhz => before with { AcMaxFrequencyMhz = RequireFrequency(value, field).Ac, DcMaxFrequencyMhz = RequireFrequency(value, field).Dc },
            CpuTuningField.CoreParkingPercent when value is WindowsCoreParkingValue parking => before with
                { AcMinActiveCoresPercent = parking.Ac, DcMinActiveCoresPercent = parking.Dc },
            CpuTuningField.BoostEnabled when value is WindowsPowerBoostValue boost => before with { AcBoostMode = boost.AcMode, DcBoostMode = boost.DcMode },
            CpuTuningField.BoostEnabled when value is bool boost => before with
                { AcBoostMode = WindowsPowerBoostMode.SelectTarget(before.AcBoostMode, boost), DcBoostMode = WindowsPowerBoostMode.SelectTarget(before.DcBoostMode, boost) },
            _ => throw new InvalidDataException("windowsRecoveryTargetUnavailable")
        };
        var key = (before.ActiveSchemeId, field);
        if (!scopedWindowsRecovery.TryGetValue(key, out var recovery))
        {
            scopedWindowsRecovery[key] = recovery = new(before, [before]);
        }
        else if (ScopedWindowsIndices(field).Any(index =>
            !recovery.Known.Any(snapshot => ScopedWindowsIndex(snapshot, index) == ScopedWindowsIndex(before, index))))
            throw new InvalidOperationException("windowsScopedRecoveryExternalChange");
        recovery.Known.Add(target);
    }

    private static WindowsCpuSetting[] ScopedWindowsIndices(CpuTuningField field) => field switch
    {
        CpuTuningField.MaxFrequencyMhz => [WindowsCpuSetting.AcMaxFrequencyMhz, WindowsCpuSetting.DcMaxFrequencyMhz],
        CpuTuningField.BoostEnabled => [WindowsCpuSetting.AcBoostMode, WindowsCpuSetting.DcBoostMode],
        CpuTuningField.CoreParkingPercent => [WindowsCpuSetting.AcMinActiveCoresPercent, WindowsCpuSetting.DcMinActiveCoresPercent],
        _ => []
    };

    private static int? ScopedWindowsIndex(WindowsPowerSettingsSnapshot snapshot, WindowsCpuSetting index) => index switch
    {
        WindowsCpuSetting.AcMaxFrequencyMhz => snapshot.AcMaxFrequencyMhz,
        WindowsCpuSetting.DcMaxFrequencyMhz => snapshot.DcMaxFrequencyMhz,
        WindowsCpuSetting.AcBoostMode => snapshot.AcBoostMode,
        WindowsCpuSetting.DcBoostMode => snapshot.DcBoostMode,
        WindowsCpuSetting.AcMinActiveCoresPercent => snapshot.AcMinActiveCoresPercent,
        WindowsCpuSetting.DcMinActiveCoresPercent => snapshot.DcMinActiveCoresPercent,
        _ => null
    };

    private async Task RestoreScopedWindowsFieldAsync(CpuTuningField field, CancellationToken token)
    {
        if (powerSettings is not IWindowsPowerSettingsRecoveryTransport scoped) return;
        var failures = new List<Exception>();
        foreach (var entry in scopedWindowsRecovery.Where(pair => pair.Key.Field == field))
        {
            foreach (var index in ScopedWindowsIndices(field))
            {
                try
                {
                    var actual = await AccessHardwareAsync(() => scoped.ReadSchemeAsync(entry.Key.Scheme, token));
                    if (actual.ActiveSchemeId != entry.Key.Scheme || ScopedWindowsIndex(actual, index) is not int current ||
                        ScopedWindowsIndex(entry.Value.Original, index) is not int target)
                        throw new InvalidDataException("windowsScopedRecoveryReadUnavailable");
                    if (current == target) continue;
                    if (!entry.Value.Known.Any(snapshot => ScopedWindowsIndex(snapshot, index) == current))
                        throw new InvalidOperationException("windowsScopedRecoveryExternalChange");
                    await AccessHardwareAsync(() => scoped.RestoreSettingAsync(entry.Key.Scheme, index, current, target, token));
                    actual = await AccessHardwareAsync(() => scoped.ReadSchemeAsync(entry.Key.Scheme, token));
                    if (actual.ActiveSchemeId != entry.Key.Scheme || ScopedWindowsIndex(actual, index) != target)
                        throw new IOException("windowsScopedRecoveryReadbackMismatch");
                }
                catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
            }
        }
        if (failures.Count > 0) throw new AggregateException("windowsScopedRecoveryFailed", failures);
    }

    private async Task RestoreScopedWindowsAsync(CancellationToken token)
    {
        if (scopedWindowsSelection is null) return;
        var failures = new List<Exception>();
        foreach (var field in scopedWindowsRecovery.Keys.Select(key => key.Field).Distinct())
        {
            try { await RestoreScopedWindowsFieldAsync(field, token); }
            catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        }
        try
        {
            var actual = await AccessHardwareAsync(() => powerSettings.ReadAsync(token));
            if (actual.ActiveSchemeId != scopedWindowsSelection.ActiveSchemeId)
            {
                if (!scopedWindowsSelections.Contains(actual.ActiveSchemeId)) throw new InvalidOperationException("windowsRecoverySelectionExternallyChanged");
                await AccessHardwareAsync(() => powerSettings.RestoreAsync(CpuTuningField.WindowsPowerSchemeId, scopedWindowsSelection, token));
                if ((await AccessHardwareAsync(() => powerSettings.ReadAsync(token))).ActiveSchemeId != scopedWindowsSelection.ActiveSchemeId)
                    throw new IOException("windowsRecoverySelectionMismatch");
            }
        }
        catch (Exception error) when (error is not HardwareLifecycleInterruptedException) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("windowsScopedTransactionRestoreFailed", failures);
    }
}
