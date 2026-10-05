using Jiaolong_ControlCenter.Pages;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class HomePageSmokeTests
{
    [TestMethod]
    public void Home_page_contract_exposes_read_only_fallback_and_view_type()
    {
        Assert.IsNotNull(typeof(HomePage));
        var vm = new HomeViewModel();

        Assert.AreEqual("未知", vm.CpuTemperature.Value);
        Assert.AreEqual("只读安全模式：未验证的硬件能力已禁用", vm.WriteDisabledReason);
    }
}
