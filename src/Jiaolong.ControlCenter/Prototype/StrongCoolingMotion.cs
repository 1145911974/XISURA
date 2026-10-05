namespace Jiaolong_ControlCenter.Prototype;

public readonly record struct StrongCoolingMotionState(
    double LayerOpacity,
    double AmbientOpacity,
    TimeSpan LayerDuration,
    TimeSpan Stagger,
    TimeSpan Duration,
    bool Enabled);

public static class StrongCoolingMotion
{
    public static StrongCoolingMotionState Resolve(bool enabled, bool reducedMotion)
    {
        var layerDuration = reducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(enabled ? 90 : 70);
        var stagger = reducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(enabled ? 42 : 28);
        return new(
            enabled ? 1 : 0,
            enabled ? .18 : 0,
            layerDuration,
            stagger,
            layerDuration + stagger * 7,
            enabled);
    }
}
