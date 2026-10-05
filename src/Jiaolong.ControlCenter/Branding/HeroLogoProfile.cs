using System.Text.Json.Serialization;

namespace Jiaolong_ControlCenter.Branding;

public sealed record HeroLogoProfile(
    [property: JsonRequired] double Left,
    [property: JsonRequired] double Top,
    [property: JsonRequired] double Size,
    [property: JsonRequired] double CrystalOpacity,
    [property: JsonRequired] double ReflectionOpacity,
    [property: JsonRequired] int TransitionMilliseconds)
{
    public static HeroLogoProfile Default { get; } = new(124.5, 70, 360, 1, 1, 420);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!double.IsFinite(Left) || Left is < -609 or > 609) errors.Add("left must be finite and between -609 and 609");
        if (!double.IsFinite(Top) || Top is < -835 or > 835) errors.Add("top must be finite and between -835 and 835");
        if (!double.IsFinite(Size) || Size is < 160 or > 609) errors.Add("size must be finite and between 160 and 609");
        if (!double.IsFinite(CrystalOpacity) || CrystalOpacity is < 0 or > 1) errors.Add("crystalOpacity must be finite and between 0 and 1");
        if (!double.IsFinite(ReflectionOpacity) || ReflectionOpacity is < 0 or > 1) errors.Add("reflectionOpacity must be finite and between 0 and 1");
        if (TransitionMilliseconds is < 0 or > 2000) errors.Add("transitionMilliseconds must be between 0 and 2000");
        return errors;
    }
}

public sealed record HeroLogoProfileLoadResult(HeroLogoProfile Profile, string? Error);
