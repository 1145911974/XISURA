using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PrototypePageTransitionTests
{
    [TestMethod]
    public void Normal_navigation_uses_short_subtle_page_motion()
    {
        var motion = PrototypeMotionProfile.Resolve(reducedMotion: false);
        var controller = new PrototypePageTransitionController();

        var plan = controller.Begin("Home", "Performance", motion, animate: true);

        Assert.IsFalse(plan.Snap);
        Assert.IsTrue(plan.FadeThrough);
        Assert.AreEqual("Home", plan.CurrentPage);
        Assert.AreEqual("Performance", plan.TargetPage);
        Assert.AreEqual(motion.Page, plan.ExitDuration);
        Assert.AreEqual(motion.Page, plan.EnterDuration);
        Assert.AreEqual(0, plan.ExitOffset);
        Assert.AreEqual(0, plan.EnterOffset);
    }

    [TestMethod]
    public void Reduced_motion_navigation_snaps_without_translation()
    {
        var motion = PrototypeMotionProfile.Resolve(reducedMotion: true);
        var controller = new PrototypePageTransitionController();

        var plan = controller.Begin("Performance", "Gpu", motion, animate: true);

        Assert.IsTrue(plan.Snap);
        Assert.IsFalse(plan.FadeThrough);
        Assert.AreEqual(TimeSpan.Zero, plan.ExitDuration);
        Assert.AreEqual(TimeSpan.Zero, plan.EnterDuration);
        Assert.AreEqual(0, plan.ExitOffset);
        Assert.AreEqual(0, plan.EnterOffset);
    }

    [TestMethod]
    public void Latest_navigation_request_gets_a_newer_version()
    {
        var motion = PrototypeMotionProfile.Resolve(reducedMotion: false);
        var controller = new PrototypePageTransitionController();

        var first = controller.Begin("Home", "Performance", motion, animate: true);
        var latest = controller.Begin("Home", "Lighting", motion, animate: true);

        Assert.IsTrue(latest.Version > first.Version);
        Assert.AreEqual("Lighting", latest.TargetPage);
    }

    [TestMethod]
    public void Repeated_request_for_current_target_is_a_noop()
    {
        var state = new PrototypePageNavigationState("Home");

        Assert.IsFalse(state.TryRequest("Home"));
        Assert.IsTrue(state.TryRequest("Performance"));
        Assert.IsFalse(state.TryRequest("Performance"));
        Assert.AreEqual("Performance", state.TargetPage);
    }
}
