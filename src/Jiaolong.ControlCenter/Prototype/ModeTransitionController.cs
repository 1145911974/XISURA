namespace Jiaolong_ControlCenter.Prototype;

public enum ModeTransitionKind
{
    FlowMorph,
    AngularMorph,
    VolumeMorph,
    FlowAngular,
    FlowVolume,
    AngularVolume,
}

public sealed record ModeTransitionRoute(
    ModeTransitionKind Kind,
    TimeSpan Duration,
    float Displacement,
    float RevealSoftness,
    float NoiseWeight,
    bool Reverse,
    bool Snap)
{
    public static ModeTransitionRoute Resolve(
        ModeVisualScene? current,
        ModeVisualScene target,
        bool reducedMotion,
        TimeSpan? duration = null)
    {
        var route = current is null
            ? new ModeTransitionRoute(ModeTransitionKind.FlowMorph, TimeSpan.Zero, 0, 0, 0, false, true)
            : CreateRoute(current.Family, target.Family);

        return reducedMotion || current is null || current.Key == target.Key
            ? route with { Duration = TimeSpan.Zero, Snap = true }
            : route with { Duration = duration ?? route.Duration };
    }

    private static ModeTransitionRoute CreateRoute(ModeSceneFamily from, ModeSceneFamily to) =>
        from == to
            ? from switch
            {
                ModeSceneFamily.Flow => Preset(ModeTransitionKind.FlowMorph, 240, .08f, .65f, .12f),
                ModeSceneFamily.Angular => Preset(ModeTransitionKind.AngularMorph, 240, .05f, .55f, .08f),
                ModeSceneFamily.Volume => Preset(ModeTransitionKind.VolumeMorph, 240, .02f, .70f, .20f),
                _ => throw new ArgumentOutOfRangeException(nameof(from)),
            }
            : (int)from < (int)to
                ? Preset(CrossKind(from, to), DurationFor(from, to), .07f, .68f, .16f)
                : Preset(CrossKind(to, from), DurationFor(to, from), .07f, .68f, .16f) with { Reverse = true };

    private static ModeTransitionRoute Preset(
        ModeTransitionKind kind,
        int milliseconds,
        float displacement,
        float revealSoftness,
        float noiseWeight) =>
        new(kind, TimeSpan.FromMilliseconds(milliseconds), displacement, revealSoftness, noiseWeight, false, false);

    private static int DurationFor(ModeSceneFamily from, ModeSceneFamily to) =>
        from == ModeSceneFamily.Flow && to == ModeSceneFamily.Angular ? 340 : 320;

    private static ModeTransitionKind CrossKind(ModeSceneFamily from, ModeSceneFamily to) =>
        (from, to) switch
        {
            (ModeSceneFamily.Flow, ModeSceneFamily.Angular) => ModeTransitionKind.FlowAngular,
            (ModeSceneFamily.Flow, ModeSceneFamily.Volume) => ModeTransitionKind.FlowVolume,
            (ModeSceneFamily.Angular, ModeSceneFamily.Volume) => ModeTransitionKind.AngularVolume,
            _ => throw new ArgumentOutOfRangeException(),
        };
}

public sealed record ModeTransitionPlan(
    long Version,
    ModeVisualScene? Current,
    ModeVisualScene Target,
    ModeTransitionRoute Route,
    IReadOnlyList<PrototypeBrushTarget> BrushTargets)
{
    public int MaximumLiveVisuals => 2;
    public bool Snap => Route.Snap;
    public TimeSpan Duration => Route.Duration;
    public bool ShouldAnimateVisuals => !Route.Snap;
}

public readonly record struct ModeTransitionFrame(
    float CurrentOpacity,
    float TargetOpacity,
    float CurrentMaterialOpacity,
    float TargetMaterialOpacity,
    float CurrentDisplacement,
    float TargetDisplacement,
    float CurrentReveal,
    float TargetReveal,
    float RevealSoftness,
    float NoiseWeight)
{
    public static ModeTransitionFrame Resolve(ModeTransitionRoute route, float progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped <= 0) return new(1, 0, 0, 0, 0, 0, 1, 0, route.RevealSoftness, route.NoiseWeight);
        if (clamped >= 1) return new(0, 1, 0, 0, 0, 0, 0, 1, route.RevealSoftness, route.NoiseWeight);

        // The target becomes legible immediately, then uses the remaining time to settle.
        var eased = 1 - ((1 - clamped) * (1 - clamped));
        var angle = eased * MathF.PI / 2;
        var currentOpacity = MathF.Pow(MathF.Cos(angle), 2);
        var targetOpacity = MathF.Pow(MathF.Sin(angle), 2);
        var materialPulse = .22f * MathF.Sin(clamped * MathF.PI);
        var direction = route.Reverse ? -1 : 1;
        var currentPhase = Math.Clamp(eased / .90f, 0, 1);
        var targetPhase = Math.Clamp((eased - .18f) / .82f, 0, 1);
        var amplitude = route.Displacement * 210;
        var currentDisplacement = direction * amplitude * MathF.Sin(currentPhase * MathF.PI) * (1 - (.25f * eased));
        var targetDisplacement = -direction * amplitude * MathF.Sin(targetPhase * MathF.PI) * (.72f + (.28f * eased));
        var currentReveal = 1 - SmoothStep(Math.Clamp((clamped - .15f) / .85f, 0, 1));
        var targetReveal = SmoothStep(Math.Clamp(clamped / .85f, 0, 1));

        return new(
            currentOpacity,
            targetOpacity,
            materialPulse * currentOpacity,
            materialPulse * targetOpacity,
            currentDisplacement,
            targetDisplacement,
            currentReveal,
            targetReveal,
            route.RevealSoftness,
            route.NoiseWeight);
    }

    private static float SmoothStep(float value) => value * value * (3 - (2 * value));
}

public sealed class ModeTransitionController
{
    private readonly ModeMotionProfile profile;
    private long latestVersion;

    public ModeTransitionController(ModeMotionProfile? profile = null)
    {
        this.profile = profile ?? ModeMotionProfile.Final;
    }

    public ModeTransitionPlan Begin(
        ModeVisualScene? current,
        ModeVisualScene target,
        PrototypeMotionTokens motion,
        bool animate = true)
    {
        var route = ModeTransitionRoute.Resolve(
            current,
            target,
            motion.Translation <= 0 || !animate,
            profile.BackgroundDuration);
        return new ModeTransitionPlan(
            ++latestVersion,
            current,
            target,
            route,
            [
                new("ModeAccentBrush", target.Theme.AccentHex, 0xFF),
                new("ModeQuickActiveTintBrush", target.Theme.AccentHex, 0x32),
                new("ModeGlowBrush", target.Theme.GlowHex, 0xFF),
                new("ModeDeepBrush", target.Theme.DeepHex, 0xFF),
                new("ModeSelectionAccentBrush", target.Theme.AccentHex, 0x72),
                new("ModeNavSelectionBrush", target.Theme.AccentHex, 0x4A),
                new("ModePanelBrush", target.Theme.AccentHex, 0x24),
                new("ModeAccentFadeBrush", target.Theme.AccentHex, 0x00),
                new("ModeTraceTailBrush", target.Theme.AccentHex, 0x55),
            ]);
    }

    public bool IsCurrent(long version) => version == latestVersion;
}
