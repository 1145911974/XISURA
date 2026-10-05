using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class HomeQuickSettingSemanticsTests
{
    [TestMethod]
    public void Touchpad_and_fn_lock_bits_are_inverted_for_ui_enabled_state()
    {
        Assert.IsFalse(HomeQuickSettingSemantics.ToUiEnabled(QuickSettingKind.Touchpad, true));
        Assert.IsTrue(HomeQuickSettingSemantics.ToUiEnabled(QuickSettingKind.Touchpad, false));
        Assert.IsFalse(HomeQuickSettingSemantics.ToUiEnabled(QuickSettingKind.FnLock, true));
        Assert.IsTrue(HomeQuickSettingSemantics.ToUiEnabled(QuickSettingKind.FnLock, false));
    }

    [TestMethod]
    public void Ui_enabled_state_is_inverted_back_before_writing_touchpad_or_fn()
    {
        Assert.IsFalse(HomeQuickSettingSemantics.ToHardwareEnabled(QuickSettingKind.Touchpad, true));
        Assert.IsTrue(HomeQuickSettingSemantics.ToHardwareEnabled(QuickSettingKind.Touchpad, false));
        Assert.IsFalse(HomeQuickSettingSemantics.ToHardwareEnabled(QuickSettingKind.FnLock, true));
        Assert.IsTrue(HomeQuickSettingSemantics.ToHardwareEnabled(QuickSettingKind.FnLock, false));
    }

    [TestMethod]
    public void Wireless_controls_keep_direct_ui_semantics()
    {
        Assert.IsTrue(HomeQuickSettingSemantics.ToUiEnabled(QuickSettingKind.Wifi, true));
        Assert.IsFalse(HomeQuickSettingSemantics.ToUiEnabled(QuickSettingKind.Bluetooth, false));
        Assert.IsTrue(HomeQuickSettingSemantics.ToHardwareEnabled(QuickSettingKind.Wifi, true));
        Assert.IsFalse(HomeQuickSettingSemantics.ToHardwareEnabled(QuickSettingKind.Bluetooth, false));
    }
}
