using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class LightingPreviewLeaseTests
{
    [TestMethod]
    public void Preview_preserves_first_original_and_expires_after_last_heartbeat()
    {
        var original = new KeyboardLightingPlan("Cycle", 67) { Red = 12, Green = 34, Blue = 56, Speed = 2, LogoEnabled = true };
        var lease = new LightingPreviewLease();
        var now = DateTimeOffset.UtcNow;
        Assert.IsFalse(lease.Expired(now));
        lease.Begin(original, now);
        lease.Begin(original with { Red = 200 }, now.AddSeconds(4));
        Assert.AreEqual(original with { LogoEnabled = null }, lease.Original);
        Assert.IsFalse(lease.Expired(now.AddSeconds(11)));
        Assert.IsTrue(lease.Expired(now.AddSeconds(12)));
        lease.Complete();
        Assert.IsNull(lease.Original);
        Assert.IsFalse(lease.Expired(now.AddMinutes(1)));
    }

    [TestMethod]
    public void Missing_original_is_rejected_and_failed_restore_keeps_recovery_owned()
    {
        var lease = new LightingPreviewLease();
        var now = DateTimeOffset.UtcNow;
        Assert.ThrowsExactly<InvalidOperationException>(() => lease.Begin(null, now));
        var original = new KeyboardLightingPlan("Fixed", 100);
        lease.Begin(original, now);
        Assert.IsTrue(lease.Expired(now.AddSeconds(9)));
        Assert.AreEqual(original, lease.Original);
        lease.Complete(); // Only a successful restore or a formal apply releases ownership.
        lease.Begin(original with { Brightness = 0 }, now.AddSeconds(10));
        Assert.AreEqual(0, lease.Original!.Brightness);
    }
}
