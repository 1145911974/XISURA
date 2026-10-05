using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CpuPresetThermalPolicyTests
{
    [TestMethod]
    public void Cooling_and_unchanged_presets_remain_available_without_thermal_headroom()
    {
        var plan = new CpuTuningPlan(85, 55, 65, null, null, null, null, null);
        int? Current(CpuTuningField field) => field switch
        {
            CpuTuningField.TemperatureLimitC => 85,
            CpuTuningField.SplWatts => 55,
            _ => 65
        };
        Assert.IsFalse(CpuPresetThermalPolicy.RequiresHeadroom(plan, Current));
        Assert.IsFalse(CpuPresetThermalPolicy.RequiresHeadroom(plan with { TemperatureLimitC = 75, SplWatts = 45, SpptWatts = 55 }, Current));
        Assert.IsFalse(CpuPresetThermalPolicy.RequiresHeadroom(plan with { TemperatureLimitC = null, SplWatts = null, SpptWatts = null }, _ => throw new InvalidOperationException()));
    }

    [TestMethod]
    public void Any_increase_or_unknown_baseline_still_requires_thermal_headroom()
    {
        var plan = new CpuTuningPlan(75, 55, 65, null, null, null, null, null);
        foreach (var changed in new[] {
            plan with { TemperatureLimitC = 86 },
            plan with { SplWatts = 66 },
            plan with { SpptWatts = 76 } })
            Assert.IsTrue(CpuPresetThermalPolicy.RequiresHeadroom(changed, field => field == CpuTuningField.TemperatureLimitC ? 85 : field == CpuTuningField.SplWatts ? 65 : 75));
        Assert.IsTrue(CpuPresetThermalPolicy.RequiresHeadroom(plan, _ => null));
    }
}
