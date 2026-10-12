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

    public static PagePresetEnvelope Create(ControlPageId page, PresetKey key, CapabilitySnapshot? capabilities = null)
    {
        PresetKey.Create(key.Mode, key.Slot);
        if (page == ControlPageId.Performance) return DefaultPerformancePresets.Create(key, capabilities);
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
            { ["strategy"] = SettingRequest.Official, ["maximumRpm"] = SettingRequest.Official, ["strongCooling"] = SettingRequest.Set(false) },
            _ => new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase)
            { ["effect"] = SettingRequest.Set("Gradient"), ["brightness"] = SettingRequest.Set(2), ["hex"] = SettingRequest.Set("#00D7E8"),
                ["speed"] = SettingRequest.Set(1), ["logo"] = SettingRequest.Set(true) }
        };
        foreach (var field in settings.Keys.ToArray())
            if (settings[field].Intent == SettingIntent.Set && !CanResolveDefault(capabilities, field))
                settings[field] = SettingRequest.Official;
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

    public static PagePresetEnvelope Complete(PagePresetEnvelope preset, CapabilitySnapshot? capabilities = null, bool resolveFactory = false)
    {
        var defaults = Create(preset.Page, preset.Key, capabilities);
        if (preset.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion) return preset;
        PagePresetEnvelope ResolvedFactory() => defaults with
        { DisplayName = preset.DisplayName, SavedAtUtc = preset.SavedAtUtc, Revision = Math.Max(1, preset.Revision) };
        if (resolveFactory && !preset.UserModified && preset.FactoryRevision is "2026-10-08.control-v2" or "2026-10-10.control-v3" or DefaultPerformancePresets.FactoryRevision)
            return ResolvedFactory();
        if (preset.SchemaVersion < 1 || preset.Payload.ValueKind != JsonValueKind.Object)
            return defaults with { DisplayName = preset.DisplayName };

        var saved = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in preset.Payload.EnumerateObject()) saved[property.Name] = property.Value;
        bool hadOfficialPolicy = saved.ContainsKey("useOfficialCpuPolicy");
        if (preset.Page == ControlPageId.Performance && !hadOfficialPolicy)
            saved["useOfficialCpuPolicy"] = JsonSerializer.SerializeToElement(false);
        // Absence in an older saved preset means no reset request, even when the new factory default resets.
        if (preset.Page == ControlPageId.Gpu && (!saved.TryGetValue("ResetCoreFrequencyLimit", out var resetFlag) || resetFlag.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            saved["ResetCoreFrequencyLimit"] = JsonSerializer.SerializeToElement(false);
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in defaults.Payload.EnumerateObject())
            values[property.Name] = saved.TryGetValue(property.Name, out var value) && value.ValueKind != JsonValueKind.Undefined
                ? value.Clone() : property.Value.Clone();
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
        return completed with { Settings = preset.Settings.Count == 0 ? InferSettings(completed) : new(preset.Settings, StringComparer.OrdinalIgnoreCase) };
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
        foreach (var property in preset.Payload.EnumerateObject())
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (name is "maxFrequencyMhz" or "mode" or "useOfficialCpuPolicy" or "resetCoreFrequencyLimit") continue;
            result[name] = property.Value.ValueKind == JsonValueKind.Null ? SettingRequest.Keep : new(SettingIntent.Set, property.Value.Clone());
        }
        if (preset.Page == ControlPageId.Performance && preset.Payload.TryGetProperty("useOfficialCpuPolicy", out var official) && official.ValueKind == JsonValueKind.True)
            foreach (var field in new[] { "temperatureLimitC", "splWatts", "spptWatts" }) result[field] = SettingRequest.Official;
        if (preset.Page == ControlPageId.Gpu && preset.Payload.TryGetProperty("ResetCoreFrequencyLimit", out var reset) && reset.ValueKind == JsonValueKind.True)
            result["coreFrequencyLimitMhz"] = SettingRequest.Official;
        return result;
    }
}
