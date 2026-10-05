using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class TelemetryCurveSeriesTests
{
    [TestMethod]
    public void New_samples_and_interruptions_start_at_the_visible_head_without_a_jump()
    {
        var series = new TelemetryCurveSeries(60);
        series.UpdateSample(20, 0, true);
        series.UpdateSample(80, 1, true);
        Assert.AreEqual(20d, series.GetFrame(1, true).HeadValue);
        double visible = series.GetFrame(1.2, true).HeadValue!.Value;
        Assert.IsTrue(visible > 20 && visible < 80);
        series.UpdateSample(10, 1.2, true);
        Assert.AreEqual(visible, series.GetFrame(1.2, true).HeadValue);
        Assert.IsTrue(series.GetFrame(1.3, true).HeadValue < visible);
        Assert.AreEqual(10d, series.GetFrame(1.8, true).HeadValue);
    }

    [TestMethod]
    public void Missing_samples_keep_real_history_and_recovery_starts_a_new_segment()
    {
        var series = new TelemetryCurveSeries(24);
        var capturedAt = DateTimeOffset.UtcNow;
        series.UpdateSample(40, 0, true, capturedAt);
        series.UpdateSample(60, 1, true, capturedAt.AddSeconds(1));
        series.UpdateSample(double.NaN, 1.3, true, capturedAt.AddSeconds(1.3));
        var missing = series.GetFrame(1.4, true);
        Assert.AreEqual(0d, missing.HeadOpacity);
        Assert.IsTrue(missing.Samples.All(sample => sample.Value >= 40));
        series.UpdateSample(80, 2, true, capturedAt.AddSeconds(2));
        var recovered = series.GetFrame(2, true);
        Assert.AreEqual(80d, recovered.HeadValue);
        Assert.IsTrue(recovered.Samples.Any(sample => sample.Seconds == 2 && sample.StartsSegment));
    }

    [TestMethod]
    public void Reduced_motion_snaps_and_stale_history_exits_the_time_window()
    {
        var series = new TelemetryCurveSeries(24);
        series.UpdateSample(30, 0, true);
        series.UpdateSample(70, 1, true);
        Assert.AreEqual(70d, series.GetFrame(1.1, false).HeadValue);
        Assert.AreEqual(0d, series.GetFrame(3.01, true).HeadOpacity);
        Assert.IsTrue(series.GetFrame(25.5, true).Samples.Any(sample => sample.Seconds < 1.5));
        Assert.AreEqual(0, series.GetFrame(27.1, true).Samples.Count);
    }

    [TestMethod]
    public void Captured_sample_times_ignore_ui_jitter_and_duplicate_snapshots()
    {
        var series = new TelemetryCurveSeries(60);
        var capturedAt = DateTimeOffset.UtcNow;
        series.UpdateSample(40, 0, false, capturedAt);
        series.UpdateSample(90, 0.2, false, capturedAt);
        Assert.AreEqual(40d, series.GetFrame(0.2, false).HeadValue);

        series.UpdateSample(50, 2.5, false, capturedAt.AddSeconds(.75));

        var frame = series.GetFrame(3.2, false);
        Assert.IsFalse(frame.Samples.Any(sample => sample.Seconds == 2.5 && sample.StartsSegment));
        Assert.AreEqual(3.2d, frame.HeadSeconds);

        series.UpdateSample(70, 5.6, false, capturedAt.AddSeconds(4));
        Assert.IsTrue(series.GetFrame(5.6, false).Samples.Any(sample => sample.Seconds == 5.6 && sample.StartsSegment));
    }
}
