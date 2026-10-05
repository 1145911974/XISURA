using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class TelemetryCurveWindowTests
{
    [TestMethod]
    public void Window_keeps_the_segment_crossing_its_continuously_moving_boundary()
    {
        var samples = new List<double> { 0, 1, 2, 3 };
        TelemetryCurveWindow.Trim(samples, time => time, 25.5, 24);
        CollectionAssert.AreEqual(new[] { 1d, 2d, 3d }, samples);
        Assert.AreEqual(-.5 / 24, TelemetryCurveWindow.Position(1, 25.5, 24), 1e-12);
        Assert.AreEqual(.5 / 24, TelemetryCurveWindow.Position(2, 25.5, 24), 1e-12);

        TelemetryCurveWindow.Trim(samples, time => time, 26.01, 24);
        CollectionAssert.AreEqual(new[] { 2d, 3d }, samples);
        Assert.IsTrue(TelemetryCurveWindow.Position(2, 26.01, 24) < 0);
    }

    [TestMethod]
    public void Last_sample_retains_its_bounded_hold_then_expires_without_a_fake_point()
    {
        var samples = new List<double> { 1 };
        TelemetryCurveWindow.Trim(samples, time => time, 26.9, 24, 2);
        Assert.AreEqual(1, samples.Count);
        TelemetryCurveWindow.Trim(samples, time => time, 27.01, 24, 2);
        Assert.AreEqual(0, samples.Count);
        TelemetryCurveWindow.Trim(samples, time => time, 28, 24, 2);
        Assert.AreEqual(0, samples.Count);
    }
}
