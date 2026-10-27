using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformancePresetProjectionTests
{
    [TestMethod]
    public void Unknown_rows_stay_blank_while_known_keep_and_legacy_values_remain_visible_after_save()
    {
        var partial = new PerformanceDraft
        {
            TemperatureLimitC = 90,
            KnownFields = ["temperatureLimitC"],
            SettingRequests = new()
            {
                ["temperatureLimitC"] = SettingRequest.Keep,
                ["splWatts"] = SettingRequest.Keep
            }
        };

        Assert.IsTrue(PerformancePresetProjection.IsKnown(partial, nameof(PerformanceDraft.TemperatureLimitC)));
        Assert.AreEqual(90d, PerformancePresetProjection.NumericValue(partial, "temperatureLimitC", partial.TemperatureLimitC));
        Assert.IsFalse(PerformancePresetProjection.IsKnown(partial, "splWatts"));
        Assert.IsNull(PerformancePresetProjection.NumericValue(partial, "splWatts", partial.SplWatts));
        Assert.IsNull(PerformancePresetProjection.NumericValue(partial, "temperatureLimitC", null));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<PerformanceDraft>(JsonSerializer.Serialize(partial, options), options)!;
        Assert.IsFalse(PerformancePresetProjection.IsKnown(restored, "splWatts"));
        Assert.AreEqual(90d, PerformancePresetProjection.NumericValue(restored, "temperatureLimitC", restored.TemperatureLimitC));

        var legacy = new PerformanceDraft();
        Assert.AreEqual(45d, PerformancePresetProjection.NumericValue(legacy, "splWatts", legacy.SplWatts));
        var manual = PerformancePresetProjection.ApplyEdits(legacy, legacy with { SplWatts = 52 }, ["splWatts"]);
        Assert.IsNull(manual.KnownFields);
        Assert.IsNull(manual.SettingRequests);
        Assert.AreEqual(52, manual.SplWatts);
    }

    [TestMethod]
    public void Explicit_same_placeholder_edit_becomes_set_without_losing_other_values_intents_or_provenance()
    {
        var original = new PerformanceDraft
        {
            TemperatureLimitC = 90,
            SplWatts = 45,
            SpptWatts = 70,
            KnownFields = ["spptWatts", "TemperatureLimitC", "temperatureLimitC"],
            SettingRequests = new()
            {
                ["temperatureLimitC"] = SettingRequest.Official,
                ["splWatts"] = SettingRequest.Keep,
                ["spptWatts"] = SettingRequest.Set(70)
            },
            AdvancedCpuTuning = new() { PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -20 } }
        };
        var edited = original with { TemperatureLimitC = 99, SpptWatts = 100 };
        var result = PerformancePresetProjection.ApplyEdits(original, edited, [nameof(PerformanceDraft.SplWatts)]);

        Assert.AreEqual(90, result.TemperatureLimitC);
        Assert.AreEqual(70, result.SpptWatts);
        Assert.AreEqual(45, result.SplWatts);
        Assert.AreEqual(SettingIntent.Set, result.SettingRequests!["splWatts"].Intent);
        Assert.AreEqual(45, result.SettingRequests["splWatts"].Value!.Value.GetInt32());
        Assert.AreEqual(SettingIntent.Official, result.SettingRequests["temperatureLimitC"].Intent);
        Assert.AreEqual(70, result.SettingRequests["spptWatts"].Value!.Value.GetInt32());
        CollectionAssert.AreEqual(new[] { "splWatts", "spptWatts", "temperatureLimitC" }, result.KnownFields);
        Assert.AreEqual(SettingIntent.Keep, original.SettingRequests!["splWatts"].Intent);
        Assert.IsFalse(PerformancePresetProjection.IsKnown(original, "splWatts"));
        Assert.AreNotSame(original.SettingRequests, result.SettingRequests);
        Assert.AreNotSame(original.KnownFields, result.KnownFields);
        Assert.AreNotSame(original.AdvancedCpuTuning!.PerCoreCurveOptimizer, result.AdvancedCpuTuning!.PerCoreCurveOptimizer);

        Assert.IsTrue(PerformancePresetProjection.Equivalent(result, result with
        {
            KnownFields = ["TemperatureLimitC", "spptWatts", "splWatts", "splWatts"],
            SettingRequests = new()
            {
                ["spptWatts"] = SettingRequest.Set(70),
                ["temperatureLimitC"] = SettingRequest.Official,
                ["splWatts"] = SettingRequest.Set(45)
            }
        }));
        Assert.IsFalse(PerformancePresetProjection.Equivalent(result, result with { SplWatts = 46 }));
        Assert.IsFalse(PerformancePresetProjection.Equivalent(result, result with { KnownFields = ["spptWatts", "temperatureLimitC"] }));
        Assert.IsFalse(PerformancePresetProjection.Equivalent(result, result with { SettingRequests = original.SettingRequests }));
        Assert.IsFalse(PerformancePresetProjection.Equivalent(result, result with
        {
            SettingRequests = new(result.SettingRequests) { ["splWatts"] = SettingRequest.Set(46) }
        }));
        Assert.AreEqual(SettingIntent.Set, result.SettingRequests["SPLWATTS"].Intent);
        var boost = PerformancePresetProjection.ApplyEdits(original,
            original with { IsBoostEnabled = true, BoostPolicy = new(1, 3) }, ["isBoostEnabled"]);
        Assert.IsTrue(CpuBoostPolicy.TryRead(boost.SettingRequests!["isBoostEnabled"].Value!.Value, out var exact));
        Assert.AreEqual(new CpuBoostPolicy(1, 3), exact);
        var mixed = original with { AdvancedCpuTuning = new() { PboScalar = 2 }, NegativeCurveOptimizer = -50,
            SettingRequests = new(original.SettingRequests) { ["advancedCpuTuning"] = SettingRequest.Keep,
                ["negativeCurveOptimizer"] = SettingRequest.Set(-50) } };
        var curveOnly = new AdvancedCpuTuningDraft { CurveOptimizerMode = CpuCurveOptimizerMode.AllCore, CurveOptimizerAll = -30 };
        var advancedEdit = PerformancePresetProjection.ApplyEdits(mixed, mixed, ["advancedCpuTuning"], curveOnly);
        Assert.AreEqual(2, advancedEdit.AdvancedCpuTuning!.PboScalar);
        var target = advancedEdit.SettingRequests!["advancedCpuTuning"].Value!.Value.Deserialize<AdvancedCpuTuningDraft>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.IsNull(target.PboScalar);
        Assert.AreEqual(-30, target.CurveOptimizerAll);
        Assert.IsNull(advancedEdit.NegativeCurveOptimizer);
        Assert.AreEqual(SettingIntent.Keep, advancedEdit.SettingRequests["negativeCurveOptimizer"].Intent);
        var policyFlag = PerformancePresetProjection.ApplyEdits(original,
            original with { TemperatureLimitC = 91, UseOfficialCpuPolicy = false }, ["temperatureLimitC", "useOfficialCpuPolicy"]);
        Assert.IsFalse(policyFlag.KnownFields!.Contains("useOfficialCpuPolicy"));
        Assert.IsFalse(policyFlag.SettingRequests!.ContainsKey("useOfficialCpuPolicy"));
        var modeOnly = PerformancePresetProjection.ApplyEdits(advancedEdit,
            advancedEdit with { AdvancedCpuTuning = new() { PboScalar = 9 } }, ["advancedCpuTuning"]);
        Assert.IsTrue(PerformancePresetProjection.Equivalent(advancedEdit, modeOnly));
    }
}
