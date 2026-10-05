using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype;

public sealed class ModeColorTransitionController
{
    private Color[] from = [];
    private Color[] target = [];

    public void Begin(IReadOnlyList<Color> current, IReadOnlyList<Color> target)
    {
        if (current.Count != target.Count)
            throw new ArgumentException("Color transition lists must have the same length.");

        from = current.ToArray();
        this.target = target.ToArray();
    }

    public IReadOnlyList<Color> Sample(double progress)
    {
        var amount = Math.Clamp(progress, 0, 1);
        var sample = new Color[from.Length];
        for (var index = 0; index < sample.Length; index++)
            sample[index] = Lerp(from[index], target[index], amount);
        return sample;
    }

    private static Color Lerp(Color from, Color target, double amount) => Color.FromArgb(
        Channel(from.A, target.A, amount),
        Channel(from.R, target.R, amount),
        Channel(from.G, target.G, amount),
        Channel(from.B, target.B, amount));

    private static byte Channel(byte from, byte target, double amount) =>
        (byte)Math.Round(from + ((target - from) * amount));
}
