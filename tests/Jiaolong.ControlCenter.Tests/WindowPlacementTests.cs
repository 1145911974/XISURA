using Jiaolong_ControlCenter.Services;
using Windows.Graphics;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class WindowPlacementTests
{
    [TestMethod]
    public void Saved_window_size_is_dpi_normalized_and_round_trips()
    {
        var saved = WindowSizePersistence.FromPixels(new SizeInt32(1966, 1229), 144);
        var restored = WindowSizePersistence.ToPixels(saved, 144);

        Assert.AreEqual(1966, restored.Width, 1);
        Assert.AreEqual(1229, restored.Height, 1);
        Assert.AreEqual(1.6d, (double)restored.Width / restored.Height, .01);
    }

    [TestMethod]
    public void Startup_position_centers_the_client_inside_the_primary_work_area()
    {
        var position = WindowPlacement.CenterClient(
            new WindowWorkArea(0, 0, 2560, 1600),
            new SizeInt32(1966, 1229),
            frameWidth: 8,
            frameHeight: 8);

        Assert.AreEqual(293, position.X);
        Assert.AreEqual(181, position.Y);
    }

    [TestMethod]
    public void Invalid_saved_size_is_ignored()
    {
        Assert.IsFalse(WindowSizePersistence.IsUsable(new WindowSizePreference(0, 819)));
        Assert.IsFalse(WindowSizePersistence.IsUsable(new WindowSizePreference(1311, 0)));
        Assert.IsTrue(WindowSizePersistence.IsUsable(new WindowSizePreference(1311, 819)));
    }
}
