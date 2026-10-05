namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class AccessibilityTests
{
    [TestMethod]
    public void All_seven_pages_and_navigation_targets_expose_accessible_contracts()
    {
        Assert.AreEqual(7, Jiaolong_ControlCenter.ViewModels.ShellDestination.All.Count);
        var root = FindRoot();
        foreach (var page in new[] { "HomePage.xaml", "PerformancePage.xaml", "GpuPage.xaml", "FanPage.xaml", "LightingPage.xaml", "AutomationPage.xaml", "SettingsPage.xaml" })
        {
            var source = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Pages", page));
            StringAssert.Contains(source, "AutomationProperties", page);
        }
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
