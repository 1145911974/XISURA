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
        object payload = page switch
        {
            ControlPageId.Gpu => new
            {
                CoreFrequencyLimitMhz = 2400,
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
        int profile = key.Mode == ControlModeId.Office ? 0 : key.Mode == ControlModeId.Turbo ? 2 : 1;
        var curves = new FanCurveDraft(profile);
        return new(profile, false, curves.Cpu.ToArray(), curves.Gpu.ToArray(), curves.Shared.ToArray());
    }

    public static PagePresetEnvelope Complete(PagePresetEnvelope preset)
    {
        var defaults = Create(preset.Page, preset.Key);
        if (preset.SchemaVersion != 1 || preset.Payload.ValueKind != JsonValueKind.Object)
            return defaults with { DisplayName = preset.DisplayName };

        var saved = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in preset.Payload.EnumerateObject()) saved[property.Name] = property.Value;
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in defaults.Payload.EnumerateObject())
            values[property.Name] = saved.TryGetValue(property.Name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? value.Clone() : property.Value.Clone();
        foreach (var property in saved)
            if (!values.ContainsKey(property.Key)) values[property.Key] = property.Value.Clone();

        // Preserve the old shared frequency when filling newer AC/DC fields.
        if (preset.Page == ControlPageId.Performance && saved.TryGetValue("maxFrequencyMhz", out var frequency) && frequency.ValueKind == JsonValueKind.Number)
            foreach (var name in new[] { "acMaxFrequencyMhz", "dcMaxFrequencyMhz" })
                if (!saved.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) values[name] = frequency.Clone();
        return preset with { Payload = JsonSerializer.SerializeToElement(values) };
    }
}
