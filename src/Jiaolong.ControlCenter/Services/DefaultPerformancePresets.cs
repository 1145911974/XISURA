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
        int family = key.Mode switch
        {
            ControlModeId.Office or ControlModeId.Custom1 => 0,
            ControlModeId.Gaming or ControlModeId.Custom2 => 1,
            _ => 2
        };
        // Recommended starting points; limits are ceilings, not measured consumption or guaranteed clocks.
        var (temperature, spl, sppt, ac, dc, boost, activeCores) = (family, key.Slot) switch
        {
            (0, 1) => (75, 45, 45, 3200, 2400, false, 17),
            (0, 2) => (80, 45, 50, 4200, 2800, true, 33),
            (0, _) => (85, 45, 55, 4700, 3200, true, 50),
            (1, 1) => (80, 45, 50, 4500, 3000, true, 100),
            (1, 2) => (85, 50, 60, 4800, 3300, true, 100),
            (1, _) => (90, 60, 70, 5100, 3600, true, 100),
            (2, 1) => (90, 65, 70, 5000, 3300, true, 100),
            (2, 2) => (95, 75, 75, 5100, 3600, true, 100),
            _ => (95, 75, 75, 5100, 3800, true, 100)
        };
        return new PerformanceDraft
        {
            Mode = family == 0 ? PerformanceMode.Quiet : family == 1 ? PerformanceMode.Balanced : PerformanceMode.Turbo,
            TemperatureLimitC = temperature,
            SplWatts = spl,
            SpptWatts = sppt,
            AcMaxFrequencyMhz = ac,
            DcMaxFrequencyMhz = dc,
            IsBoostEnabled = boost,
            WindowsPowerSchemeId = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
            AcMinActiveCoresPercent = activeCores,
            DcMinActiveCoresPercent = 17,
            NegativeCurveOptimizer = null,
            AdvancedCpuTuning = null
        };
    }
}
