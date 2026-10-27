using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PartialPerformanceSaveTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (PagePresetEnvelope Preset, PerformanceDraft Draft) Partial()
    {
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = DefaultPerformancePresets.Create(key);
        var draft = SavedPerformancePreset.ReadDraft(preset, key);
        draft.KnownFields = ["acMaxFrequencyMhz", "dcMaxFrequencyMhz", "isBoostEnabled", "windowsPowerSchemeId"];
        draft.SettingRequests = preset.Settings.ToDictionary(pair => pair.Key, _ => SettingRequest.Keep, StringComparer.OrdinalIgnoreCase);
        return (preset with { Payload = JsonSerializer.SerializeToElement(draft, Json), Settings = new(draft.SettingRequests) }, draft);
    }

    [TestMethod]
    public async Task Explicit_same_value_edit_survives_autosave_flush_and_snapshot_mutation()
    {
        var (previous, source) = Partial();
        var edited = PerformancePresetProjection.ApplyEdits(source, source, ["temperatureLimitC"]);
        var payload = JsonSerializer.SerializeToElement(edited, Json);
        var merged = PresetCatalogController.MergeDraft(previous, previous.DisplayName, payload, performanceDraft: edited);
        Assert.AreEqual(SettingIntent.Set, merged.Settings["temperatureLimitC"].Intent);
        Assert.AreEqual(SettingIntent.Keep, merged.Settings["splWatts"].Intent);
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        await store.SaveAsync(previous, default);
        await using var session = new HomeControlSession(new ControlCenterClient("unused-partial-save-" + Guid.NewGuid().ToString("N")),
            automaticRestore: new AppliedConfigurationRestore(paths));
        var catalog = new PresetCatalogController(session, store, new UserPreferencesStore(paths));
        catalog.ScheduleSave(previous.Page, previous.Key, previous.DisplayName, payload, performanceDraft: edited);
        edited.KnownFields![0] = "splWatts";
        edited.SettingRequests!["temperatureLimitC"] = SettingRequest.Keep;
        await catalog.FlushAsync();
        var saved = await store.LoadAsync(previous.Page, previous.Key, default);
        Assert.IsNotNull(saved);
        var reopened = SavedPerformancePreset.ReadDraft(saved, previous.Key);
        Assert.AreEqual(SettingIntent.Set, reopened.SettingRequests!["temperatureLimitC"].Intent);
        Assert.AreEqual(source.TemperatureLimitC, reopened.TemperatureLimitC);
        Assert.IsTrue(PerformancePresetProjection.IsKnown(reopened, "temperatureLimitC"));
        Assert.IsFalse(PerformancePresetProjection.IsKnown(reopened, "splWatts"));
        Assert.AreEqual(SettingIntent.Keep, reopened.SettingRequests["spptWatts"].Intent);

        var retried = PerformancePresetProjection.ApplyEdits(reopened, reopened, ["spptWatts"]);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NotificationRequested += message => { if (message.Contains("自动保存失败")) failure.TrySetResult(); };
        using (var fileLock = new FileStream(paths.WrittenPaths[0], FileMode.Open, FileAccess.Read, FileShare.None))
        {
            catalog.ScheduleSave(previous.Page, previous.Key, previous.DisplayName,
                JsonSerializer.SerializeToElement(retried, Json), performanceDraft: retried);
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retried.SettingRequests!["spptWatts"] = SettingRequest.Keep;
        }
        await catalog.FlushAsync();
        var afterRetry = await store.LoadAsync(previous.Page, previous.Key, default);
        Assert.IsNotNull(afterRetry);
        Assert.AreEqual(SettingIntent.Set, afterRetry.Settings["spptWatts"].Intent);
        Assert.IsTrue(PerformancePresetProjection.IsKnown(SavedPerformancePreset.ReadDraft(afterRetry, previous.Key), "spptWatts"));
    }

    [TestMethod]
    public void Saved_partial_provenance_rejects_legacy_markers_and_unknown_set_values()
    {
        var (preset, draft) = Partial();
        var forged = preset with { Settings = new(preset.Settings) { ["splWatts"] = SettingRequest.Set(45) } };
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(forged, preset.Key));
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(preset with { SchemaVersion = 2 }, preset.Key));
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(preset with { Settings = [] }, preset.Key));
        var coreEdit = new AdvancedCpuTuningDraft { CurveOptimizerMode = CpuCurveOptimizerMode.PerCore,
            PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -20 } };
        draft.AdvancedCpuTuning = new() { PboScalar = 2, CurveOptimizerMode = CpuCurveOptimizerMode.PerCore,
            PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -10, [1] = -30 } };
        var changed = PerformancePresetProjection.ApplyEdits(draft, draft, ["advancedCpuTuning"], coreEdit);
        var subset = PresetCatalogController.MergeDraft(preset, preset.DisplayName,
            JsonSerializer.SerializeToElement(changed, Json), performanceDraft: changed);
        Assert.IsTrue(PresetValidation.Validate(subset).IsValid);
        var restored = SavedPerformancePreset.ReadDraft(subset, subset.Key);
        Assert.AreEqual(-30, restored.AdvancedCpuTuning!.PerCoreCurveOptimizer![1]);
        var target = restored.SettingRequests!["advancedCpuTuning"].Value!.Value.Deserialize<AdvancedCpuTuningDraft>(Json)!;
        Assert.AreEqual(1, target.PerCoreCurveOptimizer!.Count);
        Assert.IsNull(target.PboScalar);
        var wideSource = draft with { SpptWatts = 90, KnownFields = draft.KnownFields!.Concat(["spptWatts"]).ToArray() };
        var temperatureOnly = PerformancePresetProjection.ApplyEdits(wideSource,
            wideSource with { TemperatureLimitC = 95, UseOfficialCpuPolicy = false }, ["temperatureLimitC", "useOfficialCpuPolicy"]);
        var widePreset = PresetCatalogController.MergeDraft(preset, preset.DisplayName,
            JsonSerializer.SerializeToElement(temperatureOnly, Json), performanceDraft: temperatureOnly);
        Assert.IsTrue(PresetValidation.Validate(widePreset).IsValid);
        var commands = PerformanceCommandFactory.CreatePresetCommands(temperatureOnly,
            new(85, 50, 90, 4700, true, 8, draft.WindowsPowerSchemeId, null, null), true, false, false, false);
        Assert.IsNull(commands.Error);
        Assert.HasCount(1, commands.Steps);
        Assert.AreEqual(95, commands.Steps[0].Command.Plan.TemperatureLimitC);
        Assert.IsNull(commands.Steps[0].Command.Plan.SpptWatts);
        var legacyWide = temperatureOnly with { KnownFields = null };
        Assert.IsTrue(PerformanceDraftValidator.Validate(legacyWide).IsValid);
        Assert.IsNull(PerformanceCommandFactory.CreatePresetCommands(legacyWide,
            new(85, 50, 90, 4700, true, 8, draft.WindowsPowerSchemeId, null, null), true, false, false, false).Error);
        Assert.IsFalse(PresetValidation.Validate(widePreset with { Settings = new(widePreset.Settings)
            { ["spptWatts"] = SettingRequest.Set(90) } }).IsValid);
    }
}
