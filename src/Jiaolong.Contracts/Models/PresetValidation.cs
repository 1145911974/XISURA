using System.Text.Json;

namespace Jiaolong.Contracts.Models;

public sealed record PresetValidationResult(bool IsValid, string[] Errors);

public static class PresetValidation
{
    public static PresetValidationResult Validate(PagePresetEnvelope preset, CapabilitySnapshot? capabilities = null,
        bool requireWritable = false)
    {
        var errors = new List<string>();
        HashSet<string>? knownFields = null;
        string[] fields = [];
        if (preset.SchemaVersion is < 1 or > PagePresetEnvelope.CurrentSchemaVersion) errors.Add("预设版本不受支持，请升级应用");
        if (!Enum.IsDefined(preset.Page) || !Enum.IsDefined(preset.Key.Mode) || preset.Key.Slot is < 1 or > 3)
            errors.Add("预设身份无效");
        if (preset.Revision < 0 || string.IsNullOrWhiteSpace(preset.DisplayName)) errors.Add("预设名称或修订号无效");
        if (preset.Payload.ValueKind != JsonValueKind.Object) errors.Add("预设缺少有效 payload");
        else
        {
            errors.AddRange(ValidatePayload(preset.Page, preset.Payload,
                sourceValues: preset.Page == ControlPageId.Performance && preset.Settings is { Count: > 0 }).Errors);
            fields = preset.Page switch
            {
                ControlPageId.Performance => ["temperatureLimitC", "splWatts", "spptWatts", "acMaxFrequencyMhz", "dcMaxFrequencyMhz", "isBoostEnabled", "windowsPowerSchemeId", "acMinActiveCoresPercent", "dcMinActiveCoresPercent", "negativeCurveOptimizer", "advancedCpuTuning"],
                ControlPageId.Gpu => ["coreFrequencyLimitMhz", "resetCoreFrequencyLimit", "memoryOffsetKhz", "coreOffsetKhz", "vfOffsetsKhz"],
                ControlPageId.Fan => ["profile", "isShared", "cpu", "gpu", "shared", "strategy", "fixedRpm"],
                ControlPageId.Lighting => ["effect", "brightness", "hex", "logo", "speed"],
                _ => []
            };
            var present = preset.Payload.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!fields.Any(present.Contains) || preset.SchemaVersion >= 2 && !fields.All(present.Contains))
                errors.Add("预设缺少完整页字段");
            var provenance = preset.Payload.EnumerateObject().FirstOrDefault(property =>
                property.Name.Equals("knownFields", StringComparison.OrdinalIgnoreCase)).Value;
            if (provenance.ValueKind != JsonValueKind.Undefined)
            {
                if (preset.SchemaVersion < 3 || preset.Page != ControlPageId.Performance)
                    errors.Add("已知字段标记需要性能预设版本 3");
                else if (provenance.ValueKind == JsonValueKind.Array)
                {
                    knownFields = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in provenance.EnumerateArray())
                        if (field.ValueKind != JsonValueKind.String ||
                            !fields.Contains(field.GetString(), StringComparer.OrdinalIgnoreCase) || !knownFields.Add(field.GetString()!))
                            errors.Add("已知字段标记无效");
                }
                else if (provenance.ValueKind != JsonValueKind.Null) errors.Add("已知字段标记必须为数组或 null");
            }
        }
        if (knownFields is not null && preset.Settings is not null &&
            fields.Any(field => !preset.Settings.Keys.Contains(field, StringComparer.OrdinalIgnoreCase)))
            errors.Add("部分本机预设缺少明确字段意图");
        if (preset.Settings is null) errors.Add("预设缺少字段意图");
        else foreach (var (field, request) in preset.Settings)
        {
            if (request is null || !Enum.IsDefined(request.Intent) ||
                (request.Intent == SettingIntent.Set
                    ? request.Value is null || request.Value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    : request.Value is not null))
            { errors.Add($"{field} 的字段意图无效"); continue; }
            if (knownFields is not null && !knownFields.Contains(field) && request.Intent != SettingIntent.Keep)
                errors.Add($"{field} 缺少已知源值，必须保持 Keep");
            if (field.Equals("knownFields", StringComparison.OrdinalIgnoreCase)) errors.Add("已知字段标记不能作为写入意图");
            if (request.Intent != SettingIntent.Set) continue;
            var valuePayload = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [field] = request.Value!.Value });
            bool exactBoost = preset.Page == ControlPageId.Performance && field.Equals("isBoostEnabled", StringComparison.OrdinalIgnoreCase) &&
                request.Value.Value.ValueKind == JsonValueKind.Object;
            if (exactBoost)
            {
                if (!CpuBoostPolicy.TryRead(request.Value.Value, out _)) errors.Add("Boost 策略必须包含有效的 AC/DC 模式");
                var savedPolicy = preset.Payload.ValueKind == JsonValueKind.Object
                    ? preset.Payload.EnumerateObject().FirstOrDefault(property => property.Name.Equals("boostPolicy", StringComparison.OrdinalIgnoreCase)).Value : default;
                if (savedPolicy.ValueKind != JsonValueKind.Object || !EquivalentValue(savedPolicy, request.Value.Value)) errors.Add("Boost 策略的 Set 值与 payload 不一致");
            }
            else
            {
                errors.AddRange(ValidatePayload(preset.Page, valuePayload).Errors);
                if (preset.Page == ControlPageId.Performance && field.Equals("isBoostEnabled", StringComparison.OrdinalIgnoreCase) &&
                    preset.Payload.ValueKind == JsonValueKind.Object && preset.Payload.EnumerateObject().Any(property =>
                        property.Name.Equals("boostPolicy", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object))
                    errors.Add("精确 Boost 策略须使用策略意图，或先清除策略再使用开关意图");
            }
            if (!exactBoost && preset.Payload.ValueKind == JsonValueKind.Object)
                foreach (var property in preset.Payload.EnumerateObject())
                    if (property.Name.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                        !(preset.SchemaVersion >= 3 && field.Equals("advancedCpuTuning", StringComparison.OrdinalIgnoreCase)
                            ? AdvancedTargetMatchesSource(property.Value, request.Value.Value)
                            : EquivalentValue(property.Value, request.Value.Value)))
                        errors.Add($"{field} 的 Set 值与 payload 不一致");
            if (!requireWritable) continue;
            var capability = capabilities?.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (capability is null || capability.Availability != CapabilityState.Available || !capability.Writable)
                errors.Add($"{field} 当前不可写");
            else if (request.Value!.Value.ValueKind == JsonValueKind.Number &&
                (!request.Value.Value.TryGetDouble(out var value) || !WithinRange(value, capability)))
                errors.Add($"{field} 超出当前设备范围 {capability.Minimum}–{capability.Maximum} {capability.Unit}");
        }
        return new(errors.Count == 0, errors.ToArray());
    }

    public static bool IsOptionalField(string field) => field.ToLowerInvariant() is "advancedcputuning" or "pptwatts" or "mp1temperaturec" or "pboscalar" or
        "negativecurveoptimizer" or "corefrequencylimitmhz" or "voltageboost" or "powerlimit" or "muxmode";

    public static bool IsVerifiedWritable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] FieldCapability? capability, long epoch) =>
        capability is { Availability: CapabilityState.Available, Writable: true, VerifiedAtUtc: not null } &&
        capability.Source != "uiSuggestion" && capability.HardwareEpoch == epoch;

    public static bool WithinRange(double value, FieldCapability capability) => double.IsFinite(value)
        && (capability.Minimum is not double minimum || value >= minimum)
        && (capability.Maximum is not double maximum || value <= maximum)
        && (capability.Step is not double step || step <= 0 || capability.Minimum is not double start ||
            Math.Abs((value - start) / step - Math.Round((value - start) / step)) < 0.000001);

    private static bool EquivalentValue(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Array && right.ValueKind == JsonValueKind.Array)
            return left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => EquivalentValue(pair.First, pair.Second));
        if (left.ValueKind != JsonValueKind.Object || right.ValueKind != JsonValueKind.Object)
            return JsonElement.DeepEquals(left, right);
        // Legacy typed DTOs and Web payloads differ only in property-name casing.
        try
        {
            var properties = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            return left.EnumerateObject().Count() == properties.Count && left.EnumerateObject().All(p =>
                properties.TryGetValue(p.Name, out var value) && EquivalentValue(p.Value, value));
        }
        catch (ArgumentException) { return false; }
    }

    private static bool AdvancedTargetMatchesSource(JsonElement source, JsonElement target)
    {
        if (source.ValueKind != JsonValueKind.Object || target.ValueKind != JsonValueKind.Object) return false;
        try
        {
            var fields = source.EnumerateObject().ToDictionary(property => property.Name, property => property.Value,
                StringComparer.OrdinalIgnoreCase);
            return target.EnumerateObject().All(property => fields.TryGetValue(property.Name, out var value) &&
                (property.Value.ValueKind == JsonValueKind.Null ||
                 (value.ValueKind == JsonValueKind.Object && property.Value.ValueKind == JsonValueKind.Object
                    ? AdvancedTargetMatchesSource(value, property.Value) : EquivalentValue(value, property.Value))));
        }
        catch (ArgumentException) { return false; }
    }

    public static PresetValidationResult ValidatePayload(ControlPageId page, JsonElement payload, bool sourceValues = false)
    {
        try { return ValidatePayloadCore(page, payload, sourceValues); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or JsonException)
        { return new(false, ["预设字段类型或重复字段无效"]); }
    }

    private static PresetValidationResult ValidatePayloadCore(ControlPageId page, JsonElement payload, bool sourceValues)
    {
        var errors = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object) return new(false, ["预设缺少有效 payload"]);
        var values = payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        bool OfficialCpu() => values.TryGetValue("useOfficialCpuPolicy", out var flag) && flag.ValueKind == JsonValueKind.True;
        void Number(string field, double min, double max, bool nullable = false, double step = 0, bool integer = false)
        {
            if (!values.TryGetValue(field, out var value)) return;
            if (nullable && value.ValueKind == JsonValueKind.Null) return;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) ||
                number < min || number > max || (integer || step >= 1) && !value.TryGetInt32(out _) ||
                step > 0 && Math.Abs(number / step - Math.Round(number / step)) > 0.000001)
                errors.Add($"{field} 必须在 {min}–{max} 范围内" + (step > 0 ? $"，步长 {step}" : ""));
        }
        switch (page)
        {
            case ControlPageId.Performance:
                Number("temperatureLimitC", 45, 100, integer: true);
                Number("splWatts", OfficialCpu() || sourceValues ? 20 : 45, OfficialCpu() || sourceValues ? 105 : 75, integer: true);
                Number("spptWatts", OfficialCpu() || sourceValues ? 20 : 45, OfficialCpu() || sourceValues ? 120 : 75, integer: true);
                Number("pptWatts", 45, 120, nullable: true, step: 0.001);
                Number("mp1TemperatureC", 40, 100, nullable: true, integer: true);
                Number("tdcCurrentMilliamps", 1000, 200000, nullable: true, integer: true);
                Number("edcCurrentMilliamps", 1000, 200000, nullable: true, integer: true);
                // Windows uses zero to remove a frequency cap; intermediate values remain invalid.
                foreach (var field in new[] { "maxFrequencyMhz", "acMaxFrequencyMhz", "dcMaxFrequencyMhz" })
                    if (!values.TryGetValue(field, out var frequency) || frequency.ValueKind != JsonValueKind.Number ||
                        !frequency.TryGetInt32(out int mhz) || mhz != 0) Number(field, 1500, 5400, integer: true);
                Number("acMinActiveCoresPercent", 0, 100, true, integer: true); Number("dcMinActiveCoresPercent", 0, 100, true, integer: true);
                Number("negativeCurveOptimizer", -50, 0, true, 1);
                foreach (var field in new[] { "isBoostEnabled", "useOfficialCpuPolicy" })
                    if (values.TryGetValue(field, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add($"{field} 必须为布尔值");
                if (values.TryGetValue("boostPolicy", out var boostPolicy) && boostPolicy.ValueKind != JsonValueKind.Null)
                {
                    if (!CpuBoostPolicy.TryRead(boostPolicy, out var policy)) errors.Add("Boost 策略必须包含 0–6 的 AC/DC 模式");
                    else if (values.TryGetValue("isBoostEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                        enabled.GetBoolean() != policy!.IsEnabled) errors.Add("Boost 策略与开关状态不一致");
                }
                if (values.TryGetValue("windowsPowerSchemeId", out var scheme) && (scheme.ValueKind != JsonValueKind.String || !scheme.TryGetGuid(out _))) errors.Add("Windows 电源方案无效");
                if (values.TryGetValue("advancedCpuTuning", out var advanced) && advanced.ValueKind != JsonValueKind.Null)
                {
                    if (advanced.ValueKind != JsonValueKind.Object) errors.Add("高级 CPU 配置无效");
                    else
                    {
                        bool official = OfficialCpu();
                        var outer = values;
                        values = advanced.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
                        foreach (var field in new[] { "stapmWatts", "fastPptWatts", "slowPptWatts" }) Number(field, official ? 1 : 45, official ? 250 : 75, true);
                        Number("pptWatts", official ? 1 : 45, official ? 250 : 120, true, step: 0.001);
                        foreach (var field in new[] { "vrmCurrentMilliamps", "tdcCurrentMilliamps", "edcCurrentMilliamps" }) Number(field, 1000, 200000, true, 1);
                        Number("mp1TemperatureC", 40, 100, true, integer: true); Number("rsmuTemperatureC", 40, 100, true, integer: true);
                        Number("pboScalar", 1, 10, true, 1); Number("curveOptimizerAll", -50, 0, true, 1);
                        if (values.TryGetValue("perCoreCurveOptimizer", out var cores) && cores.ValueKind != JsonValueKind.Null)
                        {
                            if (cores.ValueKind != JsonValueKind.Object || cores.EnumerateObject().Any(p =>
                                !int.TryParse(p.Name, out var index) || index is < 0 or >= 16 || !p.Value.TryGetInt32(out var co) || co is < -50 or > 0))
                                errors.Add("逐核 Curve Optimizer 无效");
                        }
                        values = outer;
                    }
                }
                break;
            case ControlPageId.Gpu:
                Number("coreFrequencyLimitMhz", 300, 5000, true, 1);
                Number("memoryOffsetKhz", -1000000, 3000000, true, 1000); Number("coreOffsetKhz", -1000000, 1000000, true, 1000);
                if (values.TryGetValue("resetCoreFrequencyLimit", out var reset) && reset.ValueKind == JsonValueKind.True &&
                    values.TryGetValue("coreFrequencyLimitMhz", out var limit) && limit.ValueKind != JsonValueKind.Null) errors.Add("GPU 官方频率与固定频率互斥");
                if (values.TryGetValue("vfOffsetsKhz", out var vf) && vf.ValueKind != JsonValueKind.Null &&
                    (vf.ValueKind != JsonValueKind.Array || vf.GetArrayLength() != 127 || !vf[0].TryGetInt32(out var first) || first != 0 ||
                     vf.EnumerateArray().Any(value => !value.TryGetInt32(out var offset) || offset is < -200000 or > 200000))) errors.Add("GPU VF 曲线无效");
                break;
            case ControlPageId.Fan:
                Number("profile", 0, 2, step: 1); Number("fixedRpm", 1, int.MaxValue, step: 1); Number("maximumRpm", 1, int.MaxValue, true, 1);
                foreach (var field in new[] { "isShared", "strongCooling" })
                    if (values.TryGetValue(field, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add($"{field} 必须为布尔值");
                if (values.TryGetValue("strategy", out var strategy) && (strategy.ValueKind != JsonValueKind.String || strategy.GetString() is not ("Auto" or "Fixed" or "Curve"))) errors.Add("风扇策略无效");
                foreach (var field in new[] { "cpu", "gpu", "shared" })
                    if (values.TryGetValue(field, out var points))
                    {
                        int previous = 29;
                        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() is < 2 or > 71) { errors.Add("风扇曲线无效"); continue; }
                        foreach (var point in points.EnumerateArray())
                        {
                            if (point.ValueKind != JsonValueKind.Object) { errors.Add("风扇曲线节点无效"); break; }
                            var fields = point.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
                            if (!fields.TryGetValue("temperature", out var t) || !t.TryGetInt32(out var temp) || temp <= previous || temp > 100 ||
                                !fields.TryGetValue("targetPercent", out var p) || !p.TryGetInt32(out var percent) || percent is < 0 or > 100) { errors.Add("风扇曲线节点无效"); break; }
                            previous = temp;
                        }
                    }
                break;
            case ControlPageId.Lighting:
                Number("brightness", 0, 3, step: 1); Number("speed", .5, 2);
                if (values.TryGetValue("effect", out var effect) && (effect.ValueKind != JsonValueKind.String || effect.GetString() is not ("Static" or "Gradient" or "Cycle"))) errors.Add("灯光效果无效");
                if (values.TryGetValue("hex", out var color) && (color.ValueKind != JsonValueKind.String || color.GetString() is not { Length: 7 } hex || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))) errors.Add("灯光颜色无效");
                if (values.TryGetValue("logo", out var logo) && logo.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add("Logo 必须为布尔值");
                break;
            default: errors.Add("未知的预设页面"); break;
        }
        return new(errors.Count == 0, errors.ToArray());
    }
}
