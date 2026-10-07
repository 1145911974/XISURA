using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class PerformanceWorkspace
{
    public async Task<AdaptiveAutomationPreset[]> ReadAutomaticServicePresetsAsync(
        IReadOnlyCollection<PresetKey> keys, CancellationToken cancellationToken)
    {
        var currentStore = new ControlPresetStore();
        var result = new List<AdaptiveAutomationPreset>();
        foreach (var key in keys.Distinct())
        {
            var saved = await currentStore.LoadAsync(ControlPageId.Performance, key, cancellationToken);
            var draft = SavedPerformancePreset.ReadDraft(saved, key);
            var state = session?.State?.Controls.CpuTuning;
            var pboScalar = draft.AdvancedCpuTuning?.PboScalar;
            if (pboScalar is not null && state?.PboScalar is not (>= 1 and <= 10))
                throw new InvalidOperationException("自动预设中的 PBO Scalar 缺少可恢复的本机读回值。");
            var curveMode = draft.AdvancedCpuTuning?.ResolveCurveOptimizerMode(draft.NegativeCurveOptimizer)
                ?? (draft.NegativeCurveOptimizer is null ? CpuCurveOptimizerMode.Bios : CpuCurveOptimizerMode.AllCore);
            int? curveAll = curveMode == CpuCurveOptimizerMode.AllCore
                ? draft.AdvancedCpuTuning?.CurveOptimizerAll ?? draft.NegativeCurveOptimizer : null;
            var curveCores = curveMode == CpuCurveOptimizerMode.PerCore ? draft.AdvancedCpuTuning?.PerCoreCurveOptimizer : null;
            if (curveMode != CpuCurveOptimizerMode.Bios && (state?.CurveOptimizerVerification != "hardwareReadback" ||
                !state.HasCompleteCurveValues() || state.PerCoreCurveOptimizer!.Values.Any(value => value > 0) ||
                curveMode == CpuCurveOptimizerMode.AllCore && curveAll is not (>= -30 and <= 0) ||
                curveMode == CpuCurveOptimizerMode.PerCore && (curveCores is not { Count: > 0 } ||
                    curveCores.Any(pair => pair.Key < 0 || pair.Key >= state.PerCoreCurveOptimizer!.Count || pair.Value is < -30 or > 0))))
                throw new InvalidOperationException("自动预设中的 CO 缺少有效目标或完整可恢复的硬件读回值。");
            CpuTuningPlan? plan = state is null ? null
                : PerformanceCommandFactory.CreateForReadBackOnly(draft, state, true).Plan with
                {
                    TemperatureLimitC = null, SplWatts = null, SpptWatts = null,
                    MaxFrequencyMhz = null, EnabledCoreCount = null, NegativeCurveOptimizer = null,
                    Advanced = pboScalar is null && curveAll is null && curveCores is null ? null :
                        new AdvancedCpuTuningPlan(PboScalar: pboScalar, CurveOptimizerAll: curveAll,
                            PerCoreCurveOptimizer: curveCores is null ? null : new Dictionary<int,int>(curveCores)),
                    AcMinActiveCoresPercent = state.AcMinActiveCoresPercent is not null && state.DcMinActiveCoresPercent is not null
                        ? draft.AcMinActiveCoresPercent : null,
                    DcMinActiveCoresPercent = state.AcMinActiveCoresPercent is not null && state.DcMinActiveCoresPercent is not null
                        ? draft.DcMinActiveCoresPercent : null
                };
            if (plan is not null && plan.MaxFrequencyMhz is null && plan.AcMaxFrequencyMhz is null
                && plan.DcMaxFrequencyMhz is null && plan.BoostEnabled is null && plan.WindowsPowerSchemeId is null
                && plan.AcMinActiveCoresPercent is null && plan.DcMinActiveCoresPercent is null && plan.Advanced is null)
                plan = null;
            result.Add(new(key, AdaptiveTargetMap.HardwareModeFor(key), plan));
        }
        return result.ToArray();
    }
}
