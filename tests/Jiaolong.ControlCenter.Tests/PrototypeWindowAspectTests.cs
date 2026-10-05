using Jiaolong_ControlCenter.Prototype;
using Windows.Graphics;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PrototypeWindowAspectTests
{
    [TestMethod]
    public void Final_design_size_is_the_16_by_10_target()
    {
        Assert.AreEqual(new SizeInt32(1672, 1045), PrototypeWindowChrome.FinalDesignClientSize);
    }

    [TestMethod]
    public void Proposed_client_box_is_fitted_to_the_design_aspect()
    {
        Assert.AreEqual(new SizeInt32(1506, 941), PrototypeWindowChrome.ConstrainClientSize(1800, 941));
        Assert.AreEqual(new SizeInt32(1600, 1000), PrototypeWindowChrome.ConstrainClientSize(1672, 1000));
        Assert.AreEqual(new SizeInt32(836, 522), PrototypeWindowChrome.ConstrainClientSize(836, 600));
    }

    [TestMethod]
    public void Initial_client_size_uses_work_area_occupancy_and_preserves_aspect()
    {
        var fitted = PrototypeWindowChrome.FitClientSize(3840, 2160, .86);

        Assert.AreEqual(2973, fitted.Width);
        Assert.AreEqual(1858, fitted.Height);
        Assert.AreEqual(1.6d, (double)fitted.Width / fitted.Height, .001);
    }

    [TestMethod]
    public void Prototype_window_uses_compact_default_scale()
    {
        Assert.AreEqual(.72, PrototypeWindowChrome.DefaultOccupancy, .001);
    }

    [TestMethod]
    public void Full_work_area_fit_includes_non_client_frame()
    {
        var client = PrototypeWindowChrome.FitClientSizeForWorkArea(1920, 1040, 6, 6, 1);

        Assert.IsTrue(client.Width + 6 <= 1920);
        Assert.IsTrue(client.Height + 6 <= 1040);
        Assert.AreEqual(1.6d, client.Width / (double)client.Height, .001);
    }

    [TestMethod]
    public void Left_edge_sizing_preserves_opposite_edge_and_maintains_design_aspect_ratio()
    {
        var rect = PrototypeWindowChrome.ConstrainSizingRect(
            new WindowSizingRect(100, 100, 1500, 1000), WindowSizingEdge.Left, 6, 6);

        Assert.AreEqual(1500, rect.Right);
        Assert.AreEqual(100, rect.Top);
        Assert.AreEqual(100, rect.Left);
        Assert.AreEqual(1.6d, (double)(rect.Width - 6) / (rect.Height - 6), .01);
    }

    [TestMethod]
    public void Corner_sizing_enforces_aspect_ratio_and_anchor()
    {
        var rect = PrototypeWindowChrome.ConstrainSizingRect(
            new WindowSizingRect(900, 600, 1000, 700), WindowSizingEdge.TopLeft, 6, 6);

        Assert.AreEqual(1000, rect.Right);
        Assert.AreEqual(700, rect.Bottom);
        Assert.IsTrue(rect.Width >= Math.Round(PrototypeWindowChrome.DesignWidth * PrototypeWindowChrome.MinimumScale) + 6);
        Assert.IsTrue(rect.Height >= Math.Round(PrototypeWindowChrome.DesignHeight * PrototypeWindowChrome.MinimumScale) + 6);
        Assert.AreEqual(1.6d, (double)(rect.Width - 6) / (rect.Height - 6), .01);
    }
}
