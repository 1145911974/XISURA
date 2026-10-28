using Jiaolong_ControlCenter.Services;
using Windows.Graphics;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class WorkAreaFitSizingSessionTests
{
    [TestMethod]
    public void Completed_resize_ends_fit_and_does_not_leak_into_next_interaction()
    {
        var session = new WorkAreaFitSizingSession();
        session.Begin(new WindowSizePreference(1600, 1000));
        session.RecordSizing();
        Assert.IsTrue(session.Complete(new WindowSizePreference(1280, 800)));
        Assert.IsFalse(session.Complete(new WindowSizePreference(1200, 750)));
        session.Begin(new WindowSizePreference(1600, 1000));
        Assert.IsFalse(session.Complete(new WindowSizePreference(1280, 800)));
    }

    [TestMethod]
    public void Cancelled_resize_or_return_to_original_size_preserves_fit()
    {
        var session = new WorkAreaFitSizingSession();
        var initial = new WindowSizePreference(1600, 1000);
        session.Begin(initial);
        session.RecordSizing();
        session.RecordSizing();
        Assert.IsFalse(session.Complete(initial));
        session.Begin(initial);
        session.RecordSizing();
        Assert.IsFalse(session.Complete(default));
        Assert.IsFalse(session.Complete(new WindowSizePreference(1280, 800)));
    }

    [TestMethod]
    public void Movement_and_unfitted_interactions_do_not_end_fit()
    {
        var session = new WorkAreaFitSizingSession();
        session.Begin(new WindowSizePreference(1600, 1000));
        Assert.IsFalse(session.Complete(new WindowSizePreference(1280, 800)));
        session.Begin(null);
        session.RecordSizing();
        Assert.IsFalse(session.Complete(new WindowSizePreference(1280, 800)));
    }

    [TestMethod]
    public void Dpi_only_change_preserves_fit_but_logical_resize_ends_it()
    {
        var session = new WorkAreaFitSizingSession();
        var initial = WindowSizePersistence.FromPixels(new SizeInt32(1600, 1000), 96);
        session.Begin(initial);
        session.RecordSizing();
        Assert.IsFalse(session.Complete(
            WindowSizePersistence.FromPixels(new SizeInt32(2400, 1500), 144)));
        session.Begin(initial);
        session.RecordSizing();
        Assert.IsTrue(session.Complete(
            WindowSizePersistence.FromPixels(new SizeInt32(1920, 1200), 144)));
    }
}
