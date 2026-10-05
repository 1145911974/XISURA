namespace Jiaolong_ControlCenter.Services;

public static class TelemetryCurveWindow
{
    // Keep coordinates outside the viewport so clipping removes partial segments.
    public static double Position(double sampleSeconds, double now, double windowSeconds) =>
        (sampleSeconds - (now - windowSeconds)) / windowSeconds;

    public static void Trim<T>(List<T> samples, Func<T, double> time, double now,
        double windowSeconds, double holdSeconds = 0)
    {
        double cutoff = now - windowSeconds;
        int expired = 0;
        // The predecessor anchors the segment crossing the left edge.
        while (expired + 1 < samples.Count && time(samples[expired + 1]) <= cutoff)
            expired++;
        if (expired > 0) samples.RemoveRange(0, expired);
        if (samples.Count == 1 && time(samples[0]) + holdSeconds < cutoff)
            samples.Clear();
    }
}
