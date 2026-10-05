using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class WindowsDisplayPowerControllerTests
{
    [TestMethod]
    public void Display_off_action_reports_the_native_sender_result()
    {
        var called = false;
        var controller = new WindowsDisplayPowerController(() =>
        {
            called = true;
            return true;
        });

        Assert.IsTrue(controller.TryTurnOff());
        Assert.IsTrue(called);
    }

    [TestMethod]
    public void Display_off_action_does_not_throw_when_native_sender_fails()
    {
        var controller = new WindowsDisplayPowerController(() => throw new InvalidOperationException());

        Assert.IsFalse(controller.TryTurnOff());
    }

    [TestMethod]
    public void Display_off_does_not_broadcast_to_every_top_level_window()
    {
        var source = File.ReadAllText(SourcePath("src", "Jiaolong.ControlCenter", "Services", "WindowsDisplayPowerController.cs"));

        StringAssert.Contains(source, "CreateWindowEx");
        Assert.IsFalse(source.Contains("new IntPtr(-1)", StringComparison.Ordinal));
    }

    private static string SourcePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;

        return Path.Combine(new[]
        {
            directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。")
        }.Concat(parts).ToArray());
    }
}
