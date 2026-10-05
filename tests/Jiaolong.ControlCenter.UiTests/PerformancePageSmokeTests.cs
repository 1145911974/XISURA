using Jiaolong_ControlCenter.Pages;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class PerformancePageSmokeTests
{
    [TestMethod]
    public void Performance_page_contract_exposes_safe_draft_editor()
    {
        Assert.IsNotNull(typeof(PerformancePage));
        Assert.IsNotNull(typeof(PerformanceViewModel).GetProperty("EditableProfiles"));
        Assert.IsNull(typeof(PerformanceDraft).GetProperty("EnabledCoreCount"));
        Assert.IsNotNull(typeof(PerformanceDraft).GetProperty("NegativeCurveOptimizer"));
    }
}
