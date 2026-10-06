using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Prototype.QuickMenu;

public static class QuickMenuCatalog
{
    public static bool IsLocal(QuickSettingKind kind) => kind is QuickSettingKind.WinKey or QuickSettingKind.NumLock or QuickSettingKind.CapsLock or QuickSettingKind.DisplayOff;

    public static bool IsAvailable(QuickMenuItem item, HomeStateSnapshot? snapshot) =>
        IsLocal(item.Kind) || item.IsAction || snapshot?.Capabilities.Items.Any(capability =>
            capability.Key == item.CapabilityKey && capability.State == CapabilityState.Available) == true;

    public static IReadOnlyList<QuickMenuItem> CreateDefault() =>
    [
        Item(QuickSettingKind.Wifi, "Wi-Fi", "ms-appx:///Assets/QuickMenu/QuickWifi.png"),
        Item(QuickSettingKind.Bluetooth, "蓝牙", "ms-appx:///Assets/QuickMenu/QuickBluetooth.png"),
        Item(QuickSettingKind.Touchpad, "触控板", "ms-appx:///Assets/QuickMenu/QuickTouchpad.png"),
        Item(QuickSettingKind.FnLock, "Fn", "ms-appx:///Assets/QuickMenu/QuickFn.png"),
        Item(QuickSettingKind.LidLogo, "背板灯", "ms-appx:///Assets/QuickMenu/QuickBackplate.png"),
        Item(QuickSettingKind.StrongCooling, "一键强冷", "ms-appx:///Assets/QuickMenu/QuickStrongCooling.png", "strongCooling"),
        Item(QuickSettingKind.NumLock, "NumLock", "ms-appx:///Assets/QuickMenu/QuickNumLock.png"),
        Item(QuickSettingKind.CapsLock, "CapsLock", "ms-appx:///Assets/QuickMenu/QuickCapsLock.png"),
        Item(QuickSettingKind.WinKey, "Win键", "ms-appx:///Assets/QuickMenu/QuickWinKey.png"),
        Item(QuickSettingKind.DisplayOff, "显示器息屏", "ms-appx:///Assets/QuickMenu/QuickDisplayOff.png", "system:displayOff", QuickMenuInteraction.Action)
    ];

    private static QuickMenuItem Item(
        QuickSettingKind kind,
        string label,
        string icon,
        string? capabilityKey = null,
        QuickMenuInteraction interaction = QuickMenuInteraction.Toggle) =>
        new(kind, label, icon, capabilityKey ?? $"quickSetting:{ToLowerCamel(kind.ToString())}", interaction);

    private static string ToLowerCamel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
