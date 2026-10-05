using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Prototype;

public sealed class PendingQuickSettingStates
{
    private readonly Dictionary<QuickSettingKind, (bool DesiredUiEnabled, long Version)> desiredUiStates = [];
    private long nextVersion;

    public long Set(QuickSettingKind setting, bool desiredUiEnabled)
    {
        var version = ++nextVersion;
        desiredUiStates[setting] = (desiredUiEnabled, version);
        return version;
    }

    public bool Clear(QuickSettingKind setting) => desiredUiStates.Remove(setting);

    public bool Clear(QuickSettingKind setting, long version) =>
        desiredUiStates.TryGetValue(setting, out var pending) &&
        pending.Version == version &&
        desiredUiStates.Remove(setting);

    public bool ShouldPreserve(QuickSettingKind setting, bool? hardwareEnabled) =>
        desiredUiStates.TryGetValue(setting, out var pending) &&
        (!hardwareEnabled.HasValue || HomeQuickSettingSemantics.ToUiEnabled(setting, hardwareEnabled.Value) != pending.DesiredUiEnabled);

    public bool TryConfirm(QuickSettingKind setting, bool? hardwareEnabled)
    {
        if (!desiredUiStates.TryGetValue(setting, out var pending) ||
            !hardwareEnabled.HasValue ||
            HomeQuickSettingSemantics.ToUiEnabled(setting, hardwareEnabled.Value) != pending.DesiredUiEnabled)
            return false;

        desiredUiStates.Remove(setting);
        return true;
    }
}
