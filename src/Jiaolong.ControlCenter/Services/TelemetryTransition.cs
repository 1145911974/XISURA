namespace Jiaolong_ControlCenter.Services;

public sealed class TelemetryTransition
{
    public const double LiveSampleDurationSeconds = 0.5;

    public double? Value { get; private set; }
    public bool IsActive { get; private set; }
    private double? target;
    private double from;
    private double started;
    private double duration;

    public void SetTarget(double? next, double now, double seconds)
    {
        if (next.HasValue && !double.IsFinite(next.Value)) next = null;
        Advance(now);
        if (seconds <= 0 || Value is null || next is null)
        {
            target = Value = next;
            IsActive = false;
            return;
        }
        if (target == next) return;
        from = Value.Value;
        target = next;
        started = now;
        duration = seconds;
        IsActive = from != next;
    }

    public void Advance(double now)
    {
        if (!IsActive) return;
        double t = Math.Clamp((now - started) / duration, 0, 1);
        Value = from + (target!.Value - from) * (1 - Math.Pow(1 - t, 3));
        if (t >= 1) { Value = target; IsActive = false; }
    }
}
