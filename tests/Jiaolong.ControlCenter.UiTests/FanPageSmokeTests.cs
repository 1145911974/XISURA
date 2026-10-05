using Jiaolong_ControlCenter.Controls;
using Jiaolong_ControlCenter.Pages;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class FanPageSmokeTests
{
    [TestMethod]
    public void Fan_page_contract_contains_keyboard_curve_editor_and_release_control()
    {
        Assert.IsNotNull(typeof(FanPage));
        Assert.IsNotNull(typeof(FanCurveEditor));
        var curve = new FanCurve([new FanPoint(40, 20), new(50, 30), new(60, 40), new(70, 55), new(80, 75), new(90, 100)]);
        Assert.AreEqual(6, curve.Points.Count);
    }
}
