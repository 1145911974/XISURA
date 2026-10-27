using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class CurvePresetModeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task Bios_selection_survives_save_reopen_without_replaying_saved_curve()
    {
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = DefaultPerformancePresets.Create(key);
        var source = SavedPerformancePreset.ReadDraft(preset, key) with
        {
            UseOfficialCpuPolicy = false,
            AdvancedCpuTuning = new() { CurveOptimizerMode = CpuCurveOptimizerMode.AllCore, CurveOptimizerAll = -12, PboScalar = 2 }
        };
        source.SettingRequests = null;
        source.KnownFields = null;
        var target = PerformancePresetProjection.AdvancedEditTarget(null, CpuCurveOptimizerMode.Bios);
        Assert.IsNotNull(target, "An explicit BIOS selection must survive without a numeric edit");
        Assert.IsNull(target.CurveOptimizerAll);
        Assert.IsNull(target.PerCoreCurveOptimizer);
        foreach (bool partial in new[] { false, true })
        {
            if (partial)
            {
                source.KnownFields = ["advancedCpuTuning"];
                source.SettingRequests = preset.Settings.ToDictionary(pair => pair.Key, _ => SettingRequest.Keep,
                    StringComparer.OrdinalIgnoreCase);
                source.SettingRequests["advancedCpuTuning"] = SettingRequest.Set(new AdvancedCpuTuningDraft
                    { CurveOptimizerMode = CpuCurveOptimizerMode.AllCore, CurveOptimizerAll = -12 });
            }
            var edited = PerformancePresetProjection.ApplyEdits(source, source, ["advancedCpuTuning"], target);
            var merged = PresetCatalogController.MergeDraft(preset, preset.DisplayName,
                JsonSerializer.SerializeToElement(edited, Json), performanceDraft: edited);
            Assert.IsTrue(PresetValidation.Validate(merged).IsValid);
            var paths = new RecordingPathProvider();
            await new ControlPresetStore(paths).SaveAsync(merged, default);
            var saved = await new ControlPresetStore(paths).LoadAsync(merged.Page, key, default);
            Assert.IsNotNull(saved);
            var reopened = SavedPerformancePreset.ReadDraft(saved, key);
            Assert.AreEqual(CpuCurveOptimizerMode.Bios, reopened.AdvancedCpuTuning!.CurveOptimizerMode);
            Assert.AreEqual(-12, reopened.AdvancedCpuTuning.CurveOptimizerAll);
            Assert.AreEqual(2, reopened.AdvancedCpuTuning.PboScalar);
            var plan = PerformanceCommandFactory.Create(reopened, true).Plan;
            Assert.IsNull(plan.NegativeCurveOptimizer);
            Assert.IsNull(plan.Advanced!.CurveOptimizerAll);
            Assert.IsNull(plan.Advanced.PerCoreCurveOptimizer);
            if (partial)
            {
                Assert.IsNull(plan.Advanced.PboScalar, "Saved display values must not become new write targets");
                Assert.AreEqual(SettingIntent.Keep, saved.Settings["splWatts"].Intent);
            }
            else Assert.AreEqual(2, plan.Advanced.PboScalar);
        }
    }

    [TestMethod]
    public void Editor_selection_does_not_convert_stale_values_or_drop_unrelated_edits()
    {
        foreach (var mode in new CpuCurveOptimizerMode?[] { null, CpuCurveOptimizerMode.AllCore, CpuCurveOptimizerMode.PerCore })
            Assert.IsNull(PerformancePresetProjection.AdvancedEditTarget(null, mode));
        var numeric = new AdvancedCpuTuningDraft { PptWatts = 60 };
        Assert.AreSame(numeric, PerformancePresetProjection.AdvancedEditTarget(numeric, null));
        var bios = PerformancePresetProjection.AdvancedEditTarget(numeric, CpuCurveOptimizerMode.Bios)!;
        Assert.AreEqual(CpuCurveOptimizerMode.Bios, bios.CurveOptimizerMode);
        Assert.AreEqual(60, bios.PptWatts);
        Assert.IsNull(bios.CurveOptimizerAll);
        Assert.IsNull(bios.PerCoreCurveOptimizer);
        Assert.IsNull(numeric.CurveOptimizerMode);
    }
}
