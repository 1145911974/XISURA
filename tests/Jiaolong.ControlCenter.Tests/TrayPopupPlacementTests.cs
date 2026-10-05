using Jiaolong_ControlCenter.Services;
using Windows.Graphics;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class TrayPopupPlacementTests
{
    [TestMethod]
    public void Console_docks_inside_work_area_with_fixed_gap()
    {
        var p = WindowPlacement.DockBottomRight(new WindowWorkArea(0, 0, 2560, 1536), new SizeInt32(615, 733), 15);
        Assert.AreEqual(1930, p.X);
        Assert.AreEqual(788, p.Y);
    }

    [TestMethod]
    public void Dock_supports_negative_monitor_origins_and_oversized_windows()
    {
        var p = WindowPlacement.DockBottomRight(new WindowWorkArea(-1920, 40, 1920, 1040), new SizeInt32(492, 586), 12);
        Assert.AreEqual(-504, p.X);
        Assert.AreEqual(482, p.Y);
        var oversized = WindowPlacement.DockBottomRight(new WindowWorkArea(0, 0, 400, 300), new SizeInt32(492, 586), 12);
        Assert.AreEqual(0, oversized.X);
        Assert.AreEqual(0, oversized.Y);
    }
}
