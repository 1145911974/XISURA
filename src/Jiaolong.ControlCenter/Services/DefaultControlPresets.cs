using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype.Controls;

namespace Jiaolong_ControlCenter.Services;

public static class DefaultControlPresets
{
    internal static bool CanResolveDefault(CapabilitySnapshot? capabilities, string field, bool requireReadback = false)
    {
        var capability = capabilities?.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (capability is null || capability.HardwareEpoch <= 0 || !PresetValidation.IsVerifiedWritable(capability, capability.HardwareEpoch) ||
            requireReadback && !capability.Readable) return false;
        return capabilities!.Fields.All(item => item.HardwareEpoch == capability.HardwareEpoch);
    }

    public static PagePresetEnvelope Create(ControlPageId page, PresetKey key, CapabilitySnapshot? capabilities = null,
        DeviceSchedulingBaseline? baseline = null)
    {
        PresetKey.Create(key.Mode, key.Slot);
        if (page == ControlPageId.Performance) return DefaultPerformancePresets.Create(key, capabilities, baseline);
        int family = key.Mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 : key.Mode is ControlModeId.Turbo or ControlModeId.Custom3 ? 2 : 1;
        bool resetGpuClock = true;
        object payload = page switch
        {
            ControlPageId.Gpu => new
            {
                CoreFrequencyLimitMhz = resetGpuClock ? (int?)null : family == 0 ? 900 + 300 * key.Slot : family == 1 ? 1500 + 300 * key.Slot : 2400,
                ResetCoreFrequencyLimit = resetGpuClock,
                MemoryOffsetKhz = 0,
                CoreOffsetKhz = 0,
                VfOffsetsKhz = new int[127]
            },
            ControlPageId.Fan => CreateFanDraft(key),
            ControlPageId.Lighting => LightingDraft.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "未知的预设页面。")
        };
        var settings = page switch
        {
            ControlPageId.Gpu => new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase)
            { ["coreFrequencyLimitMhz"] = SettingRequest.Official, ["coreOffsetKhz"] = SettingRequest.Official,
                ["memoryOffsetKhz"] = SettingRequest.Official, ["vfOffsetsKhz"] = SettingRequest.Official,
                ["voltageBoost"] = SettingRequest.Keep, ["powerLimit"] = SettingRequest.Keep, ["muxMode"] = SettingRequest.Keep },
            ControlPageId.Fan => new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase)
            { ["strategy"] = SettingRequest.Set("Auto"), ["maximumRpm"] = SettingRequest.Keep, ["strongCooling"] = SettingRequest.Keep },
            _ => new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase)
            { ["effect"] = SettingRequest.Set("Gradient"), ["brightness"] = SettingRequest.Set(2), ["hex"] = SettingRequest.Set("#00D7E8"),
                ["speed"] = SettingRequest.Set(1), ["logo"] = SettingRequest.Set(true) }
        };
        foreach (var field in settings.Keys.ToArray())
            if (settings[field].Intent == SettingIntent.Set && !(page == ControlPageId.Fan && field == "strategy") &&
                !CanResolveDefault(capabilities, field))
                settings[field] = SettingRequest.Official;
        if (key.Slot == 2 && page == ControlPageId.Gpu && baseline is not null &&
            (capabilities?.Identity is null || capabilities.Identity == baseline.Identity))
        {
            var gpu = baseline.Gpu;
            int? Offset(int? value, int maximum) => value is >= -1000000 && value <= maximum && value % 1000 == 0 ? value : null;
            payload = new
            {
                CoreFrequencyLimitMhz = (int?)null, ResetCoreFrequencyLimit = false,
                CoreOffsetKhz = Offset(gpu?.CoreOffsetKhz, 1000000), MemoryOffsetKhz = Offset(gpu?.MemoryOffsetKhz, 3000000),
                VfOffsetsKhz = gpu?.VfOffsetsKhz is { Length: 127 } offsets && offsets[0] == 0 &&
                    offsets.All(value => value is >= -200000 and <= 200000) ? offsets : null
            };
            foreach (var field in settings.Keys.ToArray()) settings[field] = SettingRequest.Keep;
            if (page == ControlPageId.Gpu)
            {
                var values = JsonSerializer.SerializeToElement(payload).EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var field in new[] { "coreOffsetKhz", "memoryOffsetKhz", "vfOffsetsKhz" })
                    if (values[field].ValueKind != JsonValueKind.Null && CanResolveDefault(capabilities, field, requireReadback: true) &&
                        (values[field].ValueKind != JsonValueKind.Number || PresetValidation.WithinRange(values[field].GetInt32(),
                            capabilities!.Fields.First(capability => capability.Key.Equals(field, StringComparison.OrdinalIgnoreCase)))))
                        settings[field] = new(SettingIntent.Set, values[field]);
            }
        }
        return new(PagePresetEnvelope.CurrentSchemaVersion, page, key, PresetNameCatalog.GetName(new Dictionary<string, string>(), key),
            JsonSerializer.SerializeToElement(payload), DateTimeOffset.UnixEpoch)
        { FactoryRevision = DefaultPerformancePresets.FactoryRevision, UserModified = false, Revision = 1, Settings = settings };
    }

    private static FanCurveState CreateFanDraft(PresetKey key)
    {
        int profile = key.Mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 : key.Mode is ControlModeId.Turbo or ControlModeId.Custom3 ? 2 : 1;
        var curves = new FanCurveDraft(profile);
        int extra = (key.Slot - 1) * (profile == 2 ? 4 : 2);
        CurvePoint[] Cooling(IEnumerable<CurvePoint> points) => points.Select(point => point with { TargetPercent = Math.Min(100, point.TargetPercent + extra) }).ToArray();
        return new(profile, false, Cooling(curves.Cpu), Cooling(curves.Gpu), Cooling(curves.Shared),
            Strategy: "Auto");
    }

    public static PagePresetEnvelope Complete(PagePresetEnvelope preset, CapabilitySnapshot? capabilities = null, bool resolveFactory = false,
        DeviceSchedulingBaseline? baseline = null)
    {
        var defaults = Create(preset.Page, preset.Key, capabilities, baseline);
        if (preset.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion) return preset;
        PagePresetEnvelope ResolvedFactory() => defaults with
        { DisplayName = preset.DisplayName, SavedAtUtc = preset.SavedAtUtc, Revision = Math.Max(1, preset.Revision) };
        if (resolveFactory && !preset.UserModified && preset.FactoryRevision is "2026-10-08.control-v2" or "2026-10-10.control-v3" or "2026-10-11.control-v4" or "2026-10-14.control-v5" or "2026-10-14.control-v6" or DefaultPerformancePresets.FactoryRevision)
            return ResolvedFactory();
        if (preset.SchemaVersion < 1 || preset.Payload.ValueKind != JsonValueKind.Object)
            return defaults with { DisplayName = preset.DisplayName };

        var saved = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in preset.Payload.EnumerateObject()) saved[property.Name] = property.Value;
        if (preset.Page == ControlPageId.Lighting && saved.TryGetValue("effect", out var effect) &&
            effect.ValueKind == JsonValueKind.String && effect.GetString() == "Cycle")
            saved["effect"] = JsonSerializer.SerializeToElement("Gradient");
        bool hadOfficialPolicy = saved.ContainsKey("useOfficialCpuPolicy");
        if (preset.Page == ControlPageId.Performance && !hadOfficialPolicy)
            saved["useOfficialCpuPolicy"] = JsonSerializer.SerializeToElement(false);
        // Absence in an older saved preset means no reset request, even when the new factory default resets.
        if (preset.Page == ControlPageId.Gpu && (!saved.TryGetValue("ResetCoreFrequencyLimit", out var resetFlag) || resetFlag.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            saved["ResetCoreFrequencyLimit"] = JsonSerializer.SerializeToElement(false);
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in defaults.Payload.EnumerateObject())
        {
            // Partial factory provenance must never be inherited by a saved legacy/manual preset.
            if (preset.Page == ControlPageId.Performance && property.Name.Equals("knownFields", StringComparison.OrdinalIgnoreCase) &&
                !saved.ContainsKey("knownFields")) continue;
            values[property.Name] = saved.TryGetValue(property.Name, out var value) && value.ValueKind != JsonValueKind.Undefined
                ? value.Clone() : property.Value.Clone();
        }
        foreach (var property in saved)
            if (!values.ContainsKey(property.Key)) values[property.Key] = property.Value.Clone();
        if (preset.Page == ControlPageId.Gpu && saved.TryGetValue("ResetCoreFrequencyLimit", out var reset) && reset.ValueKind == JsonValueKind.True &&
            (!saved.TryGetValue("CoreFrequencyLimitMhz", out var limit) || limit.ValueKind == JsonValueKind.Null))
            values["CoreFrequencyLimitMhz"] = JsonSerializer.SerializeToElement((int?)null);

        // Preserve the old shared frequency when filling newer AC/DC fields.
        if (preset.Page == ControlPageId.Performance && saved.TryGetValue("maxFrequencyMhz", out var frequency) && frequency.ValueKind == JsonValueKind.Number)
            foreach (var name in new[] { "acMaxFrequencyMhz", "dcMaxFrequencyMhz" })
                if (!saved.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) values[name] = frequency.Clone();
        var payload = JsonSerializer.SerializeToElement(values);
        if (preset.SchemaVersion == 1 && string.IsNullOrEmpty(preset.FactoryRevision) && preset.Settings.Count == 0)
        {
            JsonElement? legacy = preset.Page switch
            {
                ControlPageId.Performance => JsonSerializer.SerializeToElement(
                    DefaultPerformancePresets.CreateLegacyDraft(preset.Key) with
                    { UseOfficialCpuPolicy = hadOfficialPolicy && saved["useOfficialCpuPolicy"].ValueKind == JsonValueKind.True },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ControlPageId.Fan => JsonSerializer.SerializeToElement(CreateFanDraft(preset.Key) with { Strategy = "Curve" }),
                ControlPageId.Gpu => CreateLegacyGpuPayload(preset.Key),
                ControlPageId.Lighting => defaults.Payload,
                _ => null
            };
            if (legacy is { } prior && !hadOfficialPolicy && preset.Page == ControlPageId.Performance)
                legacy = JsonSerializer.SerializeToElement(prior.EnumerateObject().Where(p => p.Name != "useOfficialCpuPolicy").ToDictionary(p => p.Name, p => p.Value));
            if (legacy is { } candidate && JsonElement.DeepEquals(preset.Payload, candidate))
                return ResolvedFactory();
            if (preset.Page == ControlPageId.Fan && JsonElement.DeepEquals(preset.Payload,
                JsonSerializer.SerializeToElement(CreateFanDraft(preset.Key) with { Strategy = "Auto" })))
                return ResolvedFactory();
        }
        var completed = preset with { SchemaVersion = PagePresetEnvelope.CurrentSchemaVersion,
            Payload = payload, Revision = Math.Max(1, preset.Revision) };
        var settings = preset.Settings.Count == 0 ? InferSettings(completed) : new(preset.Settings, StringComparer.OrdinalIgnoreCase);
        if (preset.Page == ControlPageId.Lighting && settings.TryGetValue("effect", out var setting) &&
            setting.Intent == SettingIntent.Set && setting.Value is { ValueKind: JsonValueKind.String } effectValue && effectValue.GetString() == "Cycle")
            settings["effect"] = SettingRequest.Set("Gradient");
        return completed with { Settings = settings };
    }

    private static JsonElement CreateLegacyGpuPayload(PresetKey key)
    {
        int family = key.Mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 : key.Mode is ControlModeId.Turbo or ControlModeId.Custom3 ? 2 : 1;
        bool reset = key.Slot == 2 || family == 2 && key.Slot == 3;
        return JsonSerializer.SerializeToElement(new
        {
            CoreFrequencyLimitMhz = reset ? (int?)null : family == 0 ? 900 + 300 * key.Slot : family == 1 ? 1500 + 300 * key.Slot : 2400,
            ResetCoreFrequencyLimit = reset, MemoryOffsetKhz = 0, CoreOffsetKhz = 0, VfOffsetsKhz = new int[127]
        });
    }

    public static Dictionary<string, SettingRequest> InferSettings(PagePresetEnvelope preset)
    {
        var result = new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase);
        var provenance = preset.Page == ControlPageId.Performance ? preset.Payload.EnumerateObject()
            .FirstOrDefault(property => property.Name.Equals("knownFields", StringComparison.OrdinalIgnoreCase)).Value : default;
        var known = provenance.ValueKind == JsonValueKind.Array
            ? provenance.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var property in preset.Payload.EnumerateObject())
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (name is "maxFrequencyMhz" or "mode" or "useOfficialCpuPolicy" or "resetCoreFrequencyLimit" or "boostPolicy" or "knownFields") continue;
            result[name] = property.Value.ValueKind == JsonValueKind.Null || known is not null && !known.Contains(name)
                ? SettingRequest.Keep : new(SettingIntent.Set, property.Value.Clone());
        }
        if (preset.Page == ControlPageId.Performance)
        {
            var rawPolicy = preset.Payload.EnumerateObject().FirstOrDefault(property => property.Name.Equals("boostPolicy", StringComparison.OrdinalIgnoreCase)).Value;
            if ((known is null || known.Contains("isBoostEnabled")) && CpuBoostPolicy.TryRead(rawPolicy, out var policy)) result["isBoostEnabled"] = SettingRequest.Set(policy!);
        }
        if (preset.Page == ControlPageId.Performance && preset.Payload.TryGetProperty("useOfficialCpuPolicy", out var official) && official.ValueKind == JsonValueKind.True)
            foreach (var field in new[] { "temperatureLimitC", "splWatts", "spptWatts" })
                result[field] = known is null || known.Contains(field) ? SettingRequest.Official : SettingRequest.Keep;
        if (preset.Page == ControlPageId.Gpu && preset.Payload.TryGetProperty("ResetCoreFrequencyLimit", out var reset) && reset.ValueKind == JsonValueKind.True)
            result["coreFrequencyLimitMhz"] = SettingRequest.Official;
        return result;
    }
}
