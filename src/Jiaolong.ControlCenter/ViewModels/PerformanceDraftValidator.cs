namespace Jiaolong_ControlCenter.ViewModels;

public sealed record ValidationSummary(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationSummary Valid { get; } = new(true, []);
}

public static class PerformanceDraftValidator
{
    public static ValidationSummary Validate(PerformanceDraft draft)
    {
        var errors = new List<string>();
        if (!PerformanceCapabilities.TemperatureWallC.Contains(draft.TemperatureLimitC)) errors.Add("温度墙必须在 45–100°C 之间");
        if (!PerformanceCapabilities.SplWatts.Contains(draft.SplWatts)) errors.Add("SPL 必须在 20–105W 之间");
        if (!PerformanceCapabilities.SpptWatts.Contains(draft.SpptWatts)) errors.Add("SPPT 必须在 20–120W 之间");
        if (!PerformanceCapabilities.MaxFrequencyMhz.Contains(draft.AcMaxFrequencyMhz) ||
            !PerformanceCapabilities.MaxFrequencyMhz.Contains(draft.DcMaxFrequencyMhz))
            errors.Add("交流电和电池最高频率必须在 1500–5400MHz 之间");
        if (draft.NegativeCurveOptimizer is < -30 or > 0) errors.Add("曲线优化器必须在 -30–0 之间");
        if (draft.AdvancedCpuTuning is { } advanced)
        {
            if (advanced.CurveOptimizerMode is { } mode && !Enum.IsDefined(mode))
                errors.Add("请选择有效的曲线优化方式");
            if (advanced.ResolveCurveOptimizerMode(draft.NegativeCurveOptimizer) == CpuCurveOptimizerMode.PerCore &&
                (advanced.PerCoreCurveOptimizer is not { Count: > 0 } ||
                 advanced.PerCoreCurveOptimizer.Any(pair => pair.Key is < 0 or >= 16 || pair.Value is < -30 or > 30)))
                errors.Add("逐核曲线优化需要有效的核心与偏移步数");
            if (advanced.StapmWatts is < 1 or > 250 || advanced.FastPptWatts is < 1 or > 250 || advanced.SlowPptWatts is < 1 or > 250 || advanced.PptWatts is < 1 or > 250)
                errors.Add("SMU 功耗限制必须在 1–250W 之间");
            if (advanced.VrmCurrentMilliamps is < 0 or > 200_000 || advanced.TdcCurrentMilliamps is < 0 or > 200_000 || advanced.EdcCurrentMilliamps is < 0 or > 200_000)
                errors.Add("SMU 电流限制必须在 0–200000mA 之间");
            if (advanced.Mp1TemperatureC is < 40 or > 115 || advanced.RsmuTemperatureC is < 40 or > 115)
                errors.Add("SMU 温度限制必须在 40–115°C 之间");
            if (advanced.PboScalar is < 1 or > 10) errors.Add("PBO Scalar 必须在 1–10 之间");
            if (advanced.CurveOptimizerAll is < -30 or > 30)
                errors.Add("全核 Curve Optimizer 必须在 -30–30 之间");
        }
        return errors.Count == 0 ? ValidationSummary.Valid : new ValidationSummary(false, errors);
    }
}
