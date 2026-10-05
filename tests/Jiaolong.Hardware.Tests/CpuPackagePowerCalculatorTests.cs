using Jiaolong.Hardware.Mechrevo.Telemetry;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CpuPackagePowerCalculatorTests
{
    [TestMethod]
    public void First_sample_is_only_a_baseline()
    {
        var calculator = new CpuPackagePowerCalculator();

        Assert.IsNull(calculator.Update(16UL << 8, 10, DateTimeOffset.UnixEpoch));
    }

    [TestMethod]
    public void Energy_counter_delta_is_converted_to_watts()
    {
        var calculator = new CpuPackagePowerCalculator();
        var start = DateTimeOffset.UnixEpoch;

        calculator.Update(16UL << 8, 10, start);
        var watts = calculator.Update(16UL << 8, 6_553_610, start.AddSeconds(1));

        Assert.IsTrue(watts.HasValue);
        Assert.AreEqual(100d, watts.Value, 0.0001d);
    }

    [TestMethod]
    public void Energy_counter_wrap_uses_the_unsigned_32_bit_delta()
    {
        var calculator = new CpuPackagePowerCalculator();
        var start = DateTimeOffset.UnixEpoch;

        calculator.Update(16UL << 8, uint.MaxValue - 10, start);
        var watts = calculator.Update(16UL << 8, 10, start.AddSeconds(1));

        Assert.IsTrue(watts.HasValue);
        Assert.AreEqual(20d / 65_536d, watts.Value, 0.0001d);
    }
}
