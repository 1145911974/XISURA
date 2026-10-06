using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ControlPresetStoreTests
{
    [TestMethod]
    public void Preset_key_exposes_six_modes_and_three_slots()
    {
        Assert.HasCount(18, PresetKey.All);
        Assert.AreEqual(18, PresetKey.All.Distinct().Count());
        Assert.IsTrue(PresetKey.All.Contains(new PresetKey(ControlModeId.Office, 1)));
        Assert.IsTrue(PresetKey.All.Contains(new PresetKey(ControlModeId.Custom3, 3)));
    }

    [TestMethod]
    public void Preset_key_rejects_slots_outside_one_to_three()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetKey.Create(ControlModeId.Office, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetKey.Create(ControlModeId.Office, 4));
    }

    [TestMethod]
    public async Task Save_then_load_round_trips_one_page_preset_without_hardware_application()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Office, 2);
        using var document = JsonDocument.Parse("{\"temperatureLimitC\":76}");
        var expected = new PagePresetEnvelope(
            1,
            ControlPageId.Performance,
            key,
            "办公 · 预设 2",
            document.RootElement.Clone(),
            DateTimeOffset.UtcNow);

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(expected.DisplayName, loaded.DisplayName);
        Assert.AreEqual(expected.Payload.GetProperty("temperatureLimitC").GetInt32(), loaded.Payload.GetProperty("temperatureLimitC").GetInt32());
        Assert.AreEqual(76, SavedPerformancePreset.ReadDraft(loaded, key).TemperatureLimitC);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var draft = new Jiaolong_ControlCenter.ViewModels.PerformanceDraft
        {
            AcMaxFrequencyMhz = 4200, DcMaxFrequencyMhz = 3500, NegativeCurveOptimizer = null
        };
        var complete = expected with { Payload = JsonSerializer.SerializeToElement(draft, options) };
        var decoded = SavedPerformancePreset.ReadDraft(complete, key);
        Assert.AreEqual(4200, decoded.AcMaxFrequencyMhz);
        Assert.AreEqual(3500, decoded.DcMaxFrequencyMhz);
        Assert.IsNull(decoded.NegativeCurveOptimizer);
        Assert.IsNull(decoded.AdvancedCpuTuning);
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(null, key));
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(complete, PresetKey.Create(ControlModeId.Custom3, 2)));
        var incomplete = System.Text.Json.Nodes.JsonNode.Parse(complete.Payload.GetRawText())!.AsObject();
        incomplete.Remove("negativeCurveOptimizer");
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(
            complete with { Payload = JsonSerializer.SerializeToElement(incomplete) }, key));
        Assert.HasCount(1, paths.WrittenPaths);
        Assert.IsFalse(paths.WrittenPaths.Any(path => path.Contains("hardware", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Corrupt_preset_json_is_quarantined_and_returns_factory_default()
    {
        var paths = new RecordingPathProvider();
        var directory = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "control-presets-performance.json");
        await File.WriteAllTextAsync(path, "{not-json");

        var loaded = await new ControlPresetStore(paths).LoadAsync(
            ControlPageId.Performance,
            PresetKey.Create(ControlModeId.Office, 1),
            CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.IsNotNull(SavedPerformancePreset.ReadDraft(loaded, loaded.Key));
        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(1, Directory.GetFiles(directory, "control-presets-performance.json.corrupt-*"));
    }

    [TestMethod]
    public async Task Saving_a_second_version_replaces_the_file_without_leaving_temp_files()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Gaming, 1);

        await store.SaveAsync(CreatePreset(key, "第一版", 42), CancellationToken.None);
        await store.SaveAsync(CreatePreset(key, "第二版", 84), CancellationToken.None);
        var loaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
        var directory = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("第二版", loaded.DisplayName);
        Assert.HasCount(1, Directory.GetFiles(directory, "control-presets-performance.json"));
        Assert.HasCount(0, Directory.GetFiles(directory, "control-presets-performance.json.tmp-*"));
    }

    private static PagePresetEnvelope CreatePreset(PresetKey key, string name, int value)
    {
        using var document = JsonDocument.Parse($"{{\"temperatureLimitC\":{value}}}");
        return new PagePresetEnvelope(1, ControlPageId.Performance, key, name, document.RootElement.Clone(), DateTimeOffset.UtcNow);
    }
}
