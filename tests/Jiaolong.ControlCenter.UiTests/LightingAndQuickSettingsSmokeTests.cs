using Jiaolong_ControlCenter.Pages;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class LightingAndQuickSettingsSmokeTests
{
    [TestMethod]
    public void Lighting_page_contract_keeps_fixed_quick_grid_and_lid_logo_simple()
    {
        Assert.IsNotNull(typeof(LightingPage));
        Assert.AreEqual(6, QuickSettingsFactory.CreateDefault().Count);
        Assert.IsTrue(typeof(LightingDraft).GetProperty(nameof(LightingDraft.LidLogoEnabled)) is not null);
    }
}
