namespace Jiaolong_ControlCenter.Prototype;

public sealed record ModeMotionProfile(
    TimeSpan CardDuration,
    TimeSpan BackgroundDuration)
{
    public static ModeMotionProfile Final { get; } =
        new(TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(420));
}
