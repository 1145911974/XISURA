using Jiaolong_ControlCenter.Prototype.QuickMenu;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class QuickSettingsTests
{
    [TestMethod]
    public void Strong_cooling_is_editable_and_survives_saved_layout()
    {
        var kind = Jiaolong.Contracts.Models.QuickSettingKind.StrongCooling;
        var item = QuickMenuCatalog.CreateDefault().SingleOrDefault(item => item.Kind == kind);
        Assert.IsNotNull(item);
        Assert.AreEqual("strongCooling", item.CapabilityKey);
        Assert.AreEqual(QuickMenuInteraction.Toggle, item.Interaction);
        Assert.IsFalse(QuickMenuLayout.Default.EnabledOrder.Contains(kind));
        var restored = QuickMenuLayout.FromPersisted([kind.ToString()], [kind.ToString()]);
        CollectionAssert.AreEqual(new[] { kind }, restored.EnabledOrder.ToArray());
    }
}
