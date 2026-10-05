namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class ResponsiveLayoutTests
{
    [TestMethod]
    public void Shell_has_breakpoint_safe_navigation_and_scrollable_content()
    {
        var root = FindRoot();
        var window = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "MainWindow.xaml"));
        var home = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Pages", "HomePage.xaml"));
        var budget = File.ReadAllText(Path.Combine(root, "docs", "engineering", "performance-budget.md"));

        StringAssert.Contains(window, "OpenPaneLength");
        StringAssert.Contains(window, "SelectionFollowsFocus");
        StringAssert.Contains(home, "ScrollViewer");
        StringAssert.Contains(budget, "1280×720");
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
