using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public static class PerformancePresetProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string> FieldNames = typeof(PerformanceDraft).GetProperties()
        .ToDictionary(property => property.Name, property => JsonNamingPolicy.CamelCase.ConvertName(property.Name),
            StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(PerformanceDraft draft, string field) => draft.KnownFields is null ||
        draft.KnownFields.Contains(field, StringComparer.OrdinalIgnoreCase);

    public static double? NumericValue(PerformanceDraft draft, string field, double? value) =>
        IsKnown(draft, field) ? value : null;

    public static AdvancedCpuTuningDraft? AdvancedEditTarget(
        AdvancedCpuTuningDraft? numericEdits, CpuCurveOptimizerMode? editedMode) =>
        editedMode == CpuCurveOptimizerMode.Bios
            ? (numericEdits ?? new()) with { CurveOptimizerMode = CpuCurveOptimizerMode.Bios }
            : numericEdits;

    public static PerformanceDraft ApplyEdits(PerformanceDraft original, PerformanceDraft edited,
        IEnumerable<string> editedFields, AdvancedCpuTuningDraft? advancedTarget = null)
    {
        var result = original with
        {
            SettingRequests = original.SettingRequests is null ? null : new(original.SettingRequests, StringComparer.OrdinalIgnoreCase),
            KnownFields = original.KnownFields?.ToArray()
        };
        var fields = NormalizeFields(editedFields).Where(field => field != "advancedCpuTuning" || advancedTarget is not null).ToArray();
        foreach (var field in fields)
        {
            result = field switch
            {
                "mode" => result with { Mode = edited.Mode },
                "useOfficialCpuPolicy" => result with { UseOfficialCpuPolicy = edited.UseOfficialCpuPolicy },
                "temperatureLimitC" => result with { TemperatureLimitC = edited.TemperatureLimitC },
                "splWatts" => result with { SplWatts = edited.SplWatts },
                "spptWatts" => result with { SpptWatts = edited.SpptWatts },
                "acMaxFrequencyMhz" => result with { AcMaxFrequencyMhz = edited.AcMaxFrequencyMhz },
                "dcMaxFrequencyMhz" => result with { DcMaxFrequencyMhz = edited.DcMaxFrequencyMhz },
                "isBoostEnabled" => result with { IsBoostEnabled = edited.IsBoostEnabled, BoostPolicy = edited.BoostPolicy },
                "windowsPowerSchemeId" => result with { WindowsPowerSchemeId = edited.WindowsPowerSchemeId },
                "acMinActiveCoresPercent" => result with { AcMinActiveCoresPercent = edited.AcMinActiveCoresPercent },
                "dcMinActiveCoresPercent" => result with { DcMinActiveCoresPercent = edited.DcMinActiveCoresPercent },
                "negativeCurveOptimizer" => result with { NegativeCurveOptimizer = edited.NegativeCurveOptimizer },
                "advancedCpuTuning" => result with { AdvancedCpuTuning = advancedTarget is null
                    ? edited.AdvancedCpuTuning : MergeAdvanced(original.AdvancedCpuTuning, advancedTarget) },
                _ => throw new ArgumentException($"Unknown performance field: {field}", nameof(editedFields))
            };
            if (result.SettingRequests is not null && field is not ("mode" or "useOfficialCpuPolicy"))
                result.SettingRequests[field] = field == "advancedCpuTuning" && advancedTarget is not null
                    ? SettingRequest.Set(MergeAdvanced(ReadAdvancedTarget(original), advancedTarget))
                    : field == "isBoostEnabled" &&
                    result.BoostPolicy is { IsValid: true } policy && policy.MatchesToggle(result.IsBoostEnabled)
                        ? SettingRequest.Set(policy)
                        : new(SettingIntent.Set, JsonSerializer.SerializeToElement(result, JsonOptions).GetProperty(field).Clone());
        }
        if (original.KnownFields is not null)
            result.KnownFields = NormalizeFields(original.KnownFields.Concat(fields.Where(field => field is not ("mode" or "useOfficialCpuPolicy"))));
        if (fields.Contains("advancedCpuTuning") && advancedTarget is
            { CurveOptimizerAll: not null } or { PerCoreCurveOptimizer.Count: > 0 })
        {
            result.NegativeCurveOptimizer = null;
            if (result.SettingRequests is not null) result.SettingRequests["negativeCurveOptimizer"] = SettingRequest.Keep;
            if (result.KnownFields is not null) result.KnownFields = result.KnownFields.Where(field => field != "negativeCurveOptimizer").ToArray();
        }
        if (result.AdvancedCpuTuning is { } advanced)
            result.AdvancedCpuTuning = advanced with
            {
                PerCoreCurveOptimizer = advanced.PerCoreCurveOptimizer is null ? null : new Dictionary<int, int>(advanced.PerCoreCurveOptimizer),
                PerCoreOcClockMhz = advanced.PerCoreOcClockMhz is null ? null : new Dictionary<int, int>(advanced.PerCoreOcClockMhz)
            };
        return result;
    }

    private static AdvancedCpuTuningDraft? ReadAdvancedTarget(PerformanceDraft draft) =>
        draft.SettingRequests?.GetValueOrDefault("advancedCpuTuning") is { Intent: SettingIntent.Set, Value: { } value }
            ? value.Deserialize<AdvancedCpuTuningDraft>(JsonOptions) : null;

    private static AdvancedCpuTuningDraft MergeAdvanced(AdvancedCpuTuningDraft? previous, AdvancedCpuTuningDraft edited)
    {
        var values = JsonSerializer.SerializeToElement(previous ?? new(), JsonOptions).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);
        if (edited.CurveOptimizerMode is CpuCurveOptimizerMode.AllCore) values["perCoreCurveOptimizer"] = JsonSerializer.SerializeToElement<object?>(null);
        if (edited.CurveOptimizerMode is CpuCurveOptimizerMode.PerCore) values["curveOptimizerAll"] = JsonSerializer.SerializeToElement<object?>(null);
        foreach (var property in JsonSerializer.SerializeToElement(edited, JsonOptions).EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Name is "perCoreCurveOptimizer" or "perCoreOcClockMhz" &&
                values[property.Name].ValueKind == JsonValueKind.Object)
            {
                var cores = values[property.Name].EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
                foreach (var core in property.Value.EnumerateObject()) cores[core.Name] = core.Value;
                values[property.Name] = JsonSerializer.SerializeToElement(cores);
            }
            else values[property.Name] = property.Value;
        }
        return JsonSerializer.SerializeToElement(values).Deserialize<AdvancedCpuTuningDraft>(JsonOptions)!;
    }

    public static bool Equivalent(PerformanceDraft left, PerformanceDraft right) =>
        (left with { KnownFields = null, SettingRequests = null, AdvancedCpuTuning = null }) ==
        (right with { KnownFields = null, SettingRequests = null, AdvancedCpuTuning = null }) &&
        ((left.KnownFields is null && right.KnownFields is null) ||
            (left.KnownFields is not null && right.KnownFields is not null &&
             NormalizeFields(left.KnownFields).SequenceEqual(NormalizeFields(right.KnownFields)))) &&
        SameDictionary(left.SettingRequests, right.SettingRequests, (a, b) => a.Intent == b.Intent &&
            ((a.Value is null && b.Value is null) || (a.Value is { } av && b.Value is { } bv && JsonElement.DeepEquals(av, bv)))) &&
        SameAdvanced(left.AdvancedCpuTuning, right.AdvancedCpuTuning);

    private static bool SameAdvanced(AdvancedCpuTuningDraft? left, AdvancedCpuTuningDraft? right) =>
        left is null || right is null ? left == right :
        (left with { PerCoreCurveOptimizer = null, PerCoreOcClockMhz = null }) ==
        (right with { PerCoreCurveOptimizer = null, PerCoreOcClockMhz = null }) &&
        SameDictionary(left.PerCoreCurveOptimizer, right.PerCoreCurveOptimizer, (a, b) => a == b) &&
        SameDictionary(left.PerCoreOcClockMhz, right.PerCoreOcClockMhz, (a, b) => a == b);

    private static bool SameDictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? left,
        IReadOnlyDictionary<TKey, TValue>? right, Func<TValue, TValue, bool> same) where TKey : notnull =>
        left is null || right is null ? left == right : left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && same(pair.Value, value));

    private static string[] NormalizeFields(IEnumerable<string> fields) => fields
        .Select(field => FieldNames.TryGetValue(field, out var name) ? name : JsonNamingPolicy.CamelCase.ConvertName(field))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal).ToArray();
}
