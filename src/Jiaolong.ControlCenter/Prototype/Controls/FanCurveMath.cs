namespace Jiaolong_ControlCenter.Prototype.Controls;

public static class FanCurveMath
{
    public static double[] CreateStableSamples(IReadOnlyList<double> history, double? latest, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        var values = history.Where(double.IsFinite).ToArray();
        var hasLatest = latest is double latestValue && double.IsFinite(latestValue);
        var historyCount = hasLatest ? sampleCount - 1 : sampleCount;
        var selected = values.Length > historyCount
            ? values[^historyCount..]
            : values;

        var result = new double[sampleCount];
        if (selected.Length == 0 && !hasLatest) return [];

        var first = selected.Length > 0 ? selected[0] : latest!.Value;
        Array.Fill(result, first);
        var offset = sampleCount - selected.Length - (hasLatest ? 1 : 0);
        for (var index = 0; index < selected.Length; index++)
            result[offset + index] = selected[index];
        if (hasLatest) result[^1] = latest!.Value;
        return result;
    }

    public static double[] InterpolateSamples(
        IReadOnlyList<double> from,
        IReadOnlyList<double> to,
        double amount)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Count != to.Count)
            throw new ArgumentException("Fan curve sample geometry must remain fixed.", nameof(to));

        var progress = Math.Clamp(amount, 0, 1);
        var result = new double[to.Count];
        for (var index = 0; index < result.Length; index++)
            result[index] = from[index] + ((to[index] - from[index]) * progress);
        return result;
    }

    public static double[] CreateRollingSamples(
        IReadOnlyList<double> history,
        double? latest,
        int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        var values = history.Where(double.IsFinite).ToList();
        if (latest is double latestValue && double.IsFinite(latestValue))
            values.Add(latestValue);
        if (values.Count == 0) return [];

        var selected = values.Count > sampleCount
            ? values.Skip(values.Count - sampleCount).ToArray()
            : values.ToArray();
        var result = new double[sampleCount];
        Array.Fill(result, selected[0]);
        selected.CopyTo(result, sampleCount - selected.Length);
        return result;
    }

    public static double SmoothTowards(double current, double target, double elapsedSeconds, double timeConstantSeconds)
    {
        if (!double.IsFinite(current) || !double.IsFinite(target)) return current;
        if (elapsedSeconds <= 0) return current;
        if (timeConstantSeconds <= 0) return target;
        if (elapsedSeconds >= timeConstantSeconds * 8) return target;

        var amount = 1 - Math.Exp(-elapsedSeconds / timeConstantSeconds);
        return current + ((target - current) * amount);
    }
}
