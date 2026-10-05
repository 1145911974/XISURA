namespace Jiaolong_ControlCenter.Prototype;

public sealed record TurboBackgroundIntensity(
    float DisplacementScale,
    double ShadeOpacity,
    double GlowOpacity)
{
    public static TurboBackgroundIntensity Resolve(string tier) => tier switch
    {
        "Normal" => new(1, .03, .06),
        "Quiet" => new(.68f, .13, .025),
        "Extreme" => new(1.28f, 0, .16),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown turbo tier."),
    };
}
