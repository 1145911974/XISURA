using Jiaolong_ControlCenter.Pages;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class AutomationPageSmokeTests
{
    [TestMethod]
    public void Automation_page_contract_exposes_priority_trace()
    {
        Assert.IsNotNull(typeof(AutomationPage));
        Assert.AreEqual(8, AutomationPriority.DisplayOrder.Length);
    }
}
