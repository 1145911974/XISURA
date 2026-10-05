namespace Jiaolong_ControlCenter.Prototype;

public enum ThermalDeviceKind
{
    Cpu,
    Gpu
}

public readonly record struct ThermalVisualState(
    bool IsAvailable,
    string DisplayText,
    double Heat,
    double PrimaryOpacity,
    double SecondaryOpacity,
    double TertiaryOpacity,
    double Scale)
{
    public static ThermalVisualState Resolve(ThermalDeviceKind device, double? temperatureC)
    {
        if (temperatureC is not double value || !double.IsFinite(value))
            return new(false, "-- °C", 0, device == ThermalDeviceKind.Cpu ? .08 : .12, 0, 0, device == ThermalDeviceKind.Cpu ? .94 : .58);

        var (minimum, maximum) = device == ThermalDeviceKind.Cpu ? (30d, 95d) : (30d, 90d);
        var heat = SmoothStep(Math.Clamp((value - minimum) / (maximum - minimum), 0, 1));

        return device == ThermalDeviceKind.Cpu
            ? new(true, $"{value:0} °C", heat, Lerp(.12, .92, heat), Lerp(.03, .68, heat), Lerp(.01, .42, heat), Lerp(.94, 1, heat))
            : new(true, $"{value:0} °C", heat, Lerp(.18, .96, heat), SmoothStepRange(heat, .25, 1) * .82, SmoothStepRange(heat, .60, 1) * .68, Lerp(.58, 1, heat));
    }

    private static double SmoothStep(double value) => value * value * (3 - (2 * value));

    private static double SmoothStepRange(double value, double start, double end) =>
        SmoothStep(Math.Clamp((value - start) / (end - start), 0, 1));

    private static double Lerp(double start, double end, double amount) => start + ((end - start) * amount);
}
