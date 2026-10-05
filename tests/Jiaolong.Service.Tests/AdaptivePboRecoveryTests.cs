using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using System.Text.Json;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class AdaptivePboRecoveryTests
{
    [TestMethod]
    public void Automatic_cpu_plans_allow_valid_PBO_but_reject_unrestorable_advanced_fields()
    {
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null)
        { Advanced = new AdvancedCpuTuningPlan(PboScalar: 2) };
        Assert.IsTrue(AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(plan));
        foreach (var advanced in new[]
        {
            new AdvancedCpuTuningPlan(PboScalar: 0),
            new AdvancedCpuTuningPlan(PboScalar: 11),
            new AdvancedCpuTuningPlan(PboScalar: 2, OcClockMhz: 4000),
            new AdvancedCpuTuningPlan(PboScalar: 2, CurveOptimizerAll: -31),
            new AdvancedCpuTuningPlan(PboScalar: 2, StapmWatts: 45)
        })
            Assert.IsFalse(AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(plan with { Advanced = advanced }));
        Assert.IsTrue(AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(plan with {
            Advanced = new(PboScalar: 2, CurveOptimizerAll: -5) }));
        Assert.IsTrue(AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(plan with {
            Advanced = new(PerCoreCurveOptimizer: new Dictionary<int,int> { [0] = -5 }) }));
        Assert.IsFalse(AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(plan with {
            Advanced = new(CurveOptimizerAll: -5, PerCoreCurveOptimizer: new Dictionary<int,int> { [0] = -5 }) }));
        var office = PresetKey.Create(ControlModeId.Office, 1);
        var game = PresetKey.Create(ControlModeId.Gaming, 1);
        var turbo = PresetKey.Create(ControlModeId.Turbo, 1);
        var custom = PresetKey.Create(ControlModeId.Custom1, 3);
        var configuration = new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
            new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
            new(custom, game, turbo, office, game),
            [new(custom, PerformanceMode.Turbo, plan), new(game, PerformanceMode.Balanced, null),
                new(turbo, PerformanceMode.Turbo, null), new(office, PerformanceMode.Quiet, null)]);
        AdaptiveAutomationConfigurationValidator.Validate(configuration);
        Assert.ThrowsExactly<ArgumentException>(() => AdaptiveAutomationConfigurationValidator.Validate(configuration with
        {
            Presets = configuration.Presets.Select(preset => preset.Key == custom
                ? preset with { PerformanceMode = PerformanceMode.Custom } : preset).ToArray()
        }));
        var unsafeDc = configuration with { TargetMap = configuration.TargetMap with { DcOffice = custom } };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AdaptiveAutomationConfigurationValidator.Validate(unsafeDc));
        AdaptiveAutomationConfigurationValidator.Validate(unsafeDc with { Enabled = false, Presets = [] });
    }

    [TestMethod]
    public void PBO_application_and_verification_require_a_valid_current_readback()
    {
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null)
        { Advanced = new AdvancedCpuTuningPlan(PboScalar: 2) };
        var state = new CpuTuningState(null, null, null, null, null, null, null, null, null);
        foreach (int? original in new int?[] { null, 0, 11 })
            Assert.IsFalse(AdaptiveAutomationWorker.CanApplyAndRestore(plan, state with { PboScalar = original }));
        Assert.IsTrue(AdaptiveAutomationWorker.CanApplyAndRestore(plan, state with { PboScalar = 1 }));
        Assert.IsFalse(AdaptiveAutomationWorker.CpuPlanMatches(plan, state with { PboScalar = 1 }));
        Assert.IsTrue(AdaptiveAutomationWorker.CpuPlanMatches(plan, state with { PboScalar = 2 }));
        var curve = plan with { Advanced = new(PerCoreCurveOptimizer: new Dictionary<int,int> { [0] = -29 }) };
        var captured = state with { CurveOptimizerVerification = "hardwareReadback",
            PerCoreCurveOptimizer = Enumerable.Range(0, 8).ToDictionary(core => core, _ => -30) };
        Assert.IsTrue(AdaptiveAutomationWorker.CanApplyAndRestore(curve, captured));
        Assert.IsFalse(AdaptiveAutomationWorker.CanApplyAndRestore(curve, captured with { CurveOptimizerVerification = "cached" }));
        Assert.IsFalse(AdaptiveAutomationWorker.CanApplyAndRestore(curve, captured with { PerCoreCurveOptimizer = new Dictionary<int,int> { [0] = -30 } }));
        Assert.IsFalse(AdaptiveAutomationWorker.CpuPlanMatches(curve, captured));
        var changed = new Dictionary<int,int>(captured.PerCoreCurveOptimizer!) { [0] = -29 };
        Assert.IsTrue(AdaptiveAutomationWorker.CpuPlanMatches(curve, captured with { PerCoreCurveOptimizer = changed }));
    }

    [TestMethod]
    public void Recovery_roundtrip_preserves_the_actual_PBO_original_and_legacy_CPU_journal()
    {
        var state = new CpuTuningState(null, null, null, null, true, null, null, null, null) { PboScalar = 1 };
        var original = AdaptiveAutomationWorker.CaptureRestorableCpuPlan(state);
        var recovery = new AdaptiveAutomationRecovery(PerformanceMode.Turbo, original, PerformanceMode.Balanced, true, true);
        var restored = JsonSerializer.Deserialize<AdaptiveAutomationRecovery>(JsonSerializer.Serialize(recovery));
        Assert.AreEqual(1, restored?.OriginalCpuWindowsTuning?.Advanced?.PboScalar);
        Assert.IsTrue(AdaptiveAutomationWorker.CanApplyAndRestore(restored!.OriginalCpuWindowsTuning!, state with { PboScalar = 2 }));
        Assert.IsFalse(AdaptiveAutomationWorker.CpuPlanMatches(restored.OriginalCpuWindowsTuning!, state with { PboScalar = 2 }));
        Assert.IsTrue(AdaptiveAutomationWorker.CpuPlanMatches(restored.OriginalCpuWindowsTuning!, state));
        var legacy = original! with { Advanced = null };
        Assert.IsTrue(AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(legacy));
        Assert.IsNull(AdaptiveAutomationWorker.CaptureRestorableCpuPlan(state with { BoostEnabled = null, PboScalar = null }));
    }
}
