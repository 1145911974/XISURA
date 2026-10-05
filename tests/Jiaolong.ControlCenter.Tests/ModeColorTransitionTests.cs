using Windows.UI;
using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ModeColorTransitionTests
{
    [TestMethod]
    public void Retargeting_starts_from_the_visible_color_without_leaking_the_previous_theme()
    {
        var transition = new ModeColorTransitionController();
        var office = Color.FromArgb(255, 22, 119, 255);
        var gaming = Color.FromArgb(255, 255, 138, 31);
        var turbo = Color.FromArgb(255, 255, 49, 65);

        transition.Begin([office], [gaming]);
        var visible = transition.Sample(0.5);
        transition.Begin(visible, [turbo]);

        CollectionAssert.AreEqual(visible.ToArray(), transition.Sample(0).ToArray());
        CollectionAssert.AreEqual(new[] { turbo }, transition.Sample(1).ToArray());
    }
}
