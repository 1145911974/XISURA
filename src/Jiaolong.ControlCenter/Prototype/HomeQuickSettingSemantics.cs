using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Prototype;

public static class HomeQuickSettingSemantics
{
    public static bool ToUiEnabled(QuickSettingKind setting, bool hardwareEnabled) =>
        UsesHardwareLockBit(setting) ? !hardwareEnabled : hardwareEnabled;

    public static bool ToHardwareEnabled(QuickSettingKind setting, bool uiEnabled) =>
        UsesHardwareLockBit(setting) ? !uiEnabled : uiEnabled;

    private static bool UsesHardwareLockBit(QuickSettingKind setting) =>
        setting is QuickSettingKind.Touchpad or QuickSettingKind.FnLock;
}
