using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static AdvancedCpuTuningDraft? AdvancedTarget(PerformanceDraft draft)
    {
        if (!ShouldSet(draft, "advancedCpuTuning")) return null;
        if (draft.SettingRequests is null) return draft.AdvancedCpuTuning;
        return draft.SettingRequests?.GetValueOrDefault("advancedCpuTuning")?.Value is { ValueKind: JsonValueKind.Object } value
            ? value.Deserialize<AdvancedCpuTuningDraft>(JsonOptions) : draft.SettingRequests is null ? draft.AdvancedCpuTuning : null;
    }

    private static bool ShouldSet(PerformanceDraft draft, string field) =>
        (draft.KnownFields is null || draft.KnownFields.Contains(field, StringComparer.OrdinalIgnoreCase)) &&
        (draft.SettingRequests is null ||
         draft.SettingRequests.TryGetValue(field, out var request) && request.Intent == SettingIntent.Set);
    public static bool HasPresetTargets(PerformanceDraft draft) =>
        draft.SettingRequests is { } requests ? requests.Keys.Any(field => ShouldSet(draft, field)) :
        draft.TemperatureLimitC > 0 || draft.SplWatts > 0 || draft.SpptWatts > 0 ||
        draft.AdvancedCpuTuning is not null || draft.NegativeCurveOptimizer is not null;

    public static PerformancePresetCommandPlan CreatePresetCommands(
        PerformanceDraft draft,
        CpuTuningState? state,
        bool cpuTuningAvailable,
        bool smuAvailable,
        bool pboAvailable,
        bool curveOptimizerAvailable,
        bool forceApply = false,
        CapabilitySnapshot? capabilities = null,
        long? hardwareEpoch = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var steps = new List<PerformancePresetCommandStep>();
        var skipped = new List<string>();
        if (state is null || !cpuTuningAvailable)
            return new(steps, skipped, "CPU 调校能力不可用");

        var validation = PerformanceDraftValidator.Validate(draft);
        if (!validation.IsValid)
            return new(steps, skipped, validation.Errors[0]);
        if (!draft.UseOfficialCpuPolicy &&
            (ShouldSet(draft, "temperatureLimitC") && draft.TemperatureLimitC is < 45 or > 100 ||
             ShouldSet(draft, "splWatts") && draft.SplWatts is < 45 or > 75 ||
             ShouldSet(draft, "spptWatts") && draft.SpptWatts is < 45 or > 75))
            return new(steps, skipped, "OEM 温度墙须为 45–100°C，SPL/SPPT 须为 45–75W；未发送任何命令");

        AdvancedCpuTuningDraft? advanced = AdvancedTarget(draft)?.WithoutUnchangedHardwareFields(state.AdvancedLimits);
        if (advanced is not null && ShouldSet(draft, "spptWatts") &&
            advanced.FastPptWatts is double fastTarget && fastTarget != draft.SpptWatts)
            return new(steps, skipped, "SPPT 与 Fast PPT 目标冲突，请分别提交");
        if (advanced is not null && ShouldSet(draft, "temperatureLimitC") &&
            ((advanced.Mp1TemperatureC is int mp1Target && mp1Target != draft.TemperatureLimitC) ||
             (advanced.RsmuTemperatureC is int rsmuTarget && rsmuTarget != draft.TemperatureLimitC)))
            return new(steps, skipped, "温度墙与高级温限目标冲突，请分别提交");
        if (advanced?.VrmCurrentMilliamps is int vrmTarget && advanced.TdcCurrentMilliamps is int tdcTarget && vrmTarget != tdcTarget)
            return new(steps, skipped, "VRM 与 TDC 在本机为同一电流限值，请设置一致");
        if (advanced is not null &&
            (advanced.StapmWatts is < 45 or > 75 || advanced.FastPptWatts is < 45 or > 75 ||
             advanced.SlowPptWatts is < 45 or > 75 || advanced.PptWatts is < 45 or > 120 ||
             advanced.VrmCurrentMilliamps is < 1_000 or > 200_000 ||
             advanced.TdcCurrentMilliamps is < 1_000 or > 200_000 ||
             advanced.EdcCurrentMilliamps is < 1_000 or > 200_000 ||
             advanced.Mp1TemperatureC is < 40 or > 100 || advanced.RsmuTemperatureC is < 40 or > 100))
            return new(steps, skipped, "高级 CPU 参数超出当前控件保护范围；未发送任何命令");

        foreach (var (key, target, before) in new (string Key, double? Target, double? Before)[]
        {
            ("pptWatts", advanced?.PptWatts, state.AdvancedLimits?.PptWatts),
            ("mp1TemperatureC", advanced?.Mp1TemperatureC, state.AdvancedLimits?.Mp1TemperatureC),
            ("tdcCurrentMilliamps", advanced?.TdcCurrentMilliamps, state.AdvancedLimits?.TdcCurrentMilliamps),
            ("edcCurrentMilliamps", advanced?.EdcCurrentMilliamps, state.AdvancedLimits?.EdcCurrentMilliamps)
        })
        {
            if (target is null) continue;
            var field = capabilities?.Fields.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (!CanWriteField(capabilities, key, hardwareEpoch) || before is null ||
                field is not { Minimum: not null, Maximum: not null, Step: > 0 } || !PresetValidation.WithinRange(target.Value, field))
                return new([], skipped, $"{key} 缺少有效原值或逐字段写入验证，或超出设备范围；未发送任何命令");
        }

        var windowsPlan = new CpuTuningPlan(null, null, null, null, null, null, null, null);
        bool schemeChanges = draft.WindowsPowerSchemeId != Guid.Empty && state.WindowsPowerSchemeId is { } scheme
            && scheme != draft.WindowsPowerSchemeId;
        if (ShouldSet(draft, "acMaxFrequencyMhz") && state.AcFrequency() is int oldAc)
        {
            if (forceApply || schemeChanges || oldAc != draft.AcMaxFrequencyMhz)
                windowsPlan = windowsPlan with { AcMaxFrequencyMhz = draft.AcMaxFrequencyMhz };
        }
        else if (ShouldSet(draft, "acMaxFrequencyMhz")) skipped.Add("交流最高频率：当前不可读回");

        if (ShouldSet(draft, "dcMaxFrequencyMhz") && state.DcFrequency() is int oldDc)
        {
            if (forceApply || schemeChanges || oldDc != draft.DcMaxFrequencyMhz)
                windowsPlan = windowsPlan with { DcMaxFrequencyMhz = draft.DcMaxFrequencyMhz };
        }
        else if (ShouldSet(draft, "dcMaxFrequencyMhz")) skipped.Add("电池最高频率：当前不可读回");

        // The transport fills a missing side from the other; always preserve the saved pair.
        if (windowsPlan.AcMaxFrequencyMhz is not null || windowsPlan.DcMaxFrequencyMhz is not null)
            windowsPlan = state.AcFrequency() is not null && state.DcFrequency() is not null
                ? windowsPlan with { AcMaxFrequencyMhz = ShouldSet(draft, "acMaxFrequencyMhz") ? draft.AcMaxFrequencyMhz : state.AcFrequency(),
                    DcMaxFrequencyMhz = ShouldSet(draft, "dcMaxFrequencyMhz") ? draft.DcMaxFrequencyMhz : state.DcFrequency() }
                : windowsPlan with { AcMaxFrequencyMhz = null, DcMaxFrequencyMhz = null };

        if (ShouldSet(draft, "isBoostEnabled") && draft.BoostPolicy is { } policy)
        {
            if (CpuBoostPolicy.FromState(state) is not { } oldPolicy) skipped.Add("Boost：当前 AC/DC 模式不可读回");
            else if (forceApply || schemeChanges || oldPolicy != policy) windowsPlan = windowsPlan with { BoostPolicy = policy };
        }
        else if (ShouldSet(draft, "isBoostEnabled") && state.BoostEnabled is bool oldBoost)
        {
            bool matches = CpuBoostPolicy.FromState(state) is { } actual
                ? actual.MatchesToggle(draft.IsBoostEnabled) : oldBoost == draft.IsBoostEnabled;
            if (forceApply || schemeChanges || !matches)
                windowsPlan = windowsPlan with { BoostEnabled = draft.IsBoostEnabled };
        }
        else if (ShouldSet(draft, "isBoostEnabled")) skipped.Add("Boost：当前不可读回");

        if (ShouldSet(draft, "windowsPowerSchemeId") && draft.WindowsPowerSchemeId != Guid.Empty)
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
        if (ShouldSet(draft, "acMinActiveCoresPercent") && ShouldSet(draft, "dcMinActiveCoresPercent") &&
            draft.AcMinActiveCoresPercent is int targetAcParking && draft.DcMinActiveCoresPercent is int targetDcParking)
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

        // OEM limits and explicitly requested SMU/CO/PBO adjustments have separate ownership.
        if (!draft.UseOfficialCpuPolicy)
        {
        var temperaturePlan = new CpuTuningPlan(draft.TemperatureLimitC, null, null, null, null, null, null, null);
        var splPlan = new CpuTuningPlan(null, draft.SplWatts, null, null, null, null, null, null);
        var spptPlan = new CpuTuningPlan(null, null, draft.SpptWatts, null, null, null, null, null);
        if (ShouldSet(draft, "temperatureLimitC") && advanced?.Mp1TemperatureC is null) steps.Add(Step("温度墙", temperaturePlan, temperaturePlan));
        if (ShouldSet(draft, "splWatts")) steps.Add(Step("持续功耗 SPL", splPlan, splPlan));
        if (ShouldSet(draft, "spptWatts")) steps.Add(Step("短时功耗 SPPT", spptPlan, spptPlan));
        }

        if (advanced is not null)
        {
            if (HasResidualSmuFields(advanced) &&
                (!smuAvailable || !CanWriteField(capabilities, "advancedCpuTuning", hardwareEpoch)))
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

            int? requestedLegacyCurve = ShouldSet(draft, "negativeCurveOptimizer") ? draft.NegativeCurveOptimizer : null;
            CpuCurveOptimizerMode curveMode = advanced.ResolveCurveOptimizerMode(requestedLegacyCurve);
            if (curveMode == CpuCurveOptimizerMode.AllCore)
            {
                int? curve = advanced.CurveOptimizerAll ?? requestedLegacyCurve;
                if (curve is int value)
                {
                    if (!curveOptimizerAvailable)
                        return new([], skipped, "预设包含曲线优化，但当前 CO 能力不可用；未发送任何命令");
                    if (value is < -50 or > 0)
                        return new([], skipped, "全核 Curve Optimizer 必须在 -50–0 步之间");
                    var coPlan = new CpuTuningPlan(null, null, null, null, null, null, null, value);
                    steps.Add(Step("全核 Curve Optimizer", coPlan, coPlan));
                }
            }
            else if (curveMode == CpuCurveOptimizerMode.PerCore && advanced.PerCoreCurveOptimizer is { Count: > 0 } cores)
            {
                if (!curveOptimizerAvailable)
                    return new([], skipped, "预设包含曲线优化，但当前 CO 能力不可用；未发送任何命令");
                if (cores.Any(pair => pair.Key < 0 || pair.Key >= (state?.EnabledCoreCount ?? 8) || pair.Value is < -50 or > 0))
                    return new([], skipped, "逐核 Curve Optimizer 仅接受当前 CPU 的有效核心、偏移 -50–0；未发送任何命令");
                var coPlan = new CpuTuningPlan(null, null, null, null, null, null, null, null)
                { Advanced = new AdvancedCpuTuningPlan(PerCoreCurveOptimizer: new Dictionary<int, int>(cores)) };
                steps.Add(Step("逐核 Curve Optimizer", coPlan, coPlan));
            }
            else if (curveMode == CpuCurveOptimizerMode.PerCore)
                return new([], skipped, "逐核曲线优化缺少有效核心草稿；未发送任何命令");
        }
        else if (ShouldSet(draft, "negativeCurveOptimizer") && draft.NegativeCurveOptimizer is int legacyCurve)
        {
            if (!curveOptimizerAvailable)
                return new([], skipped, "预设包含曲线优化，但当前 CO 能力不可用；未发送任何命令");
            if (legacyCurve is < -50 or > 0)
                return new([], skipped, "全核 Curve Optimizer 必须在 -50–0 步之间");
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
        (plan.BoostEnabled is null || (CpuBoostPolicy.FromState(state) is { } actualBoost
            ? actualBoost.MatchesToggle(plan.BoostEnabled.Value) : state.BoostEnabled == plan.BoostEnabled)) &&
        (plan.BoostPolicy is null || CpuBoostPolicy.FromState(state) == plan.BoostPolicy) &&
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
            state.HasCompleteCurveValues() && state.PerCoreCurveOptimizer!.Values.All(value => value == curve)) &&
        (plan.Advanced?.PerCoreCurveOptimizer is not { Count: > 0 } targets ||
            state.HasCompleteCurveValues() &&
            targets.All(pair => state.PerCoreCurveOptimizer!.TryGetValue(pair.Key, out int actual) && actual == pair.Value));

    internal static bool CanWriteField(CapabilitySnapshot? capabilities, string key, long? hardwareEpoch = null)
    {
        var field = capabilities?.Fields.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return field is { Readable: true, HardwareEpoch: > 0 } &&
            PresetValidation.IsVerifiedWritable(field, hardwareEpoch ?? field.HardwareEpoch) &&
            capabilities!.Fields.All(item => item.HardwareEpoch == field.HardwareEpoch);
    }

    private static bool HasResidualSmuFields(AdvancedCpuTuningDraft draft) =>
        draft.StapmWatts is not null || draft.FastPptWatts is not null || draft.SlowPptWatts is not null ||
        draft.VrmCurrentMilliamps is not null ||
        draft.RsmuTemperatureC is not null;

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
        plan.AcMaxFrequencyMhz is not null || plan.DcMaxFrequencyMhz is not null || plan.BoostEnabled is not null || plan.BoostPolicy is not null ||
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
            TemperatureLimitC = state.TemperatureLimitC is null ? null : command.Plan.TemperatureLimitC,
            SplWatts = state.SplWatts is null ? null : command.Plan.SplWatts,
            SpptWatts = state.SpptWatts is null ? null : command.Plan.SpptWatts,
            MaxFrequencyMhz = hasFrequencyReadBack ? command.Plan.MaxFrequencyMhz : null,
            BoostEnabled = state.BoostEnabled is null ? null : command.Plan.BoostEnabled,
            BoostPolicy = CpuBoostPolicy.FromState(state) is null ? null : command.Plan.BoostPolicy,
            WindowsPowerSchemeId = state.WindowsPowerSchemeId is null || draft.WindowsPowerSchemeId == Guid.Empty
                ? null
                : command.Plan.WindowsPowerSchemeId,
            NegativeCurveOptimizer = null,
            AcMaxFrequencyMhz = hasFrequencyReadBack ? command.Plan.AcMaxFrequencyMhz : null,
            DcMaxFrequencyMhz = hasFrequencyReadBack ? command.Plan.DcMaxFrequencyMhz : null,
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
            BoostEnabled = appliedPlan.BoostEnabled is null || CpuBoostPolicy.FromState(previousState) is not null ? null : previousState.BoostEnabled,
            BoostPolicy = appliedPlan.BoostPolicy is not null || appliedPlan.BoostEnabled is not null ? CpuBoostPolicy.FromState(previousState) : null,
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
        Guid? operationId = null)
    {
        var validation = PerformanceDraftValidator.Validate(draft);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0]);
        return new(
            operationId ?? Guid.NewGuid(),
            new CpuTuningPlan(
                draft.UseOfficialCpuPolicy || !ShouldSet(draft, "temperatureLimitC") ? null : draft.TemperatureLimitC,
                draft.UseOfficialCpuPolicy || !ShouldSet(draft, "splWatts") ? null : draft.SplWatts,
                draft.UseOfficialCpuPolicy || !ShouldSet(draft, "spptWatts") ? null : draft.SpptWatts,
                ShouldSet(draft, "acMaxFrequencyMhz") && ShouldSet(draft, "dcMaxFrequencyMhz") ? draft.MaxFrequencyMhz : null,
                ShouldSet(draft, "isBoostEnabled") && draft.BoostPolicy is null ? draft.IsBoostEnabled : null,
                null,
                draft.WindowsPowerSchemeId == Guid.Empty || !ShouldSet(draft, "windowsPowerSchemeId") ? null : draft.WindowsPowerSchemeId,
                !draft.UseOfficialCpuPolicy && ShouldSet(draft, "negativeCurveOptimizer") && AdvancedTarget(draft) is null ? draft.NegativeCurveOptimizer : null)
            {
                AcMaxFrequencyMhz = ShouldSet(draft, "acMaxFrequencyMhz") ? draft.AcMaxFrequencyMhz : null,
                DcMaxFrequencyMhz = ShouldSet(draft, "dcMaxFrequencyMhz") ? draft.DcMaxFrequencyMhz : null,
                BoostPolicy = ShouldSet(draft, "isBoostEnabled") ? draft.BoostPolicy : null,
                Advanced = draft.UseOfficialCpuPolicy || !ShouldSet(draft, "advancedCpuTuning") ? null : ToPlan(AdvancedTarget(draft),
                    ShouldSet(draft, "negativeCurveOptimizer") ? draft.NegativeCurveOptimizer : null)
            },
            riskConfirmed);
    }

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
