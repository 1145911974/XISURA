using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PresetNameCatalogTests
{
    [TestMethod]
    public void Defaults_are_named_by_slot_and_custom_names_are_mode_scoped()
    {
        var office = PresetKey.Create(ControlModeId.Office, 2);
        var gaming = PresetKey.Create(ControlModeId.Gaming, 2);
        var names = new Dictionary<string, string>();

        Assert.AreEqual("安静", PresetNameCatalog.GetName(names, PresetKey.Create(ControlModeId.Office, 1)));
        Assert.AreEqual("均衡", PresetNameCatalog.GetName(names, office));
        Assert.AreEqual("性能", PresetNameCatalog.GetName(names, PresetKey.Create(ControlModeId.Office, 3)));

        PresetNameCatalog.SetName(names, office, "  我的办公  ");
        Assert.AreEqual("我的办公", PresetNameCatalog.GetName(names, office));
        Assert.AreEqual("均衡", PresetNameCatalog.GetName(names, gaming));

        PresetNameCatalog.SetName(names, office, "");
        Assert.AreEqual("均衡", PresetNameCatalog.GetName(names, office));
    }

    [TestMethod]
    public void Renamed_slot_survives_preferences_round_trip()
    {
        var store = new UserPreferencesStore(new RecordingPathProvider());
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preferences = store.Load();
        PresetNameCatalog.SetName(preferences.PresetNames, key, "高效安静");
        store.Save(preferences);

        Assert.AreEqual("高效安静", PresetNameCatalog.GetName(store.Load().PresetNames, key));
    }
}
