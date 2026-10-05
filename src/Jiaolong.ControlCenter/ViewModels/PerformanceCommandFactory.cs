using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.ViewModels;

public sealed record PerformancePresetCommandStep(
    string Label,
    SetCpuTuningCommand Command,
    CpuTuningPlan? ReadbackPlan = null);

public sealed record PerformancePresetCommandPlan(
    IReadOnlyList<PerformancePresetCommandStep> Steps,
    IReadOnlyList<string> Skipped,
    string? Error = null);

public static class PerformanceCommandFactory
{
    public static bool HasPresetTargets(PerformanceDraft draft) =>
        draft.TemperatureLimitC > 0 || draft.SplWatts > 0 || draft.SpptWatts > 0 ||
        draft.AdvancedCpuTuning is not null || draft.NegativeCurveOptimizer is not null;

    public static PerformancePresetCommandPlan CreatePresetCommands(
        PerformanceDraft draft,
        CpuTuningState? state,
        bool cpuTuningAvailable,
        bool smuAvailable,
        bool pboAvailable,
        bool curveOptimizerAvailable,
        bool forceApply = false)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var steps = new List<PerformancePresetCommandStep>();
        var skipped = new List<string>();
        if (state is null || !cpuTuningAvailable)
            return new(steps, skipped, "CPU 调校能力不可用");

        var validation = PerformanceDraftValidator.Validate(draft);
        if (!validation.IsValid)
            return new(steps, skipped, validation.Errors[0]);
        if (draft.TemperatureLimitC is < 45 or > 100 || draft.SplWatts is < 45 or > 75 || draft.SpptWatts is < 45 or > 75)
            return new(steps, skipped, "OEM 温度墙须为 45–100°C，SPL/SPPT 须为 45–75W；未发送任何命令");

        AdvancedCpuTuningDraft? advanced = draft.AdvancedCpuTuning;
        if (advanced is not null &&
            ((advanced.FastPptWatts is double fastTarget && fastTarget != draft.SpptWatts) ||
             (advanced.PptWatts is double pptTarget && pptTarget != draft.SpptWatts)))
            return new(steps, skipped, "SPPT、Fast PPT 和 PPT 在本机为同一限值，请设置一致");
        if (advanced is not null &&
            ((advanced.Mp1TemperatureC is int mp1Target && mp1Target != draft.TemperatureLimitC) ||
             (advanced.RsmuTemperatureC is int rsmuTarget && rsmuTarget != draft.TemperatureLimitC)))
            return new(steps, skipped, "温度墙、MP1 和 RSMU 在本机为同一温限，请设置一致");
        if (advanced?.VrmCurrentMilliamps is int vrmTarget && advanced.TdcCurrentMilliamps is int tdcTarget && vrmTarget != tdcTarget)
            return new(steps, skipped, "VRM 与 TDC 在本机为同一电流限值，请设置一致");
        if (advanced is not null &&
            (advanced.StapmWatts is < 45 or > 75 || advanced.FastPptWatts is < 45 or > 75 ||
             advanced.SlowPptWatts is < 45 or > 75 || advanced.PptWatts is < 45 or > 75 ||
             advanced.VrmCurrentMilliamps is < 1_000 or > 200_000 ||
             advanced.TdcCurrentMilliamps is < 1_000 or > 200_000 ||
             advanced.EdcCurrentMilliamps is < 1_000 or > 200_000 ||
             advanced.Mp1TemperatureC is < 40 or > 100 || advanced.RsmuTemperatureC is < 40 or > 100))
            return new(steps, skipped, "高级 CPU 参数超出当前控件保护范围；未发送任何命令");

        var windowsPlan = new CpuTuningPlan(null, null, null, null, null, null, null, null);
        bool schemeChanges = draft.WindowsPowerSchemeId != Guid.Empty && state.WindowsPowerSchemeId is { } scheme
            && scheme != draft.WindowsPowerSchemeId;
        if (state.AcFrequency() is int oldAc)
        {
            if (forceApply || schemeChanges || oldAc != draft.AcMaxFrequencyMhz)
                windowsPlan = windowsPlan with { AcMaxFrequencyMhz = draft.AcMaxFrequencyMhz };
        }
        else skipped.Add("交流最高频率：当前不可读回");

        if (state.DcFrequency() is int oldDc)
        {
            if (forceApply || schemeChanges || oldDc != draft.DcMaxFrequencyMhz)
                windowsPlan = windowsPlan with { DcMaxFrequencyMhz = draft.DcMaxFrequencyMhz };
        }
        else skipped.Add("电池最高频率：当前不可读回");

        // The transport fills a missing side from the other; always preserve the saved pair.
        if (windowsPlan.AcMaxFrequencyMhz is not null || windowsPlan.DcMaxFrequencyMhz is not null)
            windowsPlan = state.AcFrequency() is not null && state.DcFrequency() is not null
                ? windowsPlan with { AcMaxFrequencyMhz = draft.AcMaxFrequencyMhz, DcMaxFrequencyMhz = draft.DcMaxFrequencyMhz }
                : windowsPlan with { AcMaxFrequencyMhz = null, DcMaxFrequencyMhz = null };

        if (state.BoostEnabled is bool oldBoost)
        {
            if (forceApply || schemeChanges || oldBoost != draft.IsBoostEnabled)
                windowsPlan = windowsPlan with { BoostEnabled = draft.IsBoostEnabled };
        }
        else skipped.Add("Boost：当前不可读回");

        if (draft.WindowsPowerSchemeId != Guid.Empty)
        {
            if (state.WindowsPowerSchemeId is Guid oldScheme)
            {
                if (forceApply || oldScheme != draft.WindowsPowerSchemeId)
                    windowsPlan = windowsPlan with { WindowsPowerSchemeId = draft.WindowsPowerSchemeId };
            }
            else skipped.Add("Windows 电源方案：当前不可读回");
        }

        if (draft.AcMinActiveCoresPercent.HasValue != draft.DcMinActiveCoresPercent.HasValue)
            return new(steps, skipped, "AC/DC 核心停泊比例必须同时设置");
        if (draft.AcMinActiveCoresPercent is int targetAcParking && draft.DcMinActiveCoresPercent is int targetDcParking)
        {
            if (targetAcParking is < 0 or > 100 || targetDcParking is < 0 or > 100)
                return new(steps, skipped, "核心停泊比例必须在 0–100% 之间");
            if (state.AcMinActiveCoresPercent is int oldAcParking && state.DcMinActiveCoresPercent is int oldDcParking)
            {
                if (forceApply || schemeChanges || oldAcParking != targetAcParking || oldDcParking != targetDcParking)
                    windowsPlan = windowsPlan with
                    {
                        AcMinActiveCoresPercent = targetAcParking,
                        DcMinActiveCoresPercent = targetDcParking
                    };
            }
            else skipped.Add("核心停泊比例：当前不可读回");
        }

        if (HasPlanValues(windowsPlan))
            steps.Add(Step("Windows 频率/Boost/电源方案/停泊", windowsPlan, windowsPlan));

        var temperaturePlan = new CpuTuningPlan(draft.TemperatureLimitC, null, null, null, null, null, null, null);
        var splPlan = new CpuTuningPlan(null, draft.SplWatts, null, null, null, null, null, null);
        var spptPlan = new CpuTuningPlan(null, null, draft.SpptWatts, null, null, null, null, null);
        steps.Add(Step("温度墙", temperaturePlan, temperaturePlan));
        steps.Add(Step("持续功耗 SPL", splPlan, splPlan));
        steps.Add(Step("短时功耗 SPPT", spptPlan, spptPlan));

        if (advanced is not null)
        {
            if (HasSmuFields(advanced) && !smuAvailable)
                return new([], skipped, "预设包含 SMU 字段，但当前 SMU 能力不可用；未发送任何命令");
            if (advanced.PboScalar is not null && !pboAvailable)
                return new([], skipped, "预设包含 PBO Scalar，但当前 PBO 能力不可用；未发送任何命令");

            AddSmuStep(steps, "STAPM", advanced.StapmWatts, plan => plan with { StapmWatts = advanced.StapmWatts });
            AddSmuStep(steps, "Fast PPT", advanced.FastPptWatts, plan => plan with { FastPptWatts = advanced.FastPptWatts });
            AddSmuStep(steps, "Slow PPT", advanced.SlowPptWatts, plan => plan with { SlowPptWatts = advanced.SlowPptWatts });
            AddSmuStep(steps, "PPT", advanced.PptWatts, plan => plan with { PptWatts = advanced.PptWatts });
            AddSmuStep(steps, "VRM 电流", advanced.VrmCurrentMilliamps, plan => plan with { VrmCurrentMilliamps = advanced.VrmCurrentMilliamps });
            AddSmuStep(steps, "TDC 电流", advanced.TdcCurrentMilliamps, plan => plan with { TdcCurrentMilliamps = advanced.TdcCurrentMilliamps });
            AddSmuStep(steps, "EDC 电流", advanced.EdcCurrentMilliamps, plan => plan with { EdcCurrentMilliamps = advanced.EdcCurrentMilliamps });
            AddSmuStep(steps, "MP1 温度", advanced.Mp1TemperatureC, plan => plan with { Mp1TemperatureC = advanced.Mp1TemperatureC });
            AddSmuStep(steps, "RSMU 温度", advanced.RsmuTemperatureC, plan => plan with { RsmuTemperatureC = advanced.RsmuTemperatureC });

            if (advanced.PboScalar is int scalar)
            {
                if (state.PboScalar is not int oldScalar)
                    return new([], skipped, "PBO Scalar 缺少原值读回，未发送任何命令");
                if (scalar is < 1 or > 10)
                    return new([], skipped, "PBO Scalar 必须在 1–10 之间");
                if (forceApply || scalar != oldScalar)
                {
                    var pboPlan = new CpuTuningPlan(null, null, null, null, null, null, null, null)
                    { Advanced = new AdvancedCpuTuningPlan(PboScalar: scalar) };
                    steps.Add(Step("PBO Scalar", pboPlan, pboPlan));
                }
            }

            CpuCurveOptimizerMode curveMode = advanced.ResolveCurveOptimizerMode(draft.NegativeCurveOptimizer);
            if (curveMode == CpuCurveOptimizerMode.AllCore)
            {
                int? curve = advanced.CurveOptimizerAll ?? draft.NegativeCurveOptimizer;
                if (curve is int value)
                {
                    if (!curveOptimizerAvailable)
                        return new([], skipped, "预设包含曲线优化，但当前 CO 能力不可用；未发送任何命令");
                    if (value is < -30 or > 0)
                        return new([], skipped, "全核 Curve Optimizer 必须在 -30–0 步之间");
                    var coPlan = new CpuTuningPlan(null, null, null, null, null, null, null, value);
                    steps.Add(Step("全核 Curve Optimizer", coPlan, coPlan));
                }
            }
            else if (curveMode == CpuCurveOptimizerMode.PerCore && advanced.PerCoreCurveOptimizer is { Count: > 0 } cores)
            {
                if (!curveOptimizerAvailable)
                    return new([], skipped, "预设包含曲线优化，但当前 CO 能力不可用；未发送任何命令");
                if (cores.Any(pair => pair.Key is < 0 or > 7 || pair.Value is < -30 or > 0))
                    return new([], skipped, "逐核 Curve Optimizer 仅接受核心 0–7、偏移 -30–0；未发送任何命令");
                var coPlan = new CpuTuningPlan(null, null, null, null, null, null, null, null)
                { Advanced = new AdvancedCpuTuningPlan(PerCoreCurveOptimizer: new Dictionary<int, int>(cores)) };
                steps.Add(Step("逐核 Curve Optimizer", coPlan, coPlan));
            }
            else if (curveMode == CpuCurveOptimizerMode.PerCore)
                return new([], skipped, "逐核曲线优化缺少有效核心草稿；未发送任何命令");
        }
        else if (draft.NegativeCurveOptimizer is int legacyCurve)
        {
            if (!curveOptimizerAvailable)
                return new([], skipped, "预设包含曲线优化，但当前 CO 能力不可用；未发送任何命令");
            if (legacyCurve is < -30 or > 0)
                return new([], skipped, "全核 Curve Optimizer 必须在 -30–0 步之间");
            var coPlan = new CpuTuningPlan(null, null, null, null, null, null, null, legacyCurve);
            steps.Add(Step("全核 Curve Optimizer", coPlan, coPlan));
        }

        return new(steps, skipped);
    }

    public static bool MatchesReadBack(CpuTuningState? state, CpuTuningPlan plan) =>
        state is not null &&
        (plan.TemperatureLimitC is null || state.TemperatureLimitC == plan.TemperatureLimitC) &&
        (plan.SplWatts is null || state.SplWatts == plan.SplWatts) &&
        (plan.SpptWatts is null || state.SpptWatts == plan.SpptWatts) &&
        (plan.AcMaxFrequencyMhz is null || state.AcFrequency() == plan.AcMaxFrequencyMhz) &&
        (plan.DcMaxFrequencyMhz is null || state.DcFrequency() == plan.DcMaxFrequencyMhz) &&
        (plan.BoostEnabled is null || state.BoostEnabled == plan.BoostEnabled) &&
        (plan.WindowsPowerSchemeId is null || state.WindowsPowerSchemeId == plan.WindowsPowerSchemeId) &&
        (plan.AcMinActiveCoresPercent is null || state.AcMinActiveCoresPercent == plan.AcMinActiveCoresPercent) &&
        (plan.DcMinActiveCoresPercent is null || state.DcMinActiveCoresPercent == plan.DcMinActiveCoresPercent) &&
        (plan.Advanced?.StapmWatts is null || state.AdvancedLimits?.StapmWatts == plan.Advanced.StapmWatts) &&
        (plan.Advanced?.FastPptWatts is null || state.AdvancedLimits?.FastPptWatts == plan.Advanced.FastPptWatts) &&
        (plan.Advanced?.SlowPptWatts is null || state.AdvancedLimits?.SlowPptWatts == plan.Advanced.SlowPptWatts) &&
        (plan.Advanced?.PptWatts is null || state.AdvancedLimits?.PptWatts == plan.Advanced.PptWatts) &&
        (plan.Advanced?.VrmCurrentMilliamps is null || state.AdvancedLimits?.VrmCurrentMilliamps == plan.Advanced.VrmCurrentMilliamps) &&
        (plan.Advanced?.TdcCurrentMilliamps is null || state.AdvancedLimits?.TdcCurrentMilliamps == plan.Advanced.TdcCurrentMilliamps) &&
        (plan.Advanced?.EdcCurrentMilliamps is null || state.AdvancedLimits?.EdcCurrentMilliamps == plan.Advanced.EdcCurrentMilliamps) &&
        (plan.Advanced?.Mp1TemperatureC is null || state.AdvancedLimits?.Mp1TemperatureC == plan.Advanced.Mp1TemperatureC) &&
        (plan.Advanced?.RsmuTemperatureC is null || state.AdvancedLimits?.RsmuTemperatureC == plan.Advanced.RsmuTemperatureC) &&
        (plan.Advanced?.PboScalar is null || state.PboScalar == plan.Advanced.PboScalar) &&
        ((plan.NegativeCurveOptimizer ?? plan.Advanced?.CurveOptimizerAll) is not int curve ||
            state.PerCoreCurveOptimizer is { Count: 8 } allCores && allCores.Values.All(value => value == curve)) &&
        (plan.Advanced?.PerCoreCurveOptimizer is not { Count: > 0 } targets ||
            state.PerCoreCurveOptimizer is { Count: 8 } cores &&
            targets.All(pair => cores.TryGetValue(pair.Key, out int actual) && actual == pair.Value));

    private static bool HasSmuFields(AdvancedCpuTuningDraft draft) =>
        draft.StapmWatts is not null || draft.FastPptWatts is not null || draft.SlowPptWatts is not null ||
        draft.PptWatts is not null || draft.VrmCurrentMilliamps is not null || draft.TdcCurrentMilliamps is not null ||
        draft.EdcCurrentMilliamps is not null || draft.Mp1TemperatureC is not null || draft.RsmuTemperatureC is not null;

    private static void AddSmuStep<T>(ICollection<PerformancePresetCommandStep> steps, string label, T? value, Func<AdvancedCpuTuningPlan, AdvancedCpuTuningPlan> setField)
        where T : struct
    {
        if (value is not null)
        {
            var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null)
            { Advanced = setField(new AdvancedCpuTuningPlan()) };
            steps.Add(Step(label, plan, plan));
        }
    }

    private static PerformancePresetCommandStep Step(string label, CpuTuningPlan plan, CpuTuningPlan? readback = null) =>
        new(label, new SetCpuTuningCommand(Guid.NewGuid(), plan, RiskConfirmed: true), readback);

    private static bool HasPlanValues(CpuTuningPlan plan) =>
        plan.AcMaxFrequencyMhz is not null || plan.DcMaxFrequencyMhz is not null || plan.BoostEnabled is not null ||
        plan.WindowsPowerSchemeId is not null || plan.AcMinActiveCoresPercent is not null || plan.DcMinActiveCoresPercent is not null;

    public static bool HasReadBackTargets(PerformanceDraft draft, CpuTuningState? state)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return state is not null && (
            state.TemperatureLimitC is not null ||
            state.SplWatts is not null ||
            state.SpptWatts is not null ||
            state.BoostEnabled is not null ||
            state.AcFrequency() is not null && state.DcFrequency() is not null ||
            state.WindowsPowerSchemeId is not null && draft.WindowsPowerSchemeId != Guid.Empty);
    }

    public static bool HasCompleteReadBackCoverage(PerformanceDraft draft, CpuTuningState? state) =>
        state is not null &&
        state.TemperatureLimitC is not null &&
        state.SplWatts is not null &&
        state.SpptWatts is not null &&
        state.BoostEnabled is not null &&
        state.AcFrequency() is not null &&
        state.DcFrequency() is not null &&
        (draft.WindowsPowerSchemeId == Guid.Empty || state.WindowsPowerSchemeId is not null) &&
        draft.NegativeCurveOptimizer is null &&
        draft.AdvancedCpuTuning is null;

    public static SetCpuTuningCommand CreateForReadBackOnly(
        PerformanceDraft draft,
        CpuTuningState state,
        bool riskConfirmed,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(state);

        SetCpuTuningCommand command = Create(draft, riskConfirmed, operationId);
        bool hasFrequencyReadBack = state.AcFrequency() is not null && state.DcFrequency() is not null;
        CpuTuningPlan plan = command.Plan with
        {
            TemperatureLimitC = state.TemperatureLimitC is null ? null : draft.TemperatureLimitC,
            SplWatts = state.SplWatts is null ? null : draft.SplWatts,
            SpptWatts = state.SpptWatts is null ? null : draft.SpptWatts,
            MaxFrequencyMhz = hasFrequencyReadBack ? draft.MaxFrequencyMhz : null,
            BoostEnabled = state.BoostEnabled is null ? null : draft.IsBoostEnabled,
            WindowsPowerSchemeId = state.WindowsPowerSchemeId is null || draft.WindowsPowerSchemeId == Guid.Empty
                ? null
                : draft.WindowsPowerSchemeId,
            NegativeCurveOptimizer = null,
            AcMaxFrequencyMhz = hasFrequencyReadBack ? draft.AcMaxFrequencyMhz : null,
            DcMaxFrequencyMhz = hasFrequencyReadBack ? draft.DcMaxFrequencyMhz : null,
            Advanced = null
        };
        return command with { Plan = plan };
    }

    public static SetCpuTuningCommand CreateRestoreForReadBackOnly(
        CpuTuningPlan appliedPlan,
        CpuTuningState previousState,
        bool riskConfirmed,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(appliedPlan);
        ArgumentNullException.ThrowIfNull(previousState);

        int? ac = appliedPlan.AcMaxFrequencyMhz is null && appliedPlan.DcMaxFrequencyMhz is null
            ? null
            : previousState.AcFrequency();
        int? dc = appliedPlan.AcMaxFrequencyMhz is null && appliedPlan.DcMaxFrequencyMhz is null
            ? null
            : previousState.DcFrequency();
        var restorePlan = appliedPlan with
        {
            TemperatureLimitC = appliedPlan.TemperatureLimitC is null ? null : previousState.TemperatureLimitC,
            SplWatts = appliedPlan.SplWatts is null ? null : previousState.SplWatts,
            SpptWatts = appliedPlan.SpptWatts is null ? null : previousState.SpptWatts,
            MaxFrequencyMhz = appliedPlan.MaxFrequencyMhz is null
                ? null
                : previousState.MaxFrequencyMhz ?? ac ?? dc,
            BoostEnabled = appliedPlan.BoostEnabled is null ? null : previousState.BoostEnabled,
            WindowsPowerSchemeId = appliedPlan.WindowsPowerSchemeId is null
                ? null
                : previousState.WindowsPowerSchemeId,
            NegativeCurveOptimizer = null,
            AcMaxFrequencyMhz = ac,
            DcMaxFrequencyMhz = dc,
            Advanced = null
        };
        return new SetCpuTuningCommand(
            operationId ?? Guid.NewGuid(), restorePlan, riskConfirmed);
    }

    public static SetCpuTuningCommand Create(
        PerformanceDraft draft,
        bool riskConfirmed,
        Guid? operationId = null) =>
        new(
            operationId ?? Guid.NewGuid(),
            new CpuTuningPlan(
                draft.TemperatureLimitC,
                draft.SplWatts,
                draft.SpptWatts,
                draft.MaxFrequencyMhz,
                draft.IsBoostEnabled,
                null,
                draft.WindowsPowerSchemeId == Guid.Empty ? null : draft.WindowsPowerSchemeId,
                draft.AdvancedCpuTuning is null ? draft.NegativeCurveOptimizer : null)
            {
                AcMaxFrequencyMhz = draft.AcMaxFrequencyMhz,
                DcMaxFrequencyMhz = draft.DcMaxFrequencyMhz,
                Advanced = ToPlan(draft.AdvancedCpuTuning, draft.NegativeCurveOptimizer)
            },
            riskConfirmed);

    private static AdvancedCpuTuningPlan? ToPlan(AdvancedCpuTuningDraft? draft, int? legacyAllCore) => draft is null
        ? null
        : new AdvancedCpuTuningPlan(
            draft.StapmWatts,
            draft.FastPptWatts,
            draft.SlowPptWatts,
            draft.PptWatts,
            draft.VrmCurrentMilliamps,
            draft.TdcCurrentMilliamps,
            draft.EdcCurrentMilliamps,
            draft.Mp1TemperatureC,
            draft.RsmuTemperatureC,
            draft.PboScalar,
            null,
            null,
            null,
            null,
            draft.ResolveCurveOptimizerMode(legacyAllCore) == CpuCurveOptimizerMode.AllCore
                ? draft.CurveOptimizerAll ?? legacyAllCore : null,
            draft.ResolveCurveOptimizerMode(legacyAllCore) == CpuCurveOptimizerMode.PerCore
                ? draft.PerCoreCurveOptimizer is { } cores ? new Dictionary<int, int>(cores) : null : null);
}
