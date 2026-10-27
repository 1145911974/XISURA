using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PassiveCpuPresetTests
{
    private static readonly string[] Fields = ["negativeCurveOptimizer", "acMaxFrequencyMhz", "dcMaxFrequencyMhz"];

    [TestMethod]
    public void Defaults_preserve_cpu_values_even_with_verified_write_capabilities()
    {
        var capabilities = new CapabilitySnapshot([])
        {
            Identity = new("MRID6-23", "V39", "AMD Ryzen 7 7745HX", "RTX 4070"),
            Fields = Fields.Select(field => new FieldCapability(field, CapabilityState.Available,
                true, true, -50, 6000, 1, "", "verifiedProfile", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1)).ToArray()
        };
        var preset = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Gaming, 2), capabilities);
        foreach (var field in Fields) Assert.AreEqual(SettingIntent.Keep, preset.Settings[field].Intent, field);
        Assert.IsNull(SavedPerformancePreset.ReadDraft(preset, preset.Key).NegativeCurveOptimizer);
    }

    [TestMethod]
    public void Unmodified_v4_defaults_migrate_without_preserving_implicit_tuning()
    {
        var preset = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Gaming, 2));
        var old = preset with { FactoryRevision = "2026-10-11.control-v4", Revision = 7, DisplayName = "保留名称",
            Settings = new(preset.Settings) { ["negativeCurveOptimizer"] = SettingRequest.Set(-30),
                ["acMaxFrequencyMhz"] = SettingRequest.Set(4800), ["dcMaxFrequencyMhz"] = SettingRequest.Set(3300) } };
        var migrated = DefaultControlPresets.Complete(old, resolveFactory: true);
        foreach (var field in Fields) Assert.AreEqual(SettingIntent.Keep, migrated.Settings[field].Intent, field);
        Assert.AreEqual(7L, migrated.Revision);
        Assert.AreEqual("保留名称", migrated.DisplayName);
    }

    [TestMethod]
    public void Explicit_user_tuning_survives_factory_revision_change()
    {
        var preset = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Gaming, 2));
        var settings = new Dictionary<string, SettingRequest>(preset.Settings)
        { ["negativeCurveOptimizer"] = SettingRequest.Set(-10), ["acMaxFrequencyMhz"] = SettingRequest.Set(4200) };
        var edited = preset with { FactoryRevision = "2026-10-11.control-v4", UserModified = true, Settings = settings,
            Payload = JsonSerializer.SerializeToElement(SavedPerformancePreset.ReadDraft(preset, preset.Key)
                with { NegativeCurveOptimizer = -10, AcMaxFrequencyMhz = 4200 }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var result = DefaultControlPresets.Complete(edited, resolveFactory: true);
        Assert.AreEqual(-10, result.Settings["negativeCurveOptimizer"].Value!.Value.GetInt32());
        Assert.AreEqual(4200, result.Settings["acMaxFrequencyMhz"].Value!.Value.GetInt32());
        Assert.IsTrue(result.UserModified);
    }
}
