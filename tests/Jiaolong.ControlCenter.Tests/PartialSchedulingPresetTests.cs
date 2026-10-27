using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PartialSchedulingPresetTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void Partial_defaults_use_only_captured_fields_and_keep_source_values_separate_from_writable_targets()
    {
        var identity = new HardwareIdentity("MRID6-23", "V35", "7745HX", "RTX 4070");
        var cpu = new CpuTuningState(null, null, null, null, true, 2,
            Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), null, "hardwareReadback")
        {
            AcMaxFrequencyMhz = 4700, DcMaxFrequencyMhz = 4500, AcBoostMode = 2, DcBoostMode = 3,
            ObservedSplWatts = 40.5, PboScalar = 4,
            PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -5, [1] = -10 }
        };
        var baseline = new DeviceSchedulingBaseline(2, identity, DateTimeOffset.UtcNow, PerformanceMode.Balanced, cpu)
        { CapturedCpuFields = ["acMaxFrequencyMhz", "dcMaxFrequencyMhz", "isBoostEnabled", "windowsPowerSchemeId", "advancedCpuTuning"] };
        var field = new FieldCapability("negativeCurveOptimizer", CapabilityState.Available, true, true, -50, 0, 1,
            "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1);
        var caps = new CapabilitySnapshot([]) { Identity = identity, Fields = [field,
            field with { Key = "pboScalar", Minimum = 1, Maximum = 10, Writable = false }] };
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = DefaultPerformancePresets.Create(key, caps, baseline);
        Assert.AreEqual(3, preset.SchemaVersion);
        var draft = SavedPerformancePreset.ReadDraft(preset, key);
        Assert.IsFalse(PerformancePresetProjection.IsKnown(draft, "splWatts"));
        Assert.IsFalse(PerformancePresetProjection.IsKnown(draft, "temperatureLimitC"));
        foreach (var unknown in new[] { "temperatureLimitC", "splWatts", "spptWatts", "negativeCurveOptimizer" })
            Assert.AreEqual(SettingIntent.Keep, preset.Settings[unknown].Intent);
        Assert.AreEqual(4700, draft.AcMaxFrequencyMhz);
        Assert.AreEqual(SettingIntent.Keep, preset.Settings["acMaxFrequencyMhz"].Intent);
        Assert.AreEqual(4, draft.AdvancedCpuTuning!.PboScalar);
        Assert.AreEqual(-10, draft.AdvancedCpuTuning.PerCoreCurveOptimizer![1]);
        var target = preset.Settings["advancedCpuTuning"].Value!.Value.Deserialize<AdvancedCpuTuningDraft>(Json)!;
        Assert.IsNull(target.PboScalar);
        Assert.AreEqual(-10, target.PerCoreCurveOptimizer![1]);
        Assert.IsTrue(PresetValidation.Validate(preset).IsValid);
        var commands = PerformanceCommandFactory.CreatePresetCommands(draft, cpu with { PboScalar = 7 }, true, false, false, true);
        Assert.IsNull(commands.Error);
        Assert.HasCount(1, commands.Steps);
        Assert.IsNull(commands.Steps[0].Command.Plan.Advanced!.PboScalar);
        Assert.AreEqual(-10, commands.Steps[0].Command.Plan.Advanced!.PerCoreCurveOptimizer![1]);
        var legacyCommands = PerformanceCommandFactory.CreatePresetCommands(draft with { KnownFields = null },
            cpu with { PboScalar = 7 }, true, false, false, true);
        Assert.IsNull(legacyCommands.Error);
        Assert.HasCount(1, legacyCommands.Steps);
        Assert.IsNull(legacyCommands.Steps[0].Command.Plan.Advanced!.PboScalar);

        var omitted = DefaultPerformancePresets.Create(key, caps, baseline with
        { CapturedCpuFields = ["acMaxFrequencyMhz", "dcMaxFrequencyMhz", "isBoostEnabled", "windowsPowerSchemeId"] });
        Assert.AreEqual(SettingIntent.Keep, omitted.Settings["advancedCpuTuning"].Intent);
        Assert.IsFalse(PerformancePresetProjection.IsKnown(SavedPerformancePreset.ReadDraft(omitted, key), "advancedCpuTuning"));
        Assert.IsTrue(DefaultControlPresets.Create(ControlPageId.Gpu, key, caps, baseline).Settings.Values.All(request => request.Intent == SettingIntent.Keep));
        var fan = DefaultControlPresets.Create(ControlPageId.Fan, key, caps, baseline);
        Assert.AreEqual(SettingIntent.Set, fan.Settings["strategy"].Intent);
        Assert.AreEqual("Auto", fan.Settings["strategy"].Value!.Value.GetString());
        Assert.IsTrue(fan.Settings.Where(pair => pair.Key != "strategy").All(pair => pair.Value.Intent == SettingIntent.Keep));
    }

    [TestMethod]
    public void Partial_provenance_requires_schema_three_and_survives_user_completion_without_inferred_unknown_sets()
    {
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var factory = DefaultPerformancePresets.Create(key);
        var draft = SavedPerformancePreset.ReadDraft(factory, key) with { KnownFields = ["acMaxFrequencyMhz"], AcMaxFrequencyMhz = 4100 };
        var partial = factory with { SchemaVersion = 3, UserModified = true, DisplayName = "我的源值", Payload = JsonSerializer.SerializeToElement(draft, Json),
            Settings = factory.Settings.Keys.ToDictionary(field => field, _ => SettingRequest.Keep, StringComparer.OrdinalIgnoreCase) };
        Assert.IsTrue(PresetValidation.Validate(partial).IsValid);
        Assert.IsFalse(PresetValidation.Validate(partial with { SchemaVersion = 2 }).IsValid);
        Assert.IsFalse(PresetValidation.Validate(partial with
        { Settings = new(partial.Settings) { ["splWatts"] = SettingRequest.Set(45) } }).IsValid);
        Assert.IsFalse(PresetValidation.Validate(partial with
        { Settings = new(partial.Settings) { ["splWatts"] = SettingRequest.Official } }).IsValid);
        var minimal = JsonSerializer.SerializeToElement(new { temperatureLimitC = 85 });
        Assert.IsTrue(PresetValidation.Validate(factory with { SchemaVersion = 1, Payload = minimal, Settings = new() }).IsValid);
        Assert.IsFalse(PresetValidation.Validate(factory with { SchemaVersion = 2, Payload = minimal, Settings = new() }).IsValid);
        var completed = DefaultControlPresets.Complete(partial with { Settings = new() }, resolveFactory: true);
        Assert.AreEqual(4100, completed.Payload.GetProperty("acMaxFrequencyMhz").GetInt32());
        CollectionAssert.AreEqual(new[] { "acMaxFrequencyMhz" }, SavedPerformancePreset.ReadDraft(completed, key).KnownFields);
        Assert.IsFalse(completed.Settings.ContainsKey("knownFields"));
        Assert.AreEqual(SettingIntent.Keep, completed.Settings["splWatts"].Intent);
        Assert.AreEqual(SettingIntent.Set, completed.Settings["acMaxFrequencyMhz"].Intent);
        var legacyUser = DefaultControlPresets.Complete(factory with { SchemaVersion = 2, UserModified = true },
            baseline: new(2, new("MRID6-23", "V35", "7745HX", "RTX 4070"), DateTimeOffset.UtcNow, PerformanceMode.Balanced,
                new(null, null, null, null, true, 8, Guid.NewGuid(), null, null)) { CapturedCpuFields = [] });
        Assert.IsNull(SavedPerformancePreset.ReadDraft(legacyUser, key).KnownFields);
    }
}
