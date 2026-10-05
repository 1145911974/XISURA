using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public static class DefaultPerformancePresets
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PagePresetEnvelope Create(PresetKey key)
    {
        var draft = CreateDraft(key);
        return new PagePresetEnvelope(1, ControlPageId.Performance, key,
            PresetNameCatalog.GetName(new Dictionary<string, string>(), key),
            JsonSerializer.SerializeToElement(draft, Json), DateTimeOffset.UnixEpoch);
    }

    public static PerformanceDraft CreateDraft(PresetKey key)
    {
        PresetKey.Create(key.Mode, key.Slot);
        var (mode, temperature, spl, sppt, ac, dc, boost) = key.Mode switch
        {
            ControlModeId.Office or ControlModeId.Custom1 => (PerformanceMode.Quiet, 75, 55, 55, 3800, 3000, false),
            ControlModeId.Gaming or ControlModeId.Custom2 => (PerformanceMode.Balanced, 85, 60, 65, 4700, 3300, true),
            _ => (PerformanceMode.Turbo, 90, 65, 70, 4800, 3300, true)
        };
        // The balanced slot is the measured efficiency baseline, also used by Reset.
        // Other slots start at the mode's existing tuning recommendations.
        if (key.Slot == 2)
            (temperature, spl, sppt, ac, dc, boost) = (85, 45, 55, 4700, 3300, true);
        return new PerformanceDraft
        {
            Mode = mode,
            TemperatureLimitC = temperature,
            SplWatts = spl,
            SpptWatts = sppt,
            AcMaxFrequencyMhz = ac,
            DcMaxFrequencyMhz = dc,
            IsBoostEnabled = boost,
            WindowsPowerSchemeId = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
            AcMinActiveCoresPercent = 100,
            DcMinActiveCoresPercent = 17,
            NegativeCurveOptimizer = null,
            AdvancedCpuTuning = null
        };
    }
}
