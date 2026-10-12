using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public static class DefaultPerformancePresets
{
    public const string FactoryRevision = "2026-10-11.control-v4";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PagePresetEnvelope Create(PresetKey key, CapabilitySnapshot? capabilities = null)
    {
        var draft = CreateDraft(key);
        var settings = new[] { "temperatureLimitC", "splWatts", "spptWatts", "acMaxFrequencyMhz", "dcMaxFrequencyMhz",
            "isBoostEnabled", "windowsPowerSchemeId", "acMinActiveCoresPercent", "dcMinActiveCoresPercent", "negativeCurveOptimizer", "advancedCpuTuning" }
            .ToDictionary(field => field, _ => SettingRequest.Official, StringComparer.OrdinalIgnoreCase);
        int family = key.Mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 : key.Mode is ControlModeId.Gaming or ControlModeId.Custom2 ? 1 : 2;
        var (temperature, spl, sppt, ac, dc) = (family, key.Slot) switch
        {
            (0, 1) => (75, 45, 45, (int?)3200, (int?)2400),
            (0, 2) => (80, 45, 50, (int?)4200, (int?)2800),
            (0, 3) => (85, 45, 55, (int?)4700, (int?)3200),
            (1, 1) => (85, 45, 50, (int?)4500, (int?)3000),
            (1, 2) => (85, 50, 60, (int?)4800, (int?)3300),
            (1, 3) => (90, 55, 65, (int?)5100, (int?)3600),
            (2, 1) => (90, 55, 65, (int?)4500, (int?)3300),
            (2, 2) => (95, 75, 75, (int?)5100, (int?)3600),
            (2, 3) => (95, 75, 75, (int?)5100, (int?)3800),
            _ => (95, 45, 65, null, (int?)null)
        };
        bool verifiedCpu = capabilities?.Identity?.CpuModel.Contains("7745HX", StringComparison.OrdinalIgnoreCase) == true;
        void Resolve(string field, object? value, bool oem = false)
        {
            var capability = capabilities?.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (value is null || !DefaultControlPresets.CanResolveDefault(capabilities, field, requireReadback: true) ||
                oem && (capability!.NativeModeSideEffect != "PreservesSelectedMode" ||
                    capability.Minimum is null || capability.Maximum is null || capability.VerifiedAtUtc is null || capabilities?.IsOnAcPower != true)) return;
            if ((field is "acMaxFrequencyMhz" or "dcMaxFrequencyMhz") &&
                (capability is not { Minimum: double minimum, Maximum: double maximum } ||
                 !double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)) return;
            if (value is int number && !PresetValidation.WithinRange(number, capability!)) return;
            settings[field] = SettingRequest.Set(value);
        }
        Resolve("isBoostEnabled", !(family == 0 && key.Slot != 3));
        settings["negativeCurveOptimizer"] = SettingRequest.Set(-30);
        if (verifiedCpu)
        {
            // OEM policy does not reset Windows frequency caps left by another preset or process.
            Resolve("acMaxFrequencyMhz", ac); Resolve("dcMaxFrequencyMhz", dc);
            if (key.Slot != 2)
            {
                Resolve("temperatureLimitC", temperature, true); Resolve("splWatts", spl, true); Resolve("spptWatts", sppt, true);
            }
        }
        draft = draft with
        {
            TemperatureLimitC = settings["temperatureLimitC"].Intent == SettingIntent.Set ? temperature : draft.TemperatureLimitC,
            SplWatts = settings["splWatts"].Intent == SettingIntent.Set ? spl : draft.SplWatts,
            SpptWatts = settings["spptWatts"].Intent == SettingIntent.Set ? sppt : draft.SpptWatts,
            AcMaxFrequencyMhz = settings["acMaxFrequencyMhz"].Intent == SettingIntent.Set ? ac!.Value : draft.AcMaxFrequencyMhz,
            DcMaxFrequencyMhz = settings["dcMaxFrequencyMhz"].Intent == SettingIntent.Set ? dc!.Value : draft.DcMaxFrequencyMhz,
            IsBoostEnabled = settings["isBoostEnabled"].Intent == SettingIntent.Set ? settings["isBoostEnabled"].Value!.Value.GetBoolean() : draft.IsBoostEnabled,
            UseOfficialCpuPolicy = settings["temperatureLimitC"].Intent != SettingIntent.Set && settings["splWatts"].Intent != SettingIntent.Set && settings["spptWatts"].Intent != SettingIntent.Set
        };
        return new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key,
            PresetNameCatalog.GetName(new Dictionary<string, string>(), key),
            JsonSerializer.SerializeToElement(draft, Json), DateTimeOffset.UnixEpoch)
        { FactoryRevision = FactoryRevision, UserModified = false, Revision = 1, Settings = settings };
    }

    public static PerformanceDraft CreateDraft(PresetKey key)
    {
        var legacy = CreateLegacyDraft(key);
        // Numeric draft fields are editor placeholders; Settings carries the resolved write intent.
        return legacy with { UseOfficialCpuPolicy = true, AcMaxFrequencyMhz = 4500, DcMaxFrequencyMhz = 4500,
            WindowsPowerSchemeId = Guid.Empty, AcMinActiveCoresPercent = null, DcMinActiveCoresPercent = null,
            NegativeCurveOptimizer = -30 };
    }

    internal static PerformanceDraft CreateLegacyDraft(PresetKey key)
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
            UseOfficialCpuPolicy = key.Slot == 2,
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
