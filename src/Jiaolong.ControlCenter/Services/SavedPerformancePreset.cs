using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public static class SavedPerformancePreset
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequiredFields =
    [
        "temperatureLimitC", "splWatts", "spptWatts", "acMaxFrequencyMhz", "dcMaxFrequencyMhz",
        "isBoostEnabled", "windowsPowerSchemeId", "acMinActiveCoresPercent", "dcMinActiveCoresPercent",
        "negativeCurveOptimizer", "advancedCpuTuning"
    ];

    public static PerformanceDraft ReadDraft(PagePresetEnvelope? envelope, PresetKey requestedKey)
    {
        PresetKey.Create(requestedKey.Mode, requestedKey.Slot);
        if (envelope is null)
            throw new InvalidOperationException($"未读取到 {requestedKey.Mode} 的预设 {requestedKey.Slot}。");
        if (envelope.Page != ControlPageId.Performance || envelope.Key != requestedKey || envelope.SchemaVersion != 1)
            throw new InvalidOperationException("性能预设身份或版本不匹配，未应用。");
        var payload = envelope.Payload;
        if (payload.ValueKind != JsonValueKind.Object || RequiredFields.Any(field => !payload.TryGetProperty(field, out _)))
            throw new InvalidOperationException("性能预设字段不完整，请在性能页重新保存；不会补入默认参数。");

        PerformanceDraft draft;
        try
        {
            draft = payload.Deserialize<PerformanceDraft>(Json)
                ?? throw new JsonException("Missing performance draft.");
            // The legacy MaxFrequencyMhz setter updates both fields during deserialization.
            draft.AcMaxFrequencyMhz = payload.GetProperty("acMaxFrequencyMhz").GetInt32();
            draft.DcMaxFrequencyMhz = payload.GetProperty("dcMaxFrequencyMhz").GetInt32();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("性能预设数据无效，请在性能页重新保存。", error);
        }
        var validation = PerformanceDraftValidator.Validate(draft);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0]);
        if (draft.AcMinActiveCoresPercent.HasValue != draft.DcMinActiveCoresPercent.HasValue ||
            draft.AcMinActiveCoresPercent is < 0 or > 100 || draft.DcMinActiveCoresPercent is < 0 or > 100)
            throw new InvalidOperationException("AC/DC 核心停泊比例必须成对保存且在 0–100% 之间。");
        return draft;
    }
}
