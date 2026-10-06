using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype.Controls;

namespace Jiaolong_ControlCenter.Services;

public static class DefaultControlPresets
{
    public static PagePresetEnvelope Create(ControlPageId page, PresetKey key)
    {
        PresetKey.Create(key.Mode, key.Slot);
        if (page == ControlPageId.Performance) return DefaultPerformancePresets.Create(key);
        int family = key.Mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 : key.Mode is ControlModeId.Turbo or ControlModeId.Custom3 ? 2 : 1;
        bool resetGpuClock = family == 2 && key.Slot >= 2;
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
        return new(1, page, key, PresetNameCatalog.GetName(new Dictionary<string, string>(), key),
            JsonSerializer.SerializeToElement(payload), DateTimeOffset.UnixEpoch);
    }

    private static FanCurveState CreateFanDraft(PresetKey key)
    {
        int profile = key.Mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 : key.Mode is ControlModeId.Turbo or ControlModeId.Custom3 ? 2 : 1;
        var curves = new FanCurveDraft(profile);
        int extra = (key.Slot - 1) * (profile == 2 ? 4 : 2);
        CurvePoint[] Cooling(IEnumerable<CurvePoint> points) => points.Select(point => point with { TargetPercent = Math.Min(100, point.TargetPercent + extra) }).ToArray();
        return new(profile, false, Cooling(curves.Cpu), Cooling(curves.Gpu), Cooling(curves.Shared));
    }

    public static PagePresetEnvelope Complete(PagePresetEnvelope preset)
    {
        var defaults = Create(preset.Page, preset.Key);
        if (preset.SchemaVersion != 1 || preset.Payload.ValueKind != JsonValueKind.Object)
            return defaults with { DisplayName = preset.DisplayName };

        var saved = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in preset.Payload.EnumerateObject()) saved[property.Name] = property.Value;
        // Absence in an older saved preset means no reset request, even when the new factory default resets.
        if (preset.Page == ControlPageId.Gpu && (!saved.TryGetValue("ResetCoreFrequencyLimit", out var resetFlag) || resetFlag.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            saved["ResetCoreFrequencyLimit"] = JsonSerializer.SerializeToElement(false);
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in defaults.Payload.EnumerateObject())
            values[property.Name] = saved.TryGetValue(property.Name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
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
        return preset with { Payload = JsonSerializer.SerializeToElement(values) };
    }
}
