using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class FanCurveEditorModelTests
{
    [TestMethod]
    public void Curve_editor_model_keeps_exactly_six_points()
    {
        var draft = FanFixture.ValidDraft;

        Assert.AreEqual(6, draft.CpuCurve.Points.Count);
        Assert.AreEqual(6, draft.GpuCurve.Points.Count);
    }
}
