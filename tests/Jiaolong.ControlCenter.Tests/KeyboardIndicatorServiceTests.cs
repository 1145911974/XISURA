using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class KeyboardIndicatorServiceTests
{
    [TestMethod]
    [DataRow(QuickSettingKind.NumLock, 0x90)]
    [DataRow(QuickSettingKind.CapsLock, 0x14)]
    public void Selected_keyboard_indicators_map_to_the_windows_virtual_key(QuickSettingKind setting, int expectedVirtualKey)
    {
        Assert.AreEqual(expectedVirtualKey, KeyboardIndicatorService.VirtualKeyFor(setting));
    }

    [TestMethod]
    public void Hardware_shortcuts_do_not_map_to_keyboard_indicators()
    {
        Assert.IsNull(KeyboardIndicatorService.VirtualKeyFor(QuickSettingKind.StrongCooling));
        Assert.IsNull(KeyboardIndicatorService.VirtualKeyFor(QuickSettingKind.LidLogo));
    }
}
