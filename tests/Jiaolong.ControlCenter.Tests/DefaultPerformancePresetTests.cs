using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class DefaultPerformancePresetTests
{
    [TestMethod]
    public async Task Reset_restores_only_requested_slot_and_preserves_saved_name()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Custom2, 2);
        var other = PresetKey.Create(ControlModeId.Custom2, 1);
        var original = DefaultPerformancePresets.Create(key);
        var otherPreset = DefaultPerformancePresets.Create(other) with { DisplayName = "保留" };
        await store.SaveAsync(original with { DisplayName = "我的安静", Payload = JsonSerializer.SerializeToElement(new { temperatureLimitC = 99 }) }, CancellationToken.None);
        await store.SaveAsync(otherPreset, CancellationToken.None);
        var reset = await store.ResetPerformanceAsync(key, CancellationToken.None);
        Assert.AreEqual("我的安静", reset.DisplayName);
        Assert.AreEqual(original.Payload.GetRawText(), reset.Payload.GetRawText());
        var reloaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
        Assert.IsTrue(JsonElement.DeepEquals(reset.Payload, reloaded!.Payload));
        var untouched = await store.LoadAsync(ControlPageId.Performance, other, CancellationToken.None);
        Assert.AreEqual(otherPreset.DisplayName, untouched!.DisplayName);
        Assert.IsTrue(JsonElement.DeepEquals(otherPreset.Payload, untouched.Payload));
        Assert.AreEqual(otherPreset.SavedAtUtc, untouched.SavedAtUtc);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.ResetPerformanceAsync(key, new CancellationToken(true)));
    }

    [TestMethod]
    public async Task Missing_slots_have_eighteen_valid_distinct_identities_without_writes()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        foreach (var key in PresetKey.All)
        {
            var preset = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
            Assert.IsNotNull(preset, key.ToString());
            Assert.AreEqual(key, preset.Key);
            var draft = SavedPerformancePreset.ReadDraft(preset, key);
            Assert.IsTrue(PerformanceDraftValidator.Validate(draft).IsValid);
            Assert.IsTrue(draft.SpptWatts >= draft.SplWatts);
            Assert.IsTrue(draft.SplWatts is >= 45 and <= 75 && draft.SpptWatts is >= 45 and <= 75);
            Assert.IsNull(draft.NegativeCurveOptimizer);
            Assert.IsNull(draft.AdvancedCpuTuning);
            Assert.AreNotEqual(Guid.Empty, draft.WindowsPowerSchemeId);
        }
        foreach (var mode in Enum.GetValues<ControlModeId>())
        {
            var tiers = Enumerable.Range(1, 3).Select(slot => DefaultPerformancePresets.CreateDraft(PresetKey.Create(mode, slot))).ToArray();
            Assert.HasCount(3, tiers.Distinct().ToArray());
            Assert.IsTrue(tiers[0].SplWatts <= tiers[1].SplWatts && tiers[1].SplWatts <= tiers[2].SplWatts);
        }
        var office = DefaultPerformancePresets.CreateDraft(PresetKey.Create(ControlModeId.Office, 2));
        var gaming = DefaultPerformancePresets.CreateDraft(PresetKey.Create(ControlModeId.Gaming, 2));
        var turbo = DefaultPerformancePresets.CreateDraft(PresetKey.Create(ControlModeId.Turbo, 2));
        Assert.IsTrue(office.AcMaxFrequencyMhz < gaming.AcMaxFrequencyMhz && gaming.AcMaxFrequencyMhz < turbo.AcMaxFrequencyMhz);
        Assert.IsTrue(office.AcMinActiveCoresPercent < gaming.AcMinActiveCoresPercent);
        Assert.AreEqual(75, turbo.SplWatts);
        Assert.AreEqual(75, turbo.SpptWatts);
        Assert.AreEqual(5100, turbo.AcMaxFrequencyMhz);
        Assert.HasCount(0, paths.WrittenPaths);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.LoadAsync(ControlPageId.Performance, PresetKey.All[0], canceled.Token));
        Assert.IsNotNull(await store.LoadAsync(ControlPageId.Fan, PresetKey.All[0], CancellationToken.None));
    }

    [TestMethod]
    public async Task Saved_slot_wins_and_default_siblings_do_not_rewrite_it()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Custom2, 2);
        var saved = new PagePresetEnvelope(1, ControlPageId.Performance, key, "我的配置",
            JsonSerializer.SerializeToElement(new { temperatureLimitC = 81 }), DateTimeOffset.UtcNow);
        await store.SaveAsync(saved, CancellationToken.None);
        var path = paths.WrittenPaths.Single();
        var bytes = await File.ReadAllBytesAsync(path);
        foreach (var candidate in PresetKey.All)
            Assert.IsNotNull(await store.LoadAsync(ControlPageId.Performance, candidate, CancellationToken.None));
        var loaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
        Assert.AreEqual("我的配置", loaded!.DisplayName);
        Assert.AreEqual(81, loaded.Payload.GetProperty("temperatureLimitC").GetInt32());
        Assert.AreEqual(81, SavedPerformancePreset.ReadDraft(loaded, key).TemperatureLimitC);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.HasCount(1, paths.WrittenPaths);
    }
}
