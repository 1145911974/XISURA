namespace Jiaolong_ControlCenter.Prototype;

public enum ModeSceneKind
{
    OrbitalFocus,
    ConstellationFlow,
    FacetArena,
    VolumetricCloud,
    DigitalFaultPlanes,
    HexPressureCells,
    GraphiteStrata,
    LiquidJade,
    PurpleFilaments,
}

public enum ModeSceneFamily
{
    Flow,
    Angular,
    Volume,
}

public sealed record ModeVisualScene(
    string Key,
    ModeSceneKind Kind,
    ModeSceneFamily Family,
    PrototypePerformanceMode Mode,
    string? CustomProfile,
    string PlateAsset,
    string GuideAsset,
    PrototypeModeTheme Theme);

public static class ModeVisualCatalog
{
    private static ModeVisualScene Scene(
        string key,
        ModeSceneKind kind,
        ModeSceneFamily family,
        PrototypePerformanceMode mode,
        string? customProfile,
        PrototypeModeTheme theme)
    {
        var assetFolder = key.Replace('-', '_');
        return new(
            key,
            kind,
            family,
            mode,
            customProfile,
            $"ms-appx:///Assets/ModeScenes/{assetFolder}/plate-16x10.png",
            $"ms-appx:///Assets/ModeScenes/{assetFolder}/guide.png",
            theme);
    }

    public static IReadOnlyList<ModeVisualScene> All { get; } =
    [
        Scene("office-orbital-focus", ModeSceneKind.OrbitalFocus, ModeSceneFamily.Flow, PrototypePerformanceMode.Office, null, PrototypeModePalette.Get(PrototypePerformanceMode.Office)),
        Scene("office-constellation-flow", ModeSceneKind.ConstellationFlow, ModeSceneFamily.Flow, PrototypePerformanceMode.Office, null, PrototypeModePalette.Get(PrototypePerformanceMode.Office)),
        Scene("game-facet-arena", ModeSceneKind.FacetArena, ModeSceneFamily.Angular, PrototypePerformanceMode.Gaming, null, PrototypeModePalette.Get(PrototypePerformanceMode.Gaming)),
        Scene("game-volumetric-cloud", ModeSceneKind.VolumetricCloud, ModeSceneFamily.Volume, PrototypePerformanceMode.Gaming, null, PrototypeModePalette.Get(PrototypePerformanceMode.Gaming)),
        Scene("turbo-digital-fault-planes", ModeSceneKind.DigitalFaultPlanes, ModeSceneFamily.Angular, PrototypePerformanceMode.Turbo, null, PrototypeModePalette.Get(PrototypePerformanceMode.Turbo)),
        Scene("turbo-hex-pressure-cells", ModeSceneKind.HexPressureCells, ModeSceneFamily.Angular, PrototypePerformanceMode.Turbo, null, PrototypeModePalette.Get(PrototypePerformanceMode.Turbo)),
        Scene("custom-preset-1-graphite-strata", ModeSceneKind.GraphiteStrata, ModeSceneFamily.Angular, PrototypePerformanceMode.Custom, "Profile1",
            new(PrototypePerformanceMode.Custom, "自定义", "#8C949F", "#15181D", "#D8DEE8", -2, 2, 90)),
        Scene("custom-preset-2-liquid-jade", ModeSceneKind.LiquidJade, ModeSceneFamily.Flow, PrototypePerformanceMode.Custom, "Profile2",
            new(PrototypePerformanceMode.Custom, "自定义", "#27D980", "#062A1C", "#72FFC0", 3, -3, 90)),
        Scene("custom-preset-3-purple-filaments", ModeSceneKind.PurpleFilaments, ModeSceneFamily.Flow, PrototypePerformanceMode.Custom, "Profile3",
            PrototypeModePalette.Get(PrototypePerformanceMode.Custom)),
    ];

    public static ModeVisualScene ForKey(string key) =>
        All.Single(scene => scene.Key == key);

    public static IReadOnlyList<ModeVisualScene> ForMode(PrototypePerformanceMode mode) =>
        All.Where(scene => scene.Mode == mode && scene.CustomProfile is null).ToArray();

    public static ModeVisualScene ForCustomProfile(string profile) =>
        All.Single(scene => scene.CustomProfile == profile);
}
