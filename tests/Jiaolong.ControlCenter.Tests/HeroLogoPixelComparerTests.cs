using Jiaolong_ControlCenter.Branding;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class HeroLogoPixelComparerTests
{
    [TestMethod]
    public void Identical_pixels_have_zero_error_and_a_transparent_heatmap()
    {
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255];
        var diff = HeroLogoPixelComparer.Compare(pixels, pixels);
        Assert.AreEqual(0, diff.MaxChannelDelta);
        Assert.AreEqual(0, diff.MeanChannelDelta);
        Assert.IsTrue(diff.HeatmapBgra.All(value => value == 0));
    }

    [TestMethod]
    public void Changed_pixel_reports_max_mean_and_red_heat()
    {
        byte[] expected = [0, 0, 0, 255];
        byte[] actual = [0, 4, 10, 255];
        var diff = HeroLogoPixelComparer.Compare(expected, actual);
        Assert.AreEqual(10, diff.MaxChannelDelta);
        Assert.AreEqual(14d / 3d, diff.MeanChannelDelta, 0.001);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 14, 255 }, diff.HeatmapBgra);
    }
}
