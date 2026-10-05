using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class WindowsKeyLockControllerTests
{
    [TestMethod]
    public void Enabled_state_means_windows_key_is_available()
    {
        var installed = false;
        var removed = false;
        using var controller = new WindowsKeyLockController(
            () =>
            {
                installed = true;
                return new IntPtr(1);
            },
            () =>
            {
                removed = true;
                return true;
            });

        Assert.IsTrue(controller.IsEnabled);
        Assert.IsTrue(controller.TrySetEnabled(false));
        Assert.IsTrue(installed);
        Assert.IsFalse(controller.IsEnabled);
        Assert.IsTrue(controller.TrySetEnabled(true));
        Assert.IsTrue(removed);
        Assert.IsTrue(controller.IsEnabled);
    }
}
