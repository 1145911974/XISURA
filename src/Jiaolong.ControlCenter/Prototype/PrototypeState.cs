using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jiaolong_ControlCenter.Prototype;

public enum PrototypePerformanceMode
{
    Office,
    Gaming,
    Turbo,
    Custom
}

public sealed record PrototypeModeTheme(
    PrototypePerformanceMode Mode,
    string Name,
    string AccentHex,
    string DeepHex,
    string GlowHex,
    double FlowX,
    double FlowY,
    double TemperatureWallC);

public static class PrototypeModePalette
{
    public static IReadOnlyList<PrototypeModeTheme> All { get; } =
    [
        new(PrototypePerformanceMode.Office, "办公", "#1677FF", "#071D3D", "#3A9DFF", -12, 0, 75),
        new(PrototypePerformanceMode.Gaming, "游戏", "#FF8A1F", "#3A1905", "#FFB24D", 4, -4, 85),
        new(PrototypePerformanceMode.Turbo, "狂飙", "#FF3141", "#3D070C", "#FF5A66", 18, 2, 95),
        new(PrototypePerformanceMode.Custom, "自定义", "#9A5CFF", "#260B45", "#C58AFF", -2, 8, 90)
    ];

    public static PrototypeModeTheme Get(PrototypePerformanceMode mode) => All[(int)mode];
}

public sealed record PrototypeMotionTokens(
    TimeSpan Mode,
    TimeSpan Page,
    TimeSpan Control,
    TimeSpan IndicatorExit,
    TimeSpan IndicatorEnter,
    double Translation,
    double PressScale);

public static class PrototypeMotionProfile
{
    public static PrototypeMotionTokens Resolve(bool reducedMotion) => reducedMotion
        ? new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(80), TimeSpan.Zero, TimeSpan.Zero, 0, 1)
        : new(TimeSpan.FromMilliseconds(260), TimeSpan.FromMilliseconds(190), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(100), 8, 0.98);
}

public sealed record PrototypeBrushTarget(string Key, string Hex, byte Alpha);

public enum ModeIndicatorTransitionPhase
{
    Snap,
    Exit,
    Enter,
}

public sealed record ModeIndicatorTransitionPlan(
    long Version,
    bool IsChild,
    PrototypePerformanceMode? TargetMode,
    string? SelectedChild,
    ModeIndicatorTransitionPhase Phase,
    TimeSpan ExitDuration,
    TimeSpan EnterDuration,
    TimeSpan ChildTotalDuration,
    string TurboTier,
    string CustomProfile)
{
    public TimeSpan PhaseDuration => Phase switch
    {
        ModeIndicatorTransitionPhase.Exit => ExitDuration,
        ModeIndicatorTransitionPhase.Enter => EnterDuration,
        _ => TimeSpan.Zero,
    };

    public bool Snap => Phase == ModeIndicatorTransitionPhase.Snap;
}

public sealed record ModeCardSurfacePlan(
    PrototypePerformanceMode Current,
    PrototypePerformanceMode Target,
    TimeSpan Duration,
    bool Snap)
{
    public static ModeCardSurfacePlan Begin(
        PrototypePerformanceMode current,
        PrototypePerformanceMode target,
        bool animate,
        TimeSpan duration) =>
        new(current, target, animate && duration > TimeSpan.Zero ? duration : TimeSpan.Zero, !animate || duration <= TimeSpan.Zero);

    public double TargetOpacity(PrototypePerformanceMode mode) => mode == Target ? 1 : 0;
}

public sealed class ModeIndicatorTransitionCoordinator
{
    private static readonly TimeSpan ChildHalfDuration = TimeSpan.FromMilliseconds(90);
    private long latestMainVersion;
    private long latestChildVersion;
    private ModeIndicatorTransitionPlan? pendingMain;
    private ModeIndicatorTransitionPlan? pendingChild;
    private string turboTier = "Normal";
    private string customProfile = "Profile1";

    public ModeIndicatorTransitionPlan BeginMode(PrototypePerformanceMode mode, PrototypeMotionTokens motion, bool animate)
    {
        var snap = !animate || motion.IndicatorExit <= TimeSpan.Zero || motion.IndicatorEnter <= TimeSpan.Zero;
        var plan = CreatePlan(++latestMainVersion, isChild: false, mode, null, snap, motion);
        pendingMain = snap ? null : plan;
        return plan;
    }

    public ModeIndicatorTransitionPlan BeginChild(string option, PrototypeMotionTokens motion, bool animate)
    {
        if (option is "Normal" or "Quiet" or "Extreme") turboTier = option;
        else if (option is "Profile1" or "Profile2" or "Profile3") customProfile = option;
        else throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown mode indicator option.");

        var snap = !animate || motion.IndicatorExit <= TimeSpan.Zero || motion.IndicatorEnter <= TimeSpan.Zero;
        var plan = CreatePlan(++latestChildVersion, isChild: true, null, option, snap, motion);
        pendingChild = snap ? null : plan;
        return plan;
    }

    public ModeIndicatorTransitionPlan? CompleteExit(long version) => CompleteMainExit(version);

    public ModeIndicatorTransitionPlan? CompleteMainExit(long version)
    {
        if (pendingMain is not { Phase: ModeIndicatorTransitionPhase.Exit } exit || exit.Version != version) return null;
        var enter = exit with { Phase = ModeIndicatorTransitionPhase.Enter };
        pendingMain = enter;
        return enter;
    }

    public ModeIndicatorTransitionPlan? CompleteChildExit(long version)
    {
        if (pendingChild is not { Phase: ModeIndicatorTransitionPhase.Exit } exit || exit.Version != version) return null;
        var enter = exit with { Phase = ModeIndicatorTransitionPhase.Enter };
        pendingChild = enter;
        return enter;
    }

    public bool CompleteMainEnter(long version) => pendingMain is { Phase: ModeIndicatorTransitionPhase.Enter } enter && enter.Version == version;
    public bool CompleteChildEnter(long version) => pendingChild is { Phase: ModeIndicatorTransitionPhase.Enter } enter && enter.Version == version;
    public bool IsCurrent(ModeIndicatorTransitionPlan plan) => plan.IsChild ? plan.Version == latestChildVersion : plan.Version == latestMainVersion;

    private ModeIndicatorTransitionPlan CreatePlan(
        long version,
        bool isChild,
        PrototypePerformanceMode? mode,
        string? child,
        bool snap,
        PrototypeMotionTokens motion) => new(
            version,
            isChild,
            mode,
            child,
            snap ? ModeIndicatorTransitionPhase.Snap : ModeIndicatorTransitionPhase.Exit,
            snap ? TimeSpan.Zero : isChild ? ChildHalfDuration : motion.IndicatorExit,
            snap ? TimeSpan.Zero : isChild ? ChildHalfDuration : motion.IndicatorEnter,
            snap ? TimeSpan.Zero : TimeSpan.FromMilliseconds(180),
            turboTier,
            customProfile);
}

public sealed class PrototypeState : INotifyPropertyChanged
{
    private PrototypePerformanceMode mode = PrototypePerformanceMode.Office;
    private bool strongCooling;

    public PrototypePerformanceMode Mode
    {
        get => mode;
        set => Set(ref mode, value);
    }

    public bool StrongCooling
    {
        get => strongCooling;
        set => Set(ref strongCooling, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
