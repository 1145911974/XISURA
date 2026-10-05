namespace Jiaolong.Hardware.Mechrevo.Telemetry;

public sealed class CpuPackagePowerCalculator
{
    private bool hasPreviousSample;
    private uint previousEnergy;
    private DateTimeOffset previousSampleAt;

    public double? Update(ulong powerUnitRegister, ulong energyRegister, DateTimeOffset sampleAt)
    {
        var exponent = (int)((powerUnitRegister >> 8) & 0x1F);
        var energy = (uint)energyRegister;
        if (!hasPreviousSample)
        {
            Remember(energy, sampleAt);
            return null;
        }

        var elapsed = sampleAt - previousSampleAt;
        var delta = previousEnergy <= energy
            ? energy - previousEnergy
            : (uint.MaxValue - previousEnergy) + energy;
        Remember(energy, sampleAt);
        if (elapsed <= TimeSpan.Zero) return null;

        var watts = Math.Pow(0.5, exponent) * delta / elapsed.TotalSeconds;
        return double.IsFinite(watts) && watts >= 0 ? watts : null;
    }

    private void Remember(uint energy, DateTimeOffset sampleAt)
    {
        previousEnergy = energy;
        previousSampleAt = sampleAt;
        hasPreviousSample = true;
    }
}
