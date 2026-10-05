namespace Jiaolong.Contracts.Models;

public readonly record struct KeyboardRgb(byte Red, byte Green, byte Blue);

// Brightness retains the legacy percentage field; new clients send the exact OEM level too.
public sealed record KeyboardLightingPlan(string Effect, int Brightness)
{
    public byte? Red { get; init; }
    public byte? Green { get; init; }
    public byte? Blue { get; init; }
    public int? BrightnessLevel { get; init; }
    public bool? LogoEnabled { get; init; }
    public double Speed { get; init; } = 1;

    public bool IsValid() => Effect is "Static" or "Fixed" or "Gradient" or "Cycle" &&
        Brightness is >= 0 and <= 100 && BrightnessLevel is not (< 0 or > 3) &&
        double.IsFinite(Speed) && Speed is >= .5 and <= 2 &&
        (Red.HasValue == Green.HasValue && Green.HasValue == Blue.HasValue);

    public KeyboardRgb ColorAt(double seconds)
    {
        var origin = new KeyboardRgb(Red ?? 255, Green ?? 255, Blue ?? 255);
        // Firmware owns the cycle palette and timing; this value is its stored color, not a simulated frame.
        if (Effect != "Gradient") return origin;
        double r = origin.Red / 255d, g = origin.Green / 255d, b = origin.Blue / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        double time = Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * Speed;
        double advance = time * 60;
        delta = Math.Max(max, .35);
        min = 0;
        double h = (((hue + advance) % 360) + 360) % 360 / 60;
        double x = delta * (1 - Math.Abs(h % 2 - 1));
        (r, g, b) = h switch { < 1 => (delta, x, 0d), < 2 => (x, delta, 0d), < 3 => (0d, delta, x), < 4 => (0d, x, delta), < 5 => (x, 0d, delta), _ => (delta, 0d, x) };
        return new((byte)Math.Round((r + min) * 255), (byte)Math.Round((g + min) * 255), (byte)Math.Round((b + min) * 255));
    }
}
