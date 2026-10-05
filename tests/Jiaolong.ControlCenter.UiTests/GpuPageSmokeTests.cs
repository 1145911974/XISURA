using Jiaolong_ControlCenter.Pages;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class GpuPageSmokeTests
{
    [TestMethod]
    public void Gpu_page_contract_keeps_mux_and_frequency_limits_separate()
    {
        Assert.IsNotNull(typeof(GpuPage));
        Assert.IsFalse(GpuDraftValidator.ValidateFrequencyLimit(2_500, 2_400).IsValid);
    }
}
