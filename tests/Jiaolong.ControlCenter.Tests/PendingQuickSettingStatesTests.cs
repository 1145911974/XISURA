using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PendingQuickSettingStatesTests
{
    [TestMethod]
    public void Stale_snapshot_does_not_clear_or_override_the_latest_ui_intent()
    {
        var pending = new PendingQuickSettingStates();

        pending.Set(QuickSettingKind.Wifi, true);

        Assert.IsTrue(pending.ShouldPreserve(QuickSettingKind.Wifi, false));
        Assert.IsFalse(pending.TryConfirm(QuickSettingKind.Wifi, false));
        Assert.IsTrue(pending.TryConfirm(QuickSettingKind.Wifi, true));
        Assert.IsFalse(pending.ShouldPreserve(QuickSettingKind.Wifi, true));
    }

    [TestMethod]
    public void Lock_bit_settings_confirm_against_their_ui_semantics()
    {
        var pending = new PendingQuickSettingStates();

        pending.Set(QuickSettingKind.FnLock, true);

        Assert.IsTrue(pending.ShouldPreserve(QuickSettingKind.FnLock, true));
        Assert.IsFalse(pending.TryConfirm(QuickSettingKind.FnLock, true));
        Assert.IsTrue(pending.TryConfirm(QuickSettingKind.FnLock, false));
    }

    [TestMethod]
    public void A_new_click_replaces_an_older_pending_intent()
    {
        var pending = new PendingQuickSettingStates();

        var firstRequest = pending.Set(QuickSettingKind.Bluetooth, true);
        var latestRequest = pending.Set(QuickSettingKind.Bluetooth, false);

        Assert.IsTrue(pending.ShouldPreserve(QuickSettingKind.Bluetooth, true));
        Assert.IsFalse(pending.TryConfirm(QuickSettingKind.Bluetooth, true));
        Assert.IsFalse(pending.Clear(QuickSettingKind.Bluetooth, firstRequest));
        Assert.IsTrue(pending.ShouldPreserve(QuickSettingKind.Bluetooth, true));
        Assert.IsTrue(pending.Clear(QuickSettingKind.Bluetooth, latestRequest));
        Assert.IsFalse(pending.ShouldPreserve(QuickSettingKind.Bluetooth, true));
    }
}
