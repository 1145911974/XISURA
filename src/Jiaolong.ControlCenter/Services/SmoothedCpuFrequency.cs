namespace Jiaolong_ControlCenter.Services;

public sealed class SmoothedCpuFrequency
{
    private double? currentMhz;

    public double? Update(double? measuredMhz)
    {
        if (measuredMhz is not double measured || !double.IsFinite(measured) || measured < 0)
        {
            currentMhz = null;
            return null;
        }

        currentMhz = currentMhz is double previous
            ? previous + (measured - previous) * 0.35d
            : measured;
        return Math.Round(currentMhz.Value / 100d, MidpointRounding.AwayFromZero) * 100d;
    }
}
