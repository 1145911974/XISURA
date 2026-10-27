using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class CurveOptimizerExtendedRangeTests
{
    [TestMethod]
    public void Negative_fifty_survives_readback_draft_preset_and_command_validation()
    {
        var state = new CpuTuningState(85, 45, 65, 4700, true, 8, null, -50, "hardwareReadback")
            { PerCoreCurveOptimizer = Enumerable.Range(0, 8).ToDictionary(core => core, _ => -50) };
        var advanced = AdvancedCpuTuningDraft.FromCurveReadback(state, null);
        Assert.AreEqual(-50, advanced.CurveOptimizerAll);
        foreach (var draft in new[]
        {
            new PerformanceDraft { NegativeCurveOptimizer = -50 },
            new PerformanceDraft { AdvancedCpuTuning = advanced },
            new PerformanceDraft { AdvancedCpuTuning = advanced with { CurveOptimizerMode = CpuCurveOptimizerMode.PerCore } }
        })
        {
            Assert.IsTrue(PerformanceDraftValidator.Validate(draft).IsValid);
            Assert.IsNull(CommandValidation.Validate(PerformanceCommandFactory.Create(draft, true)));
        }
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { NegativeCurveOptimizer = -51 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { NegativeCurveOptimizer = 1 }).IsValid);
    }

    [TestMethod]
    public void Advanced_drafts_use_the_supported_range_for_all_and_per_core_curves()
    {
        foreach (int value in new[] { -50, -30, 0, -51, 1, 30 })
        {
            bool expected = value is >= -50 and <= 0;
            foreach (var advanced in new[]
            {
                new AdvancedCpuTuningDraft { CurveOptimizerMode = CpuCurveOptimizerMode.AllCore, CurveOptimizerAll = value },
                new AdvancedCpuTuningDraft { CurveOptimizerMode = CpuCurveOptimizerMode.PerCore,
                    PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = value } }
            })
                Assert.AreEqual(expected, PerformanceDraftValidator.Validate(new PerformanceDraft
                    { AdvancedCpuTuning = advanced }).IsValid, $"{advanced.CurveOptimizerMode}: {value}");
        }
    }

    [TestMethod]
    public void Advanced_command_contract_uses_the_supported_range_for_all_and_per_core_curves()
    {
        foreach (int value in new[] { -50, -30, 0, -51, 1, 30 })
        {
            bool expected = value is >= -50 and <= 0;
            foreach (var advanced in new[]
            {
                new AdvancedCpuTuningPlan(CurveOptimizerAll: value),
                new AdvancedCpuTuningPlan(PerCoreCurveOptimizer: new Dictionary<int, int> { [0] = value })
            })
            {
                var command = new SetCpuTuningCommand(Guid.NewGuid(),
                    new CpuTuningPlan(null, null, null, null, null, null, null, null) { Advanced = advanced }, true);
                Assert.AreEqual(expected, CommandValidation.Validate(command) is null, $"{value}: {advanced}");
            }
        }
    }
}
