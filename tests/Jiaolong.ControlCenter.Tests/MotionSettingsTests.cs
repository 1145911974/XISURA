using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class MotionSettingsTests
{
    [TestMethod]
    public void Reduced_motion_maps_all_motion_tokens_to_fade_at_or_below_100ms()
    {
        var tokens = MotionTokenResolver.Resolve(reducedMotion: true);

        Assert.IsTrue(tokens.All(x =>
            x.Duration <= TimeSpan.FromMilliseconds(100) &&
            x.Translation == 0 &&
            x.Scale == 1));
    }
}
