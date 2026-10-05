namespace Jiaolong_ControlCenter.ViewModels;

public sealed record PerformanceProfile(string Key, string Name, string Baseline);

public readonly record struct PerformanceRange(int Minimum, int Maximum, string Unit)
{
    public bool Contains(int value) => value >= Minimum && value <= Maximum;
}

public static class PerformanceCapabilities
{
    public static IReadOnlyList<PerformanceProfile> EditableProfiles { get; } =
    [
        new("office", "办公", "办公基准"),
        new("game", "游戏", "游戏基准"),
        new("turbo", "狂飙", "狂飙基准"),
        new("custom-1", "自定义 1", "办公基准"),
        new("custom-2", "自定义 2", "游戏基准"),
        new("custom-3", "自定义 3", "狂飙基准")
    ];

    public static PerformanceRange TemperatureWallC { get; } = new(45, 100, "°C");
    public static PerformanceRange SplWatts { get; } = new(20, 105, "W");
    public static PerformanceRange SpptWatts { get; } = new(20, 120, "W");
    public static PerformanceRange MaxFrequencyMhz { get; } = new(1_500, 5_400, "MHz");
    public static PerformanceRange FastPowerLimitWatts { get; } = new(20, 120, "W");
    public static PerformanceRange SlowPowerLimitWatts { get; } = new(20, 105, "W");

    public const bool SupportsCoreCountEditing = false;
    public const bool SupportsCurveOptimizerEditing = false;
}
