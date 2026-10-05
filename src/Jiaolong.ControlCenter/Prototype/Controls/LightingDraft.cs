using System.Globalization;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed record LightingDraft(string Effect, int Brightness, string Hex, bool Logo)
{
    public static LightingDraft Default => new("Gradient", 2, "#00D7E8", true);
    public double Speed { get; init; } = 1;
    public static LightingDraft? FromPlan(Jiaolong.Contracts.Models.KeyboardLightingPlan? plan) =>
        plan is { Red: byte red, Green: byte green, Blue: byte blue } && plan.IsValid()
            ? new(plan.Effect == "Fixed" ? "Static" : plan.Effect,
                plan.BrightnessLevel ?? (int)Math.Round(plan.Brightness * 3d / 100),
                new LightingColor(red, green, blue).Hex, plan.LogoEnabled ?? false) { Speed = plan.Speed }
            : null;
    public bool IsValid() => Effect is "Static" or "Gradient" or "Cycle" && Brightness is >= 0 and <= 3 && LightingColor.TryParse(Hex, out _) && double.IsFinite(Speed) && Speed is >= .5 and <= 2;
    public LightingColor PreviewColor(double seconds)
    {
        var color = ToPlan().ColorAt(seconds);
        return new(color.Red, color.Green, color.Blue);
    }

    public Jiaolong.Contracts.Models.KeyboardLightingPlan ToPlan()
    {
        LightingColor.TryParse(Hex, out var color);
        return new(Effect, (int)Math.Round(Brightness * 100d / 3))
        {
            Red = color.R, Green = color.G, Blue = color.B,
            BrightnessLevel = Brightness, LogoEnabled = Logo, Speed = Speed
        };
    }
}

public readonly record struct LightingColor(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static bool TryParse(string? text, out LightingColor color)
    {
        var value = text?.Trim();
        if (value?.StartsWith('#') == true) value = value[1..];
        color = default;
        if (value?.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255d, g = G / 255d, b = B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double h = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return ((h + 360) % 360, max == 0 ? 0 : delta / max, max);
    }
    public static LightingColor FromHsv(double hue, double saturation, double value)
    {
        double h = ((hue % 360) + 360) % 360 / 60, s = Math.Clamp(saturation, 0, 1), v = Math.Clamp(value, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
        return new((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
