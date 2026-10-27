using Jiaolong.Contracts.Models;
using System.Text.Json;

namespace Jiaolong_ControlCenter.ViewModels;

public static class PerformanceLiveEdit
{
    public static CpuCurveOptimizerMode? ResolveCurveMode(PerformanceDraft draft, CpuCurveOptimizerMode? current)
    {
        bool Set(string key) => draft.SettingRequests is null ||
            draft.SettingRequests.TryGetValue(key, out var request) && request.Intent == SettingIntent.Set;
        var advanced = Set("advancedCpuTuning") ? draft.AdvancedCpuTuning : null;
        int? legacy = Set("negativeCurveOptimizer") ? draft.NegativeCurveOptimizer : null;
        if (draft.SettingRequests is not null && advanced?.CurveOptimizerMode is null &&
            advanced?.CurveOptimizerAll is null && advanced?.PerCoreCurveOptimizer is not { Count: > 0 } && legacy is null)
            return current;
        return advanced?.ResolveCurveOptimizerMode(legacy)
            ?? (legacy.HasValue ? CpuCurveOptimizerMode.AllCore : CpuCurveOptimizerMode.Bios);
    }

    public static PerformanceDraft? Prepare(PerformanceDraft draft, CpuTuningState state,
        IReadOnlySet<string> editedFields)
    {
        var fields = new HashSet<string>(editedFields, StringComparer.OrdinalIgnoreCase);
        if (fields.Count == 0) return null;
        if (fields.Contains("temperatureLimitC") && state.TemperatureLimitC is null ||
            fields.Contains("splWatts") && state.SplWatts is null ||
            fields.Contains("spptWatts") && state.SpptWatts is null ||
            fields.Contains("isBoostEnabled") && state.BoostEnabled is null ||
            fields.Contains("windowsPowerSchemeId") && state.WindowsPowerSchemeId is null)
            return null;
        if ((fields.Contains("acMaxFrequencyMhz") || fields.Contains("dcMaxFrequencyMhz")) &&
            (state.AcFrequency() is null || state.DcFrequency() is null)) return null;
        if (fields.Contains("acMinActiveCoresPercent") || fields.Contains("dcMinActiveCoresPercent"))
        {
            if (state.AcMinActiveCoresPercent is null || state.DcMinActiveCoresPercent is null ||
                draft.AcMinActiveCoresPercent is null || draft.DcMinActiveCoresPercent is null) return null;
            fields.UnionWith(["acMinActiveCoresPercent", "dcMinActiveCoresPercent"]);
        }
        if (draft.AdvancedCpuTuning is null) fields.Remove("advancedCpuTuning");
        if (draft.NegativeCurveOptimizer is null) fields.Remove("negativeCurveOptimizer");
        var advanced = fields.Contains("advancedCpuTuning") ? draft.AdvancedCpuTuning : null;
        var limits = state.AdvancedLimits;
        (double? Target, double? Before)[] values =
        [
            (advanced?.StapmWatts, limits?.StapmWatts), (advanced?.FastPptWatts, limits?.FastPptWatts),
            (advanced?.SlowPptWatts, limits?.SlowPptWatts), (advanced?.PptWatts, limits?.PptWatts),
            (advanced?.VrmCurrentMilliamps, limits?.VrmCurrentMilliamps), (advanced?.TdcCurrentMilliamps, limits?.TdcCurrentMilliamps),
            (advanced?.EdcCurrentMilliamps, limits?.EdcCurrentMilliamps), (advanced?.Mp1TemperatureC, limits?.Mp1TemperatureC),
            (advanced?.RsmuTemperatureC, limits?.RsmuTemperatureC), (advanced?.PboScalar, state.PboScalar)
        ];
        if (values.Any(value => value.Target is not null && value.Before is null)) return null;
        bool curveRequested = fields.Contains("negativeCurveOptimizer") || advanced is not null &&
            (advanced.CurveOptimizerAll is not null || advanced.PerCoreCurveOptimizer is { Count: > 0 });
        if (curveRequested && (state.CurveOptimizerVerification != "hardwareReadback" || !state.HasCompleteCurveValues())) return null;
        var payload = JsonSerializer.SerializeToElement(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string[] keys = ["temperatureLimitC", "splWatts", "spptWatts", "acMaxFrequencyMhz", "dcMaxFrequencyMhz",
            "isBoostEnabled", "windowsPowerSchemeId", "acMinActiveCoresPercent", "dcMinActiveCoresPercent",
            "negativeCurveOptimizer", "advancedCpuTuning"];
        var requests = keys.ToDictionary(key => key, key => fields.Contains(key)
            ? SettingRequest.Set(payload.GetProperty(key)) : SettingRequest.Keep, StringComparer.OrdinalIgnoreCase);
        if (!requests.Values.Any(request => request.Intent == SettingIntent.Set)) return null;
        return draft with
        {
            SettingRequests = requests,
            UseOfficialCpuPolicy = fields.Overlaps(["temperatureLimitC", "splWatts", "spptWatts"])
                ? false : draft.UseOfficialCpuPolicy
        };
    }

    public static PerformanceDraft Normalize(PerformanceDraft draft, CpuTuningState state)
    {
        bool Set(string key) => draft.SettingRequests!.TryGetValue(key, out var request) && request.Intent == SettingIntent.Set;
        if (draft.SettingRequests is null) return draft with
        {
            TemperatureLimitC = state.TemperatureLimitC == draft.TemperatureLimitC && draft.TemperatureLimitC is < 45 or > 100 ? 70 : draft.TemperatureLimitC,
            SplWatts = state.SplWatts == draft.SplWatts && draft.SplWatts is < 45 or > 75 ? 55 : draft.SplWatts,
            SpptWatts = state.SpptWatts == draft.SpptWatts && draft.SpptWatts is < 45 or > 75 ? 55 : draft.SpptWatts,
            AdvancedCpuTuning = draft.AdvancedCpuTuning?.WithoutUnchangedHardwareFields(state.AdvancedLimits)
        };
        // Keep values exist only in this validation copy; the intent map excludes them from every command.
        var normalized = draft with
        {
            TemperatureLimitC = Set("temperatureLimitC") ? draft.TemperatureLimitC : 70,
            SplWatts = Set("splWatts") ? draft.SplWatts : 55,
            SpptWatts = Set("spptWatts") ? draft.SpptWatts : 55,
            AcMaxFrequencyMhz = Set("acMaxFrequencyMhz") ? draft.AcMaxFrequencyMhz : 0,
            DcMaxFrequencyMhz = Set("dcMaxFrequencyMhz") ? draft.DcMaxFrequencyMhz : 0,
            IsBoostEnabled = Set("isBoostEnabled") && draft.IsBoostEnabled,
            BoostPolicy = Set("isBoostEnabled") ? draft.BoostPolicy : null,
            WindowsPowerSchemeId = Set("windowsPowerSchemeId") ? draft.WindowsPowerSchemeId : Guid.Empty,
            AcMinActiveCoresPercent = Set("acMinActiveCoresPercent") ? draft.AcMinActiveCoresPercent : null,
            DcMinActiveCoresPercent = Set("dcMinActiveCoresPercent") ? draft.DcMinActiveCoresPercent : null,
            NegativeCurveOptimizer = Set("negativeCurveOptimizer") ? draft.NegativeCurveOptimizer : null,
            AdvancedCpuTuning = Set("advancedCpuTuning") ? draft.AdvancedCpuTuning?.WithoutUnchangedHardwareFields(state.AdvancedLimits) : null
        };
        if (Set("advancedCpuTuning"))
        {
            // Validation consumes the intent payload too; keep it aligned with the pruned targets.
            normalized = normalized with { SettingRequests = new Dictionary<string, SettingRequest>(draft.SettingRequests,
                StringComparer.OrdinalIgnoreCase)
            {
                ["advancedCpuTuning"] = normalized.AdvancedCpuTuning is null ? SettingRequest.Keep :
                    SettingRequest.Set(JsonSerializer.SerializeToElement(normalized.AdvancedCpuTuning,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            } };
        }
        return normalized;
    }

    public static PerformancePresetCommandPlan CreatePlan(PerformanceDraft draft, CpuTuningState state,
        bool cpu, bool smu, bool pbo, bool curve, CapabilitySnapshot? capabilities = null, long? hardwareEpoch = null)
    {
        var plan = PerformanceCommandFactory.CreatePresetCommands(Normalize(draft, state), state, cpu, smu, pbo, curve,
            capabilities: capabilities, hardwareEpoch: hardwareEpoch);
        return plan with { Steps = plan.Steps.Where(step => step.Label switch
        {
            "温度墙" => state.TemperatureLimitC != draft.TemperatureLimitC,
            "持续功耗 SPL" => state.SplWatts != draft.SplWatts,
            "短时功耗 SPPT" => state.SpptWatts != draft.SpptWatts,
            _ => true
        }).ToArray() };
    }
}
