using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public static class CpuPresetThermalPolicy
{
    public static bool RequiresHeadroom(CpuTuningPlan plan, Func<CpuTuningField, int?> readCurrent)
    {
        foreach (var (field, requested) in new[] {
            (CpuTuningField.TemperatureLimitC, plan.TemperatureLimitC),
            (CpuTuningField.SplWatts, plan.SplWatts),
            (CpuTuningField.SpptWatts, plan.SpptWatts) })
            if (requested is int target && (readCurrent(field) is not int current || target > current))
                return true;
        return false;
    }
}
