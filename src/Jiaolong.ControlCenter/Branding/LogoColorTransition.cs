namespace Jiaolong_ControlCenter.Branding;

public sealed class LogoColorTransition
{
    public double[] Weights { get; } = [1, 0, 0, 0, 0, 0];
    private double[] from = [1, 0, 0, 0, 0, 0];
    private int selected;
    private double started;
    private double duration;
    public bool IsActive { get; private set; }

    public void WriteSourceOverOpacities(Span<double> destination)
    {
        if (destination.Length < Weights.Length)
            throw new ArgumentException("A destination for all six backgrounds is required.", nameof(destination));
        double cumulative = 0;
        for (int i = 0; i < Weights.Length; i++)
        {
            cumulative += Weights[i];
            destination[i] = cumulative > 0 ? Weights[i] / cumulative : 0;
        }
    }

    public void Select(int target, double now, double seconds, bool advance = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(target, Weights.Length);
        if (advance) Advance(now);
        if (seconds <= 0)
        {
            selected = target;
            for (int i = 0; i < Weights.Length; i++) Weights[i] = i == selected ? 1 : 0;
            IsActive = false;
            return;
        }
        if (selected == target) return;
        from = Weights.ToArray();
        selected = target;
        started = now;
        duration = seconds;
        IsActive = true;
    }

    public void Advance(double now)
    {
        if (!IsActive) return;
        double t = Math.Clamp((now - started) / duration, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        for (int i = 0; i < Weights.Length; i++)
            Weights[i] = from[i] * (1 - eased) + (i == selected ? eased : 0);
        IsActive = t < 1;
    }
}
