using Jiaolong_ControlCenter.Prototype.Controls;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class FanCurveDraftTests
{
    [TestMethod]
    public void Curve_readback_compares_values_and_ignores_unrelated_control_settings()
    {
        var match = typeof(FanCurveDraft).GetMethod("MatchesCurve");
        Assert.IsNotNull(match, "Identical telemetry must not replay the curve transition.");
        var draft = new FanCurveDraft(1);
        var same = new FanCurveState(1, false, draft.Cpu.ToArray(), draft.Gpu.ToArray(), draft.Shared.ToArray());
        bool Matches(FanCurveState state) => (bool)match.Invoke(draft, [state])!;
        Assert.IsTrue(Matches(same));
        Assert.IsTrue(Matches(same with { Strategy = "Auto", FixedRpm = 4100, MaximumRpm = 4500 }));
        Assert.IsFalse(Matches(same with { Profile = 0 }));
        Assert.IsFalse(Matches(same with { IsShared = true }));
        var changed = same.Gpu.ToArray();
        changed[1] = changed[1] with { TargetPercent = changed[1].TargetPercent + 1 };
        Assert.IsFalse(Matches(same with { Gpu = changed }));
        Assert.IsTrue(Matches(same));
    }

    [TestMethod]
    public void Preset_round_trip_keeps_curves_strategy_and_fixed_target()
    {
        var points = FanCurveDraft.Recommended(1);
        var state = new FanCurveState(1, true, points, points, points, "Fixed", 4100);
        var restored = System.Text.Json.JsonSerializer.Deserialize<FanCurveState>(System.Text.Json.JsonSerializer.Serialize(state))!;
        Assert.IsTrue(restored.IsValid());
        Assert.AreEqual("Fixed", restored.Strategy);
        Assert.AreEqual(4100, restored.FixedRpm);
        Assert.IsTrue(restored.IsShared);
        CollectionAssert.AreEqual(state.Shared, restored.Shared);
    }

    [TestMethod]
    public void Rotation_acceleration_and_retargeting_keep_angle_continuous()
    {
        Assert.AreEqual(90d, FanMotionMath.RotationDistance(0, 360, .5, .5), .0001);
        Assert.AreEqual(270d, FanMotionMath.RotationDistance(0, 360, 1, .5), .0001);
        double before = FanMotionMath.RotationDistance(360, 180, .5 - .000001, .5);
        double after = FanMotionMath.RotationDistance(360, 180, .5 + .000001, .5);
        Assert.IsTrue(after > before && after - before < .001);
        Assert.AreEqual(0d, FanMotionMath.RotationDistance(180, 360, 0, .5));
    }
    [TestMethod]
    public void Invalid_saved_curves_are_rejected_before_import()
    {
        var valid = FanCurveDraft.Recommended(1);
        Assert.IsTrue(new FanCurveState(1, false, valid, valid, valid).IsValid());
        Assert.IsFalse(new FanCurveState(1, false, [new(50, 80), new(40, 30)], valid, valid).IsValid());
        Assert.IsTrue(new FanCurveState(1, false, [new(30, 80), new(50, 30)], valid, valid).IsValid());
        Assert.IsFalse(new FanCurveState(3, false, valid, valid, valid).IsValid());
        Assert.IsFalse(new FanCurveState(1, false, valid, valid, valid, "Unknown", 3000).IsValid());
        Assert.IsFalse(new FanCurveState(1, false, valid, valid, valid, "Fixed", 7000).IsValid());
    }
    [TestMethod]
    public void Editing_shared_curve_preserves_both_independent_drafts()
    {
        var draft = new FanCurveDraft(1);
        var cpu = draft.Cpu.ToArray();
        var gpu = draft.Gpu.ToArray();
        draft.IsShared = true;
        FanCurveDraft.Update(draft.Points(FanCurveSeries.Gpu), 2, 51, 38);
        CollectionAssert.AreEqual(cpu, draft.Cpu.ToArray());
        CollectionAssert.AreEqual(gpu, draft.Gpu.ToArray());
        Assert.AreSame(draft.Points(FanCurveSeries.Cpu), draft.Points(FanCurveSeries.Gpu));
    }

    [TestMethod]
    public void Restore_uses_profile_recommendation_not_last_edit()
    {
        foreach (int profile in new[] { 0, 1, 2 })
        {
            var draft = new FanCurveDraft(profile);
            FanCurveDraft.Update(draft.Cpu, 2, 51, 31);
            draft.Reset(false);
            CollectionAssert.AreEqual(FanCurveDraft.Recommended(profile), draft.Cpu.ToArray());
            CollectionAssert.AreEqual(FanCurveDraft.Recommended(profile, true), draft.Gpu.ToArray());
        }
    }

    [TestMethod]
    public void Node_insertion_deduplicates_temperature_and_clamps_target()
    {
        var points = new List<CurvePoint> { new(30, 20), new(100, 100) };
        int index = FanCurveDraft.Add(points, 60, -100);
        Assert.AreEqual(new CurvePoint(60, 0), points[index]);
        Assert.AreEqual(index, FanCurveDraft.Add(points, 60, 50));
        Assert.AreEqual(3, points.Count);
        FanCurveDraft.Update(points, index, 200, 200);
        Assert.AreEqual(new CurvePoint(99, 100), points[index]);
        var latePoints = FanCurveDraft.Recommended(1).ToList();
        var original = latePoints.ToArray();
        int lateIndex = FanCurveDraft.Add(latePoints, 85, 86);
        Assert.AreEqual(new CurvePoint(85, 86), latePoints[lateIndex]);
        latePoints.RemoveAt(lateIndex);
        CollectionAssert.AreEqual(original, latePoints.ToArray());
    }

    [TestMethod]
    public void High_temperature_curve_warning_identifies_low_target_intervals()
    {
        var recommended = FanCurveDraft.Recommended(1);
        var low = recommended.ToArray();
        low[5] = low[5] with { TargetPercent = 15 };
        var gpuRecommended = FanCurveDraft.Recommended(1, true);
        var state = new FanCurveState(1, false, low, gpuRecommended, gpuRecommended);
        Assert.IsTrue(state.IsValid());
        var warnings = FanCurveSafety.Assess(state);
        Assert.IsTrue(warnings.Any(w => w.Series == "CPU" && w.StartC <= 80 && w.EndC >= 80
            && w.MinimumPercent <= 15 && w.RecommendedPercent >= 78));
        Assert.AreEqual(0, FanCurveSafety.Assess(state with { Strategy = "Auto" }).Count);
        Assert.AreEqual(0, FanCurveSafety.Assess(state with { Cpu = recommended }).Count);
        var fixedState = state with { Strategy = "Fixed", FixedRpm = 1800, IsShared = true };
        var fixedWarnings = FanCurveSafety.Assess(fixedState);
        Assert.IsTrue(fixedWarnings.Any(w => w.Series == "CPU" && w.StartC == 80 && w.MinimumPercent == 0));
        Assert.IsTrue(fixedWarnings.Any(w => w.Series == "GPU" && w.StartC == 80 && w.MinimumPercent == 0));
        var quantizedWarnings = FanCurveSafety.Assess(fixedState with { FixedRpm = 3099 });
        Assert.IsTrue(quantizedWarnings.Count > 0 && quantizedWarnings.All(w => w.MinimumPercent == 30));
        Assert.AreEqual(0, FanCurveSafety.Assess(fixedState with { FixedRpm = 5800 }).Count);
        Assert.AreEqual(0, FanCurveSafety.Assess(fixedState with { FixedRpm = 0 }).Count);
    }

    [TestMethod]
    public void Recommendations_are_monotone_bounded_and_distinct()
    {
        for (int mode = 0; mode < 3; mode++)
        {
            var points = FanCurveDraft.Recommended(mode);
            for (int i = 1; i < points.Length; i++)
            {
                Assert.IsTrue(points[i].Temperature > points[i - 1].Temperature);
                Assert.IsTrue(points[i].TargetPercent >= points[i - 1].TargetPercent);
                Assert.IsTrue(points[i].TargetPercent <= 100);
            }
        }
        Assert.IsTrue(FanCurveDraft.Recommended(0)[3].TargetPercent < FanCurveDraft.Recommended(1)[3].TargetPercent);
        Assert.IsTrue(FanCurveDraft.Recommended(1)[3].TargetPercent < FanCurveDraft.Recommended(2)[3].TargetPercent);
    }
}
