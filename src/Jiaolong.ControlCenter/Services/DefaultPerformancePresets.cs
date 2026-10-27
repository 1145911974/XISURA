using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public static class DefaultPerformancePresets
{
    public const string FactoryRevision = "2026-10-24.control-v7";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PagePresetEnvelope Create(PresetKey key, CapabilitySnapshot? capabilities = null,
        DeviceSchedulingBaseline? baseline = null)
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
        // Factory slots must not introduce undervolting or Windows clock caps.
        foreach (var field in new[] { "negativeCurveOptimizer", "acMaxFrequencyMhz", "dcMaxFrequencyMhz" })
            settings[field] = SettingRequest.Keep;
        if (verifiedCpu)
        {
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
        if (key.Slot == 2 && baseline is not null &&
            (baseline.SchemaVersion == 1 && DeviceSchedulingBaselineStore.HasCompleteCpuReadback(baseline.Cpu) ||
             baseline.SchemaVersion == 2 && baseline.CapturedCpuFields is not null) &&
            (capabilities?.Identity is null || capabilities.Identity == baseline.Identity))
        {
            var cpu = baseline.Cpu;
            bool Captured(string field) => baseline.SchemaVersion == 1 ||
                baseline.CapturedCpuFields!.Contains(field, StringComparer.OrdinalIgnoreCase);
            var sourceCores = Captured("advancedCpuTuning") && cpu.NegativeCurveOptimizer is null &&
                cpu.CurveOptimizerVerification == "hardwareReadback" && cpu.HasCompleteCurveValues() &&
                cpu.PerCoreCurveOptimizer!.Values.All(value => value <= 0)
                ? new Dictionary<int, int>(cpu.PerCoreCurveOptimizer) : null;
            draft = draft with
            {
                Mode = baseline.NativeMode, UseOfficialCpuPolicy = true,
                KnownFields = baseline.SchemaVersion == 2 ? settings.Keys.Where(Captured).Order(StringComparer.Ordinal).ToArray() : null,
                TemperatureLimitC = Captured("temperatureLimitC") ? cpu.TemperatureLimitC ?? draft.TemperatureLimitC : draft.TemperatureLimitC,
                SplWatts = Captured("splWatts") ? cpu.SplWatts ?? draft.SplWatts : draft.SplWatts,
                SpptWatts = Captured("spptWatts") ? cpu.SpptWatts ?? draft.SpptWatts : draft.SpptWatts,
                AcMaxFrequencyMhz = Captured("acMaxFrequencyMhz") ? cpu.AcMaxFrequencyMhz ?? draft.AcMaxFrequencyMhz : draft.AcMaxFrequencyMhz,
                DcMaxFrequencyMhz = Captured("dcMaxFrequencyMhz") ? cpu.DcMaxFrequencyMhz ?? draft.DcMaxFrequencyMhz : draft.DcMaxFrequencyMhz,
                IsBoostEnabled = Captured("isBoostEnabled") ? cpu.BoostEnabled ?? draft.IsBoostEnabled : draft.IsBoostEnabled,
                BoostPolicy = Captured("isBoostEnabled") ? CpuBoostPolicy.FromState(cpu) : null,
                WindowsPowerSchemeId = Captured("windowsPowerSchemeId") ? cpu.WindowsPowerSchemeId ?? Guid.Empty : Guid.Empty,
                AcMinActiveCoresPercent = Captured("acMinActiveCoresPercent") && cpu.AcMinActiveCoresPercent.HasValue && cpu.DcMinActiveCoresPercent.HasValue ? cpu.AcMinActiveCoresPercent : null,
                DcMinActiveCoresPercent = Captured("dcMinActiveCoresPercent") && cpu.AcMinActiveCoresPercent.HasValue && cpu.DcMinActiveCoresPercent.HasValue ? cpu.DcMinActiveCoresPercent : null,
                NegativeCurveOptimizer = Captured("negativeCurveOptimizer") && cpu.CurveOptimizerVerification == "hardwareReadback" && cpu.NegativeCurveOptimizer is >= -50 and <= 0
                    ? cpu.NegativeCurveOptimizer : null,
                AdvancedCpuTuning = Captured("advancedCpuTuning") && (cpu.PboScalar is >= 1 and <= 10 || sourceCores is not null)
                    ? new AdvancedCpuTuningDraft { PboScalar = cpu.PboScalar is >= 1 and <= 10 ? cpu.PboScalar : null,
                        CurveOptimizerMode = sourceCores is null ? null : CpuCurveOptimizerMode.PerCore, PerCoreCurveOptimizer = sourceCores } : null
            };
            foreach (var field in settings.Keys.ToArray()) settings[field] = SettingRequest.Keep;
            void Restore(string field, object? value, bool oem = false)
            {
                if (Captured(field)) Resolve(field, value, oem);
            }
            // These are targets for an explicit Use action, never startup writes.
            Restore("acMaxFrequencyMhz", cpu.AcMaxFrequencyMhz);
            Restore("dcMaxFrequencyMhz", cpu.DcMaxFrequencyMhz);
            Restore("windowsPowerSchemeId", cpu.WindowsPowerSchemeId);
            Restore("acMinActiveCoresPercent", draft.AcMinActiveCoresPercent);
            Restore("dcMinActiveCoresPercent", draft.DcMinActiveCoresPercent);
            Restore("isBoostEnabled", draft.BoostPolicy);
            Restore("negativeCurveOptimizer", draft.NegativeCurveOptimizer);
            bool CanRestoreAdvanced(string field, IEnumerable<int> values) =>
                DefaultControlPresets.CanResolveDefault(capabilities, field, requireReadback: true) &&
                values.All(value => PresetValidation.WithinRange(value,
                    capabilities!.Fields.First(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase))));
            int? pboTarget = draft.AdvancedCpuTuning?.PboScalar is int scalar && CanRestoreAdvanced("pboScalar", [scalar])
                ? scalar : null;
            var coreTarget = sourceCores is not null && CanRestoreAdvanced("negativeCurveOptimizer", sourceCores.Values)
                ? new Dictionary<int, int>(sourceCores) : null;
            if (pboTarget is not null || coreTarget is not null)
            {
                // A Set payload must not carry read-only siblings from the full baseline.
                var target = new AdvancedCpuTuningDraft
                { PboScalar = pboTarget, CurveOptimizerMode = coreTarget is null ? null : CpuCurveOptimizerMode.PerCore,
                    PerCoreCurveOptimizer = coreTarget };
                if (baseline.SchemaVersion == 1) draft = draft with { AdvancedCpuTuning = target };
                settings["advancedCpuTuning"] = SettingRequest.Set(target);
            }
            if (verifiedCpu)
            {
                Restore("temperatureLimitC", cpu.TemperatureLimitC, oem: true);
                Restore("splWatts", cpu.SplWatts, oem: true);
                Restore("spptWatts", cpu.SpptWatts, oem: true);
            }
        }
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
            NegativeCurveOptimizer = null };
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
