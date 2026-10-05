namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class ReducedMotionTests
{
    [TestMethod]
    public void Reduced_motion_token_is_bounded_and_the_shell_honors_system_preferences()
    {
        var root = FindRoot();
        var motion = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Styles", "Motion.xaml"));
        var shell = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs"));

        StringAssert.Contains(motion, "ReducedMotionDuration");
        StringAssert.Contains(motion, "0:0:0.100");
        StringAssert.Contains(shell, "userPreferences.ReduceMotion");
        StringAssert.Contains(shell, "new MotionSettingsService().IsReducedMotionEnabled");
        StringAssert.Contains(shell, "!new UISettings().AnimationsEnabled");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
