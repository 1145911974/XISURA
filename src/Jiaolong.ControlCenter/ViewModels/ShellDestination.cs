namespace Jiaolong_ControlCenter.ViewModels;

public sealed record ShellDestination(string Id, string Title, string IconAsset)
{
    public static IReadOnlyList<ShellDestination> All { get; } =
    [
        new("Home", "主页", "ms-appx:///Assets/Icons/Home.svg"),
        new("Performance", "性能", "ms-appx:///Assets/Icons/Performance.svg"),
        new("Gpu", "显卡", "ms-appx:///Assets/Icons/Gpu.svg"),
        new("Fan", "风扇", "ms-appx:///Assets/Icons/Fan.svg"),
        new("Lighting", "灯光", "ms-appx:///Assets/Icons/Lighting.svg"),
        new("Automation", "自动化", "ms-appx:///Assets/Icons/Automation.svg"),
        new("Settings", "设置", "ms-appx:///Assets/Icons/Settings.svg")
    ];
}
