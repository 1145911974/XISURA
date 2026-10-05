using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class ShellSmokeTests
{
    [TestMethod]
    public void Shell_navigation_contract_exposes_seven_keyboard_targets()
    {
        var automationIds = ShellDestination.All.Select(x => $"Nav.{x.Id}").ToArray();

        CollectionAssert.AreEqual(
            new[] { "Nav.Home", "Nav.Performance", "Nav.Gpu", "Nav.Fan", "Nav.Lighting", "Nav.Automation", "Nav.Settings" },
            automationIds);
    }
}
