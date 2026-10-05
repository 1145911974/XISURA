using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

internal sealed class AutomaticFanCeiling
{
    private double? cpuEntryC;
    private double? gpuEntryC;
    public void Reset() => cpuEntryC = gpuEntryC = null;

    public bool Update(int maximumRpm, HardwareSnapshot telemetry)
    {
        if (telemetry.CpuFanRpm is not { } cpuRpm || telemetry.GpuFanRpm is not { } gpuRpm ||
            !double.IsFinite(cpuRpm) || !double.IsFinite(gpuRpm) || cpuRpm < 0 || gpuRpm < 0 ||
            telemetry.CpuTemperatureC is not { } cpuC || telemetry.GpuTemperatureC is not { } gpuC ||
            !double.IsFinite(cpuC) || !double.IsFinite(gpuC) || cpuC is < 0 or >= 95 || gpuC is < 0 or >= 87)
        { Reset(); return false; }

        if (cpuEntryC is null && gpuEntryC is null)
        {
            if (cpuRpm > maximumRpm + 100) cpuEntryC = cpuC;
            if (gpuRpm > maximumRpm + 100) gpuEntryC = gpuC;
        }
        // Only intervene above the ceiling; a 3°C drop releases the override without chatter.
        else if ((cpuEntryC is null || cpuC <= cpuEntryC - 3) &&
                 (gpuEntryC is null || gpuC <= gpuEntryC - 3)) Reset();
        return cpuEntryC is not null || gpuEntryC is not null;
    }
}
