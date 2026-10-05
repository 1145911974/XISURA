namespace Jiaolong_ControlCenter.Services;

public readonly record struct TelemetryCurveSample(double Seconds, double Value, bool StartsSegment = false);

public sealed record TelemetryCurveFrame(IReadOnlyList<TelemetryCurveSample> Samples,
    double? HeadValue, double? HeadSeconds, double HeadOpacity);

public sealed class TelemetryCurveSeries(double windowSeconds)
{
    private const double HoldSeconds = 2;
    private const double SegmentGapSeconds = 3;
    private readonly List<TelemetryCurveSample> history = [];
    private readonly TelemetryTransition head = new();
    private double? target;
    private double lastSampleSeconds = double.NegativeInfinity;
    private DateTimeOffset? lastObservedAtUtc;
    private bool available;

    public bool UpdateSample(double? value, double now, bool animate, DateTimeOffset? observedAtUtc = null)
    {
        double sampleGap = available ? now - lastSampleSeconds : double.PositiveInfinity;
        if (observedAtUtc is { } observed)
        {
            if (lastObservedAtUtc is { } last)
            {
                sampleGap = (observed - last).TotalSeconds;
                if (sampleGap <= 0) return false;
            }
            lastObservedAtUtc = observed;
        }

        Advance(now, animate);
        if (head.Value is double visible && available)
            Commit(Math.Min(now, lastSampleSeconds + HoldSeconds), visible);

        if (value is not double next || !double.IsFinite(next))
        {
            target = head.Value;
            head.SetTarget(target, now, 0);
            available = false;
            return true;
        }

        bool startsSegment = !available || sampleGap > SegmentGapSeconds;
        if (startsSegment)
        {
            head.SetTarget(null, now, 0);
            Commit(now, next, true);
        }
        target = next;
        head.SetTarget(next, now, animate ? TelemetryTransition.LiveSampleDurationSeconds : 0);
        lastSampleSeconds = now;
        available = true;
        return true;
    }

    public TelemetryCurveFrame GetFrame(double now, bool animate)
    {
        Advance(now, animate);
        TelemetryCurveWindow.Trim(history, sample => sample.Seconds, now, windowSeconds, HoldSeconds);
        var samples = new List<TelemetryCurveSample>(history);
        double endSeconds = available ? Math.Min(now, lastSampleSeconds + HoldSeconds)
            : history.Count > 0 ? history[^1].Seconds : double.NegativeInfinity;
        if (head.Value is double value && endSeconds >= now - windowSeconds)
        {
            var end = new TelemetryCurveSample(endSeconds, value);
            if (samples.Count > 0 && endSeconds == samples[^1].Seconds)
                samples[^1] = end with { StartsSegment = samples[^1].StartsSegment };
            else if (samples.Count > 0)
                samples.Add(end);
        }
        double opacity = available ? Math.Clamp((HoldSeconds - (now - lastSampleSeconds)) / .5, 0, 1) : 0;
        return new(samples, available ? head.Value : null, available ? endSeconds : null, opacity);
    }

    private void Advance(double now, bool animate)
    {
        if (!animate && target.HasValue) head.SetTarget(target, now, 0);
        else head.Advance(now);
    }

    private void Commit(double seconds, double value, bool startsSegment = false)
    {
        var sample = new TelemetryCurveSample(seconds, value, startsSegment);
        if (history.Count > 0 && history[^1].Seconds == seconds)
            history[^1] = sample with { StartsSegment = startsSegment || history[^1].StartsSegment };
        else history.Add(sample);
    }
}
