using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.ViewModels;

public enum ControlSource
{
    Firmware,
    Windows,
    Product
}

public static class ControlSourceExtensions
{
    public static string ToDisplayName(this ControlSource source) => source switch
    {
        ControlSource.Firmware => "固件",
        ControlSource.Windows => "Windows",
        ControlSource.Product => "本产品",
        _ => "未知来源"
    };
}

public sealed record QuickSettingItem(
    QuickSettingKind Kind,
    string Label,
    ControlSource Source,
    bool? IsOn,
    CapabilityState CapabilityState);

public static class QuickSettingsFactory
{
    public static IReadOnlyList<QuickSettingItem> CreateDefault() =>
    [
        new(QuickSettingKind.Fn, "Fn", ControlSource.Firmware, null, CapabilityState.ReadOnly),
        new(QuickSettingKind.Touchpad, "触控板", ControlSource.Firmware, null, CapabilityState.ReadOnly),
        new(QuickSettingKind.NumLock, "NumLock", ControlSource.Windows, null, CapabilityState.ReadOnly),
        new(QuickSettingKind.CapsLock, "CapsLock", ControlSource.Windows, null, CapabilityState.ReadOnly),
        new(QuickSettingKind.LidLogo, "背板灯", ControlSource.Firmware, null, CapabilityState.ReadOnly),
        new(QuickSettingKind.Wifi, "Wi-Fi", ControlSource.Windows, null, CapabilityState.ReadOnly)
    ];
}
