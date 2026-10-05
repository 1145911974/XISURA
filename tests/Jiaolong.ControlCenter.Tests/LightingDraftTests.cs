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
    public void Preview_static_holds_gradient_flows_and_native_cycle_keeps_stored_color()
    {
        var red = new LightingDraft("Static", 2, "#FF0000", true);
        Assert.AreEqual("#FF0000", red.PreviewColor(5).Hex);
        Assert.AreEqual("#00FFFF", (red with { Effect = "Gradient" }).PreviewColor(3).Hex);
        var cycle = red with { Effect = "Cycle" };
        Assert.AreEqual("#FF0000", cycle.PreviewColor(2).Hex);
        Assert.AreEqual("#FF0000", cycle.PreviewColor(3).Hex);
        Assert.AreEqual("#FF0000", cycle.PreviewColor(2.75).Hex);
    }

    [TestMethod]
    public void Effect_speed_scales_time_and_older_presets_keep_normal_speed()
    {
        var draft = new LightingDraft("Gradient", 2, "#FF0000", true);
        Assert.AreEqual("#00FFFF", (draft with { Speed = 2 }).PreviewColor(1.5).Hex);
        Assert.AreEqual("#00FFFF", (draft with { Speed = .5 }).PreviewColor(6).Hex);
        Assert.AreEqual("#FF0000", (draft with { Effect = "Cycle", Speed = 2 }).PreviewColor(1.5).Hex);
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
            Assert.AreEqual("Cycle", (await reopened.LoadAsync(ControlPageId.Lighting, second, CancellationToken.None))!.Payload.Deserialize<LightingDraft>()!.Effect);
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
}
