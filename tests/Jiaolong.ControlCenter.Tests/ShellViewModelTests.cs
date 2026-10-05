using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ShellViewModelTests
{
    [TestMethod]
    public void Shell_exposes_exactly_seven_stable_destinations()
    {
        CollectionAssert.AreEqual(
            new[] { "Home", "Performance", "Gpu", "Fan", "Lighting", "Automation", "Settings" },
            ShellDestination.All.Select(x => x.Id).ToArray());
    }
}
