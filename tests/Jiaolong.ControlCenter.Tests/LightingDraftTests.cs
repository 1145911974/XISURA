using Jiaolong_ControlCenter.Prototype.Controls;
using Jiaolong_ControlCenter.Services;
using Jiaolong.Contracts.Models;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class LightingDraftTests
{
    [TestMethod]
    public void Hex_input_accepts_six_digits_and_rejects_partial_or_alpha()
    {
        Assert.IsTrue(LightingColor.TryParse(" #00d7e8 ", out var color));
        Assert.AreEqual((byte)215, color.G);
        Assert.AreEqual("#00D7E8", color.Hex);
        foreach (var invalid in new[] { "#fff", "FF00D7E8", "hello", "", "#12345G" })
            Assert.IsFalse(LightingColor.TryParse(invalid, out _));
    }

    [TestMethod]
    public void Hue_and_square_coordinates_produce_expected_colors()
    {
        Assert.AreEqual("#FF0000", LightingColor.FromHsv(0, 1, 1).Hex);
        Assert.AreEqual("#00FFFF", LightingColor.FromHsv(180, 1, 1).Hex);
        Assert.AreEqual("#808080", LightingColor.FromHsv(120, 0, .5).Hex);
        Assert.AreEqual("#000000", LightingColor.FromHsv(240, 1, 0).Hex);
        var hsv = new LightingColor(0, 215, 232).ToHsv();
        Assert.AreEqual("#00D7E8", LightingColor.FromHsv(hsv.H, hsv.S, hsv.V).Hex);
    }

    [TestMethod]
    public void Saved_draft_rejects_unsupported_effects_and_brightness()
    {
        var valid = new LightingDraft("Gradient", 2, "#00D7E8", true);
        Assert.IsTrue(valid.IsValid());
        Assert.AreEqual(valid, LightingDraft.FromPlan(valid.ToPlan()));
        Assert.AreEqual("Static", LightingDraft.FromPlan(valid.ToPlan() with { Effect = "Fixed" })!.Effect);
        Assert.IsNull(LightingDraft.FromPlan(valid.ToPlan() with { Green = null }));
        Assert.IsNull(LightingDraft.FromPlan(valid.ToPlan() with { Speed = double.NaN }));
        Assert.IsTrue((valid with { Effect = "Static" }).IsValid());
        Assert.IsFalse((valid with { Effect = "Wave" }).IsValid());
        Assert.IsFalse((valid with { Brightness = 4 }).IsValid());
        Assert.IsFalse((valid with { Brightness = -1 }).IsValid());
        Assert.IsFalse((valid with { Hex = "oops" }).IsValid());
        Assert.AreEqual(valid, System.Text.Json.JsonSerializer.Deserialize<LightingDraft>(System.Text.Json.JsonSerializer.Serialize(valid)));
    }

    [TestMethod]
    public void Preview_static_holds_gradient_flows_and_legacy_cycle_is_not_editable()
    {
        var red = new LightingDraft("Static", 2, "#FF0000", true);
        Assert.AreEqual("#FF0000", red.PreviewColor(5).Hex);
        Assert.AreEqual("#00FFFF", (red with { Effect = "Gradient" }).PreviewColor(3).Hex);
        var cycle = red with { Effect = "Cycle" };
        Assert.IsFalse(cycle.IsValid());
        Assert.AreEqual("Gradient", cycle.ToPlan().Effect);
    }

    [TestMethod]
    public void Effect_speed_scales_time_and_older_presets_keep_normal_speed()
    {
        var draft = new LightingDraft("Gradient", 2, "#FF0000", true);
        Assert.AreEqual("#00FFFF", (draft with { Speed = 2 }).PreviewColor(1.5).Hex);
        Assert.AreEqual("#00FFFF", (draft with { Speed = .5 }).PreviewColor(6).Hex);
        Assert.AreEqual(1d, JsonSerializer.Deserialize<LightingDraft>("{\"Effect\":\"Gradient\",\"Brightness\":2,\"Hex\":\"#FF0000\",\"Logo\":true}")!.Speed);
        Assert.IsFalse((draft with { Speed = 0 }).IsValid());
        Assert.IsFalse((draft with { Speed = double.NaN }).IsValid());
        Assert.AreEqual(1.5d, JsonSerializer.Deserialize<LightingDraft>(JsonSerializer.Serialize(draft with { Speed = 1.5 }))!.Speed);
    }

    [TestMethod]
    public async Task Lighting_slots_persist_independently_without_changing_global_preferences()
    {
        var paths = new RecordingPathProvider();
        try
        {
            var store = new ControlPresetStore(paths);
            var settings = new UserPreferencesStore(paths);
            settings.Save(new UserPreferences(true, "dark", false) { LightingRestoreAtStartup = true, LightingRestoreAfterWake = true });
            var first = PresetKey.Create(ControlModeId.Gaming, 1);
            var second = PresetKey.Create(ControlModeId.Gaming, 2);
            await store.SaveAsync(new(1, ControlPageId.Lighting, first, "one", JsonSerializer.SerializeToElement(new LightingDraft("Static", 1, "#FF0000", false)), DateTimeOffset.UtcNow), CancellationToken.None);
            await store.SaveAsync(new(1, ControlPageId.Lighting, second, "two", JsonSerializer.SerializeToElement(new LightingDraft("Cycle", 3, "#00FF00", true)), DateTimeOffset.UtcNow), CancellationToken.None);
            var reopened = new ControlPresetStore(paths);
            Assert.AreEqual("#FF0000", (await reopened.LoadAsync(ControlPageId.Lighting, first, CancellationToken.None))!.Payload.Deserialize<LightingDraft>()!.Hex);
            Assert.AreEqual("Gradient", (await reopened.LoadAsync(ControlPageId.Lighting, second, CancellationToken.None))!.Payload.Deserialize<LightingDraft>()!.Effect);
            Assert.IsTrue(settings.Load().LightingRestoreAtStartup);
            Assert.IsTrue(settings.Load().LightingRestoreAfterWake);
            Assert.AreEqual("dark", settings.Load().Theme);
        }
        finally
        {
            // RecordingPathProvider creates an isolated GUID directory only for this test.
            var root = Path.GetFullPath(paths.LocalAppDataRoot);
            Assert.IsTrue(root.StartsWith(Path.Combine(Path.GetTempPath(), "JiaolongTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Legacy_cycle_migrates_to_gradient_without_changing_other_values_or_keep_intent()
    {
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Lighting,
            PresetKey.Create(ControlModeId.Gaming, 1), "legacy", JsonSerializer.SerializeToElement(new
                { Effect = "Cycle", Brightness = 2, Hex = "#123456", Logo = false, Speed = 1.5, Note = "retain" }), DateTimeOffset.UnixEpoch)
        { UserModified = true, Revision = 3, Settings = new()
            { ["effect"] = SettingRequest.Set("Cycle"), ["brightness"] = SettingRequest.Keep } };
        var migrated = DefaultControlPresets.Complete(preset);
        Assert.AreEqual("Gradient", migrated.Payload.GetProperty("Effect").GetString());
        Assert.AreEqual("Gradient", migrated.Settings["effect"].Value!.Value.GetString());
        Assert.AreEqual(SettingIntent.Keep, migrated.Settings["brightness"].Intent);
        Assert.AreEqual("#123456", migrated.Payload.GetProperty("Hex").GetString());
        Assert.AreEqual(1.5, migrated.Payload.GetProperty("Speed").GetDouble());
        Assert.AreEqual("retain", migrated.Payload.GetProperty("Note").GetString());
        Assert.AreEqual(3L, migrated.Revision);
        Assert.AreEqual("Cycle", preset.Payload.GetProperty("Effect").GetString());
        Assert.AreEqual(SettingIntent.Keep, DefaultControlPresets.Complete(preset with
            { Settings = new() { ["effect"] = SettingRequest.Keep } }).Settings["effect"].Intent);
        var future = preset with { SchemaVersion = PagePresetEnvelope.CurrentSchemaVersion + 1 };
        Assert.AreSame(future, DefaultControlPresets.Complete(future));
        var draft = LightingDraft.FromPlan(new("Cycle", 67)
            { Red = 0x12, Green = 0x34, Blue = 0x56, BrightnessLevel = 2, Speed = 1.5, LogoEnabled = false });
        Assert.AreEqual("Gradient", draft?.Effect);
        Assert.AreEqual("#123456", draft?.Hex);
    }
}
