using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class CpuCurveModeTests
{
    private static PerformanceDraft Draft(int mode) => new()
    {
        NegativeCurveOptimizer = -9,
        AdvancedCpuTuning = JsonSerializer.Deserialize<AdvancedCpuTuningDraft>(
            "{\"CurveOptimizerMode\":" + mode + ",\"CurveOptimizerAll\":-12,\"PerCoreCurveOptimizer\":{\"0\":-7,\"1\":-8}}")
    };

    [TestMethod]
    public void Bios_mode_preserves_edits_but_emits_no_curve_write()
    {
        var draft = Draft(0);
        var plan = PerformanceCommandFactory.Create(draft, true).Plan;
        Assert.IsNull(plan.NegativeCurveOptimizer);
        Assert.IsNull(plan.Advanced!.CurveOptimizerAll);
        Assert.IsNull(plan.Advanced.PerCoreCurveOptimizer);
        Assert.AreEqual(-12, draft.AdvancedCpuTuning!.CurveOptimizerAll);
        Assert.AreEqual(-7, draft.AdvancedCpuTuning.PerCoreCurveOptimizer![0]);
    }

    [TestMethod]
    public void All_core_mode_emits_only_all_core_values()
    {
        var plan = PerformanceCommandFactory.Create(Draft(1), true).Plan;
        Assert.IsNull(plan.NegativeCurveOptimizer);
        Assert.AreEqual(-12, plan.Advanced!.CurveOptimizerAll);
        Assert.IsNull(plan.Advanced.PerCoreCurveOptimizer);
    }

    [TestMethod]
    public void Per_core_mode_emits_only_per_core_values()
    {
        var plan = PerformanceCommandFactory.Create(Draft(2), true).Plan;
        Assert.IsNull(plan.NegativeCurveOptimizer);
        Assert.IsNull(plan.Advanced!.CurveOptimizerAll);
        Assert.AreEqual(-8, plan.Advanced.PerCoreCurveOptimizer![1]);
    }

    [TestMethod]
    public void Ambiguous_legacy_curve_values_do_not_emit_overlapping_writes()
    {
        var draft = new PerformanceDraft { AdvancedCpuTuning = new()
        {
            CurveOptimizerAll = -12, PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -7 }
        }};
        var plan = PerformanceCommandFactory.Create(draft, true).Plan;
        Assert.IsNull(plan.NegativeCurveOptimizer);
        Assert.IsNull(plan.Advanced!.CurveOptimizerAll);
        Assert.IsNull(plan.Advanced.PerCoreCurveOptimizer);
    }

    [TestMethod]
    public void Command_boundary_rejects_multiple_curve_write_paths()
    {
        var command = PerformanceCommandFactory.Create(Draft(1), true);
        Assert.IsNotNull(CommandValidation.Validate(command with { Plan = command.Plan with
        {
            NegativeCurveOptimizer = -5,
            Advanced = new AdvancedCpuTuningPlan(CurveOptimizerAll: -12)
        }}));
        Assert.IsNotNull(CommandValidation.Validate(command with { Plan = command.Plan with
        {
            NegativeCurveOptimizer = null,
            Advanced = new AdvancedCpuTuningPlan(CurveOptimizerAll: -12,
                PerCoreCurveOptimizer: new Dictionary<int, int> { [0] = -7 })
        }}));
    }

    [TestMethod]
    public async Task All_three_saved_slots_survive_a_new_store_instance_with_inactive_values()
    {
        var paths = new RecordingPathProvider();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        var store = new ControlPresetStore(paths);
        try
        {
            for (int i = 0; i < 3; i++)
                await store.SaveAsync(new PagePresetEnvelope(1, ControlPageId.Performance,
                    PresetKey.Create(ControlModeId.Gaming, i + 1), $"自定义 {i}",
                    JsonSerializer.SerializeToElement(Draft(i), options), DateTimeOffset.UtcNow), CancellationToken.None);
            var reopened = new ControlPresetStore(paths);
            for (int i = 0; i < 3; i++)
            {
                var loaded = await reopened.LoadAsync(ControlPageId.Performance,
                    PresetKey.Create(ControlModeId.Gaming, i + 1), CancellationToken.None);
                Assert.IsNotNull(loaded);
                Assert.AreEqual($"自定义 {i}", loaded.DisplayName);
                var advanced = loaded.Payload.Deserialize<PerformanceDraft>(options)!.AdvancedCpuTuning!;
                Assert.AreEqual(i, JsonSerializer.SerializeToElement(advanced).GetProperty("CurveOptimizerMode").GetInt32());
                Assert.AreEqual(-12, advanced.CurveOptimizerAll);
                Assert.AreEqual(-8, advanced.PerCoreCurveOptimizer![1]);
            }
        }
        finally
        {
            if (Directory.Exists(paths.LocalAppDataRoot)) Directory.Delete(paths.LocalAppDataRoot, recursive: true);
        }
    }
}
