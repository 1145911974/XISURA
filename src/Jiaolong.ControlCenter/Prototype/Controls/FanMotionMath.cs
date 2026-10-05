namespace Jiaolong_ControlCenter.Prototype.Controls;

public static class FanMotionMath
{
    public const double MaxRpm = 7000d;
    public static double RotationDistance(double fromSpeed, double toSpeed, double elapsed, double rampSeconds)
    {
        elapsed = Math.Max(0, elapsed);
        double ramp = Math.Min(elapsed, rampSeconds);
        return fromSpeed * ramp + (toSpeed - fromSpeed) * ramp * ramp / (2 * rampSeconds)
            + toSpeed * Math.Max(0, elapsed - rampSeconds);
    }

    public static double MapRpmToRevolutionsPerSecond(double? rpm)
    {
        if (rpm is not double value || !double.IsFinite(value) || value <= 0) return 0;
        // Continuous visual speed: small RPM changes remain visible across the full scale.
        return .12 + 3.08 * Math.Pow(Math.Min(value, MaxRpm) / MaxRpm, 1.25);
    }

    public static double NormalizeFanStrength(double? rpm) => rpm is double value && double.IsFinite(value)
        ? Math.Clamp(value / MaxRpm, 0d, 1d)
        : 0d;

    public static int MapParticleCount(double strength)
    {
        if (!double.IsFinite(strength) || strength <= 0) return 0;
        return 8 + (int)Math.Round(Math.Clamp(strength, 0d, 1d) * 10d, MidpointRounding.AwayFromZero);
    }

    public static double SmoothToward(double current, double target, double deltaSeconds, double responseSeconds)
    {
        if (deltaSeconds <= 0) return current;
        double blend = 1d - Math.Exp(-deltaSeconds / Math.Max(.001d, responseSeconds));
        return current + (target - current) * blend;
    }

}
