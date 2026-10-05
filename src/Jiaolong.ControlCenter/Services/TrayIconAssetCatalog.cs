using Jiaolong_ControlCenter.Prototype;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public static class TrayIconAssetCatalog
{
    public const string BaseAsset = "Assets\\AppIcon.ico";
    public const string FixedBrandAsset = "Assets\\Brand\\JiaolongWaveApp.ico";

    public static string For(string style, ControlModeId? mode) => mode is null || !Enum.IsDefined(mode.Value)
        ? FixedBrandAsset
        : style == "classic" ? For(mode.Value switch
        {
            ControlModeId.Office => PrototypePerformanceMode.Office,
            ControlModeId.Gaming => PrototypePerformanceMode.Gaming,
            ControlModeId.Turbo => PrototypePerformanceMode.Turbo,
            _ => PrototypePerformanceMode.Custom
        }) : $"Assets\\Brand\\CModeIcons\\JiaolongC{mode}.ico";

    public static string ResolveAbsolute(string baseDirectory, string style, ControlModeId? mode)
    {
        string asset = For(style, mode);
        if (style != "classic") asset = asset[..^4] + "-runtime.ico";
        return Path.Combine(baseDirectory, asset.Replace('\\', Path.DirectorySeparatorChar));
    }

    public static string For(PrototypePerformanceMode mode) => mode switch
    {
        PrototypePerformanceMode.Office => "Assets\\Brand\\JiaolongTrayIconOffice-tight.ico",
        PrototypePerformanceMode.Gaming => "Assets\\Brand\\JiaolongTrayIconGaming-tight.ico",
        PrototypePerformanceMode.Turbo => "Assets\\Brand\\JiaolongTrayIconTurbo-tight.ico",
        _ => BaseAsset
    };

    public static string ResolveAbsolute(string baseDirectory, PrototypePerformanceMode mode) =>
        Path.Combine(baseDirectory, For(mode).Replace('\\', Path.DirectorySeparatorChar));
}
