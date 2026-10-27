using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CpuRangeBoundaryTests
{
    private static CpuTuningPlan Empty => new(null, null, null, null, null, null, null, null);

    private static void Validate(CpuTuningPlan plan, bool expected)
    {
        Assert.AreEqual(expected, CommandValidation.Validate(new SetCpuTuningCommand(Guid.NewGuid(), plan, true)) is null);
        Assert.AreEqual(expected, CommandValidation.Validate(new SetCpuTuningBatchCommand(Guid.NewGuid(), [plan], true)) is null);
    }

    [TestMethod]
    public void Current_commands_match_single_field_transport_bounds()
    {
        foreach (int value in new[] { 0, 999, 1000, 200_000, 200_001 })
        foreach (var advanced in new[] {
            new AdvancedCpuTuningPlan(VrmCurrentMilliamps: value),
            new AdvancedCpuTuningPlan(TdcCurrentMilliamps: value),
            new AdvancedCpuTuningPlan(EdcCurrentMilliamps: value) })
        {
            bool supported = value is >= 1000 and <= 200_000;
            Validate(Empty with { Advanced = advanced }, supported);
            Assert.AreEqual(supported, RyzenSmuAdvancedLimits.BuildSingle(advanced) is not null);
        }
    }

    [TestMethod]
    public void Temperature_commands_match_native_and_smu_temperature_bounds()
    {
        foreach (int value in new[] { 39, 40, 100, 101, 110, 115 })
        {
            bool supported = value is >= 40 and <= 100;
            Validate(Empty with { TemperatureLimitC = value }, supported);
            foreach (var advanced in new[] {
                new AdvancedCpuTuningPlan(Mp1TemperatureC: value),
                new AdvancedCpuTuningPlan(RsmuTemperatureC: value) })
            {
                Validate(Empty with { Advanced = advanced }, supported);
                Assert.AreEqual(supported, RyzenSmuAdvancedLimits.BuildSingle(advanced) is not null);
            }
        }
    }

    [TestMethod]
    public void Wider_power_and_windows_contracts_remain_available_for_captured_values()
    {
        Validate(Empty with { SplWatts = 82, SpptWatts = 108,
            AcMaxFrequencyMhz = 6000, DcMaxFrequencyMhz = 0 }, true);
        var captured = new AdvancedCpuTuningPlan(StapmWatts: 73.8, FastPptWatts: 108,
            SlowPptWatts: 73.8, PptWatts: 108);
        Validate(Empty with { Advanced = captured }, true);
        Assert.IsNull(RyzenSmuAdvancedLimits.BuildSingle(new(FastPptWatts: 108)),
            "A broad recovery contract must not enable manual writes outside the transport policy.");
        Assert.IsNotNull(RyzenSmuAdvancedLimits.BuildSingle(new(FastPptWatts: 75)));
    }
}
