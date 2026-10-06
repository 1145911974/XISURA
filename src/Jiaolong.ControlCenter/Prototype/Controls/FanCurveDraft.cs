namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed record FanCurveState(int Profile, bool IsShared, CurvePoint[] Cpu, CurvePoint[] Gpu, CurvePoint[] Shared,
    string Strategy = "Curve", int FixedRpm = 3000)
{
    public int? MaximumRpm { get; init; }
    public bool IsValid() => Profile is >= 0 and <= 2 && Valid(Cpu) && Valid(Gpu) && Valid(Shared)
        && Strategy is "Auto" or "Fixed" or "Curve" && FixedRpm is >= 1800 and <= 5800
        && MaximumRpm is not (< 1800 or > 5800);
    private static bool Valid(CurvePoint[]? points) => points is { Length: >= 2 and <= 71 }
        && points.All(p => p.Temperature is >= 30 and <= 100 && p.TargetPercent is >= 0 and <= 100)
        && points.Zip(points.Skip(1)).All(p => p.First.Temperature < p.Second.Temperature);
}

public sealed class FanCurveDraft
{
    public List<CurvePoint> Cpu { get; } = [];
    public List<CurvePoint> Gpu { get; } = [];
    public List<CurvePoint> Shared { get; } = [];
    public bool IsShared { get; set; }
    public int Profile { get; }

    public bool MatchesCurve(FanCurveState state) => Profile == state.Profile && IsShared == state.IsShared
        && Cpu.SequenceEqual(state.Cpu) && Gpu.SequenceEqual(state.Gpu) && Shared.SequenceEqual(state.Shared);

    public FanCurveDraft(int profile)
    {
        Profile = Math.Clamp(profile, 0, 2);
        Reset(false);
        Reset(true);
    }

    // UI recommendations only; hardware validation is required before command integration.
    public static CurvePoint[] Recommended(int profile, bool gpu = false)
    {
        int[] temperatures = [30, 40, 50, 60, 70, 80, 90, 100];
        int[] targets = profile switch
        {
            0 => [15, 18, 24, 34, 48, 68, 90, 100],
            2 => [25, 32, 42, 55, 70, 85, 100, 100],
            _ => [20, 24, 32, 44, 60, 78, 95, 100]
        };
        return temperatures.Select((t, i) => new CurvePoint(t, Math.Min(100, targets[i] + (gpu ? 4 : 0)))).ToArray();
    }

    public List<CurvePoint> Points(FanCurveSeries series) => IsShared ? Shared : series == FanCurveSeries.Cpu ? Cpu : Gpu;

    public void Reset(bool shared)
    {
        if (shared) { Shared.Clear(); Shared.AddRange(Recommended(Profile, true)); }
        else
        {
            Cpu.Clear(); Cpu.AddRange(Recommended(Profile));
            Gpu.Clear(); Gpu.AddRange(Recommended(Profile, true));
        }
    }

    public static void Update(List<CurvePoint> points, int index, int temperature, int target)
    {
        points[index] = new CurvePoint(
            Math.Clamp(temperature, index == 0 ? 30 : points[index - 1].Temperature + 1,
                index == points.Count - 1 ? 100 : points[index + 1].Temperature - 1),
            Math.Clamp(target, 0, 100));
    }

    public static int Add(List<CurvePoint> points, int temperature, int target)
    {
        temperature = Math.Clamp(temperature, 30, 100);
        int existing = points.FindIndex(p => p.Temperature == temperature);
        if (existing >= 0) return existing;
        int index = points.FindIndex(p => p.Temperature > temperature);
        if (index < 0) index = points.Count;
        points.Insert(index, new CurvePoint(temperature, target));
        Update(points, index, temperature, target);
        return index;
    }
}

public sealed record FanCurveWarning(string Series, int StartC, int EndC, int MinimumPercent, int RecommendedPercent);

public static class FanCurveSafety
{
    public static int RecommendedPercent(int profile, int temperature, bool gpu) =>
        (int)Math.Ceiling(TargetAt(FanCurveDraft.Recommended(profile, gpu), temperature));

    public static IReadOnlyList<FanCurveWarning> Assess(FanCurveState state)
    {
        var warnings = new List<FanCurveWarning>();
        if (state.Strategy == "Auto" || !state.IsValid()) return warnings;
        if (state.Strategy == "Curve" && state.IsShared) Check("双扇", state.Shared, true);
        else
        {
            Check("CPU", state.Cpu, false);
            Check("GPU", state.Gpu, true);
        }
        return warnings;

        void Check(string series, CurvePoint[] points, bool gpu)
        {
            var baselinePoints = FanCurveDraft.Recommended(state.Profile, gpu);
            for (int start = 70; start <= 100; start += 10)
            {
                int end = Math.Min(100, start + 9);
                int minimum = 101;
                int recommended = 0;
                for (int temperature = start; temperature <= end; temperature++)
                {
                    // EC targets use 100 RPM steps; curve 0–100% maps to 1800–5800 RPM.
                    double target = state.Strategy == "Fixed"
                        ? ((state.FixedRpm / 100) * 100 - 1800) / 40d
                        : TargetAt(points, temperature);
                    double baseline = TargetAt(baselinePoints, temperature);
                    if (target + 0.001d >= baseline) continue;
                    minimum = Math.Min(minimum, (int)Math.Floor(target));
                    recommended = Math.Max(recommended, (int)Math.Ceiling(baseline));
                }
                if (minimum <= 100)
                    warnings.Add(new FanCurveWarning(series, start, end, minimum, recommended));
            }
        }
    }

    private static double TargetAt(CurvePoint[] points, int temperature)
    {
        if (temperature <= points[0].Temperature) return points[0].TargetPercent;
        for (int index = 1; index < points.Length; index++)
        {
            var next = points[index];
            if (temperature > next.Temperature) continue;
            var previous = points[index - 1];
            return previous.TargetPercent + (next.TargetPercent - previous.TargetPercent)
                * (temperature - previous.Temperature) / (double)(next.Temperature - previous.Temperature);
        }
        return points[^1].TargetPercent;
    }
}
