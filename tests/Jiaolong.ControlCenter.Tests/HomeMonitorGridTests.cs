using Jiaolong_ControlCenter.Prototype.Controls;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class HomeMonitorGridTests
{
    [TestMethod]
    public void Fan_curve_samples_keep_a_fixed_geometry_and_pad_the_history()
    {
        var samples = FanCurveMath.CreateStableSamples([1200, 1800], 2400, 5);

        CollectionAssert.AreEqual(new[] { 1200d, 1200d, 1200d, 1800d, 2400d }, samples);
    }

    [TestMethod]
    public void Fan_curve_samples_keep_only_the_newest_points_when_history_is_longer()
    {
        var samples = FanCurveMath.CreateStableSamples([1000, 1500, 2000, 2500], 3000, 3);

        CollectionAssert.AreEqual(new[] { 2000d, 2500d, 3000d }, samples);
    }

    [TestMethod]
    public void Fan_curve_samples_ignore_an_unknown_latest_value_without_losing_history()
    {
        var samples = FanCurveMath.CreateStableSamples([1000, 1500], null, 4);

        CollectionAssert.AreEqual(new[] { 1000d, 1000d, 1000d, 1500d }, samples);
    }

    [TestMethod]
    public void Fan_curve_samples_interpolate_without_changing_geometry()
    {
        var samples = FanCurveMath.InterpolateSamples([1000, 2000, 3000], [2000, 3000, 5000], 0.5);

        CollectionAssert.AreEqual(new[] { 1500d, 2500d, 4000d }, samples);
    }

    [TestMethod]
    public void Fan_curve_rolling_samples_append_one_new_value_without_duplicate_tail()
    {
        var samples = FanCurveMath.CreateRollingSamples([1000, 1500], 2000, 4);

        CollectionAssert.AreEqual(new[] { 1000d, 1000d, 1500d, 2000d }, samples);
    }

    [TestMethod]
    public void Fan_curve_smoothing_is_monotonic_and_does_not_overshoot()
    {
        var middle = FanCurveMath.SmoothTowards(1000, 4000, 0.25, 0.6);

        Assert.IsTrue(middle > 1000 && middle < 4000);
        Assert.AreEqual(4000, FanCurveMath.SmoothTowards(1000, 4000, 10, 0.6));
    }
}
