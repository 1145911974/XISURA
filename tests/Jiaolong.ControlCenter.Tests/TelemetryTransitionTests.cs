using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class TelemetryTransitionTests
{
    [TestMethod]
    public void Samples_interpolate_from_visible_value_and_duplicate_samples_do_not_restart()
    {
        var value = new TelemetryTransition();
        value.SetTarget(40, 0, .4);
        Assert.AreEqual(40d, value.Value);
        value.SetTarget(80, 1, .4);
        value.Advance(1.2);
        Assert.IsTrue(value.Value > 40 && value.Value < 80);
        double? visible = value.Value;
        value.SetTarget(60, 1.2, .4);
        Assert.AreEqual(visible, value.Value);
        value.SetTarget(60, 1.3, .4);
        value.Advance(1.61);
        Assert.AreEqual(60d, value.Value);
        Assert.IsFalse(value.IsActive);
    }

    [TestMethod]
    public void Unknown_nonfinite_and_reduced_motion_never_animate_fake_zero()
    {
        var value = new TelemetryTransition();
        value.SetTarget(50, 0, .4);
        value.SetTarget(null, 1, .4);
        Assert.IsNull(value.Value);
        value.SetTarget(double.NaN, 2, .4);
        Assert.IsNull(value.Value);
        value.SetTarget(70, 3, .4);
        Assert.AreEqual(70d, value.Value);
        value.SetTarget(40, 4, 0);
        Assert.AreEqual(40d, value.Value);
        Assert.IsFalse(value.IsActive);
    }
}
