using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.Prototype.Controls;
using Jiaolong_ControlCenter.ViewModels;
using LightingDraft = Jiaolong_ControlCenter.Prototype.Controls.LightingDraft;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class DefaultControlPresetTests
{
    [TestMethod]
    public void Balanced_defaults_leave_OEM_CPU_limits_and_fan_curve_under_firmware_control()
    {
        foreach (var mode in Enum.GetValues<ControlModeId>())
        {
            var key = PresetKey.Create(mode, 2);
            var envelope = DefaultControlPresets.Create(ControlPageId.Performance, key);
            Assert.IsTrue(envelope.Payload.GetProperty("useOfficialCpuPolicy").GetBoolean());
            var draft = SavedPerformancePreset.ReadDraft(envelope, key);
            var state = new CpuTuningState(85, 45, 65, 4500, true, null, draft.WindowsPowerSchemeId, null, "verified")
            { AcMaxFrequencyMhz = 4500, DcMaxFrequencyMhz = 3000, AcMinActiveCoresPercent = 100, DcMinActiveCoresPercent = 100 };
            var plan = PerformanceCommandFactory.CreatePresetCommands(draft, state, cpuTuningAvailable: true,
                smuAvailable: false, pboAvailable: false, curveOptimizerAvailable: true, forceApply: true);
            Assert.IsNull(plan.Error);
            Assert.IsTrue(plan.Steps.All(step => step.Command.Plan.TemperatureLimitC is null &&
                step.Command.Plan.SplWatts is null && step.Command.Plan.SpptWatts is null && step.Command.Plan.Advanced is null));
            var fan = DefaultControlPresets.Create(ControlPageId.Fan, key).Payload.Deserialize<FanCurveState>()!;
            Assert.AreEqual("Auto", fan.Strategy);
            var fanSettings = DefaultControlPresets.Create(ControlPageId.Fan, key).Settings;
            Assert.AreEqual(SettingIntent.Set, fanSettings["strategy"].Intent);
            Assert.AreEqual("Auto", fanSettings["strategy"].Value!.Value.GetString());
            Assert.IsTrue(fanSettings.Where(pair => pair.Key != "strategy").All(pair => pair.Value.Intent == SettingIntent.Keep));
            Assert.IsNull(fan.MaximumRpm);
            Assert.IsTrue(fan.IsValid());
            var customized = draft with { SplWatts = 90, SpptWatts = 90,
                AdvancedCpuTuning = new AdvancedCpuTuningDraft { SlowPptWatts = 65,
                    CurveOptimizerMode = CpuCurveOptimizerMode.AllCore, CurveOptimizerAll = -30 } };
            var saved = envelope with { UserModified = true,
                Settings = new(envelope.Settings) { ["advancedCpuTuning"] = SettingRequest.Set(customized.AdvancedCpuTuning) },
                Payload = JsonSerializer.SerializeToElement(customized, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            var restored = SavedPerformancePreset.ReadDraft(saved, key);
            Assert.AreEqual(90, restored.SplWatts);
            Assert.AreEqual(-30, restored.AdvancedCpuTuning!.CurveOptimizerAll);
            var capabilities = new CapabilitySnapshot([]) { Fields = [new("advancedCpuTuning",
                CapabilityState.Available, true, true, null, null, null, "", "hardwareReadback",
                "PreservesSelectedMode", DateTimeOffset.UtcNow, 7)] };
            var curvePlan = PerformanceCommandFactory.CreatePresetCommands(restored, state with { EnabledCoreCount = 16 },
                true, true, true, true, true, capabilities, 7);
            Assert.IsNull(curvePlan.Error);
            Assert.IsTrue(curvePlan.Steps.Any(step => step.Command.Plan.Advanced?.SlowPptWatts == 65), "Explicit SMU requests are independent of Official OEM limits");
            var invalidSmu = PerformanceCommandFactory.CreatePresetCommands(restored with { AdvancedCpuTuning = restored.AdvancedCpuTuning with { SlowPptWatts = 82 } }, state, true, true, true, true);
            Assert.IsNotNull(invalidSmu.Error);
            Assert.HasCount(0, invalidSmu.Steps, "Invalid explicit SMU must not silently disappear");
            var curveStep = curvePlan.Steps.Single(step => step.Label == "全核 Curve Optimizer");
            Assert.AreEqual(-30, curveStep.Command.Plan.NegativeCurveOptimizer);
            Assert.IsTrue(curvePlan.Steps.All(step => step.Command.Plan.SplWatts is null && step.Command.Plan.SpptWatts is null));
            Assert.IsTrue(PerformanceCommandFactory.MatchesReadBack(state with { EnabledCoreCount = 16,
                PerCoreCurveOptimizer = Enumerable.Range(0, 16).ToDictionary(core => core, _ => -30) }, curveStep.ReadbackPlan!));
        }
    }

    [TestMethod]
    public void Untouched_old_balanced_defaults_upgrade_but_custom_values_are_preserved()
    {
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var factory = DefaultControlPresets.Create(ControlPageId.Performance, key);
        var properties = factory.Payload.EnumerateObject().Where(p => p.Name != "useOfficialCpuPolicy")
            .ToDictionary(p => p.Name, p => p.Value);
        var legacy = factory with { SchemaVersion = 1, FactoryRevision = "", UserModified = true, DisplayName = "保留名称", Payload = JsonSerializer.SerializeToElement(properties) };
        var upgraded = DefaultControlPresets.Complete(legacy);
        Assert.AreEqual(legacy.UserModified, upgraded.UserModified);
        Assert.AreEqual(legacy.DisplayName, upgraded.DisplayName);
        properties["temperatureLimitC"] = JsonSerializer.SerializeToElement(81);
        var custom = DefaultControlPresets.Complete(legacy with { Payload = JsonSerializer.SerializeToElement(properties) });
        Assert.AreEqual(81, custom.Payload.GetProperty("temperatureLimitC").GetInt32());
        Assert.IsFalse(custom.Payload.GetProperty("useOfficialCpuPolicy").GetBoolean());
        var manual = factory with { Payload = JsonSerializer.SerializeToElement(
            SavedPerformancePreset.ReadDraft(factory, key) with { UseOfficialCpuPolicy = false }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        Assert.IsFalse(DefaultControlPresets.Complete(manual).Payload.GetProperty("useOfficialCpuPolicy").GetBoolean());
        var fan = DefaultControlPresets.Create(ControlPageId.Fan, key);
        var oldFan = fan with { Payload = JsonSerializer.SerializeToElement(fan.Payload.Deserialize<FanCurveState>()! with { Strategy = "Curve" }) };
        Assert.AreEqual("Curve", DefaultControlPresets.Complete(oldFan).Payload.Deserialize<FanCurveState>()!.Strategy);
        var customizedFan = oldFan with { Payload = JsonSerializer.SerializeToElement(oldFan.Payload.Deserialize<FanCurveState>()! with { FixedRpm = 3200 }) };
        Assert.AreEqual("Curve", DefaultControlPresets.Complete(customizedFan).Payload.Deserialize<FanCurveState>()!.Strategy);
        var historical = new PerformanceDraft { Mode = PerformanceMode.Balanced, UseOfficialCpuPolicy = false,
            TemperatureLimitC = 85, SplWatts = 50, SpptWatts = 60, AcMaxFrequencyMhz = 4800, DcMaxFrequencyMhz = 3300,
            IsBoostEnabled = true, WindowsPowerSchemeId = new("381b4222-f694-41f0-9685-ff5bb260df2e"),
            AcMinActiveCoresPercent = 100, DcMinActiveCoresPercent = 17, NegativeCurveOptimizer = null };
        var historicalEnvelope = new PagePresetEnvelope(1, ControlPageId.Performance, key, "历史名称",
            JsonSerializer.SerializeToElement(historical, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow);
        var migrated = DefaultControlPresets.Complete(historicalEnvelope);
        Assert.AreEqual(7L, DefaultControlPresets.Complete(historicalEnvelope with { Revision = 7 }).Revision,
            "Factory migration must retain the service-visible revision");
        var explicitLegacy = historicalEnvelope with { Revision = 7, Settings = new() { ["temperatureLimitC"] = SettingRequest.Set(85) } };
        Assert.AreEqual(SettingIntent.Set, DefaultControlPresets.Complete(explicitLegacy).Settings["temperatureLimitC"].Intent,
            "Explicit saved intents take precedence over numeric factory resemblance");
        Assert.AreEqual(DefaultPerformancePresets.FactoryRevision, migrated.FactoryRevision);
        Assert.IsFalse(migrated.UserModified);
        Assert.AreEqual("历史名称", migrated.DisplayName);
        foreach (var co in new int?[] { null, -30 })
        {
            var edited = historicalEnvelope with { Payload = JsonSerializer.SerializeToElement(historical with
                { TemperatureLimitC = 86, NegativeCurveOptimizer = co }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            var preserved = DefaultControlPresets.Complete(edited);
            Assert.IsTrue(preserved.UserModified);
            Assert.AreEqual(co, SavedPerformancePreset.ReadDraft(preserved, key).NegativeCurveOptimizer);
        }
        var oldGpuKey = PresetKey.Create(ControlModeId.Office, 1);
        var oldGpu = new PagePresetEnvelope(1, ControlPageId.Gpu, oldGpuKey, "历史GPU", JsonSerializer.SerializeToElement(new
            { CoreFrequencyLimitMhz = 1200, ResetCoreFrequencyLimit = false, MemoryOffsetKhz = 0, CoreOffsetKhz = 0, VfOffsetsKhz = new int[127] }), DateTimeOffset.UtcNow);
        var migratedGpu = DefaultControlPresets.Complete(oldGpu);
        Assert.IsFalse(migratedGpu.UserModified);
        Assert.AreEqual(SettingIntent.Official, migratedGpu.Settings["coreFrequencyLimitMhz"].Intent);
        Assert.IsTrue(DefaultControlPresets.Complete(oldGpu with { Payload = JsonSerializer.SerializeToElement(new
            { CoreFrequencyLimitMhz = 1200, ResetCoreFrequencyLimit = false, MemoryOffsetKhz = (int?)null, CoreOffsetKhz = 0, VfOffsetsKhz = new int[127] }) }).UserModified);
    }
    [TestMethod]
    public async Task Fresh_install_has_72_complete_usable_presets_without_writes()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var gpuType = typeof(GpuWorkspaceV2).GetNestedType("GpuWorkspacePreset", BindingFlags.NonPublic)!;
        var gpuValidate = typeof(GpuWorkspaceV2).GetMethod("ValidPreset", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var page in Enum.GetValues<ControlPageId>())
        foreach (var key in PresetKey.All)
        {
            var preset = await store.LoadAsync(page, key, CancellationToken.None);
            Assert.IsNotNull(preset, $"{page}/{key}");
            Assert.AreEqual(key, preset.Key);
            Assert.AreEqual(page, preset.Page);
            Assert.AreEqual(DateTimeOffset.UnixEpoch, preset.SavedAtUtc);
            Assert.AreEqual(PagePresetEnvelope.CurrentSchemaVersion, preset.SchemaVersion);
            Assert.AreEqual(DefaultPerformancePresets.FactoryRevision, preset.FactoryRevision);
            Assert.IsFalse(preset.UserModified);
            Assert.IsTrue(preset.Settings.Count > 0);
            if (page is ControlPageId.Fan or ControlPageId.Lighting)
                Assert.IsTrue(preset.Settings.All(pair => pair.Value.Intent != SettingIntent.Set ||
                    page == ControlPageId.Fan && pair.Key == "strategy" && pair.Value.Value!.Value.GetString() == "Auto"),
                    $"{page}: missing hardware verification must not manufacture required writes");
            switch (page)
            {
                case ControlPageId.Performance:
                    Assert.IsTrue(PerformanceDraftValidator.Validate(SavedPerformancePreset.ReadDraft(preset, key)).IsValid);
                    break;
                case ControlPageId.Gpu:
                    Assert.IsTrue((bool)gpuValidate.Invoke(null, [JsonSerializer.Deserialize(preset.Payload.GetRawText(), gpuType)])!);
                    bool reset = true;
                    Assert.AreEqual(reset, preset.Payload.GetProperty("ResetCoreFrequencyLimit").GetBoolean());
                    if (reset) Assert.AreEqual(JsonValueKind.Null, preset.Payload.GetProperty("CoreFrequencyLimitMhz").ValueKind);
                    else Assert.IsTrue(preset.Payload.GetProperty("CoreFrequencyLimitMhz").GetInt32() is >= 1200 and <= 2400);
                    Assert.AreEqual(0, preset.Payload.GetProperty("CoreOffsetKhz").GetInt32());
                    Assert.AreEqual(0, preset.Payload.GetProperty("MemoryOffsetKhz").GetInt32());
                    Assert.HasCount(127, preset.Payload.GetProperty("VfOffsetsKhz").EnumerateArray().ToArray());
                    Assert.IsFalse(preset.Payload.TryGetProperty("MuxMode", out _));
                    Assert.AreEqual(SettingIntent.Keep, preset.Settings["muxMode"].Intent);
                    break;
                case ControlPageId.Fan:
                    var fan = preset.Payload.Deserialize<FanCurveState>()!;
                    Assert.IsTrue(fan.IsValid());
                    Assert.AreEqual("Auto", fan.Strategy);
                    Assert.HasCount(0, FanCurveSafety.Assess(fan));
                    Assert.AreEqual(key.Mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 : key.Mode is ControlModeId.Turbo or ControlModeId.Custom3 ? 2 : 1, fan.Profile);
                    break;
                case ControlPageId.Lighting:
                    Assert.IsTrue(preset.Payload.Deserialize<LightingDraft>()!.IsValid());
                    break;
            }
        }
        Assert.HasCount(0, paths.WrittenPaths);
    }

    [TestMethod]
    public async Task Reset_each_page_uses_new_defaults_without_touching_other_slots()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        foreach (var page in Enum.GetValues<ControlPageId>())
        foreach (var key in PresetKey.All)
        {
            var sibling = PresetKey.Create(key.Mode, key.Slot % 3 + 1);
            var factory = DefaultControlPresets.Create(page, key);
            var edited = System.Text.Json.Nodes.JsonNode.Parse(factory.Payload.GetRawText())!.AsObject();
            string field = page switch { ControlPageId.Performance => "temperatureLimitC", ControlPageId.Gpu => "MemoryOffsetKhz", ControlPageId.Fan => "FixedRpm", _ => "Brightness" };
            edited[field] = page switch { ControlPageId.Performance => 88, ControlPageId.Gpu => 1000, ControlPageId.Fan => 3300, _ => 1 };
            var settings = new Dictionary<string, SettingRequest>(factory.Settings, StringComparer.OrdinalIgnoreCase);
            if (settings.TryGetValue(field, out var intent) && intent.Intent == SettingIntent.Set)
                settings[field] = new(SettingIntent.Set, JsonSerializer.SerializeToElement(edited[field]));
            await store.SaveAsync(factory with { DisplayName = "我的名字", Payload = JsonSerializer.SerializeToElement(edited), Settings = settings }, CancellationToken.None);
            var originalSibling = await store.LoadAsync(page, sibling, CancellationToken.None);
            var restored = await store.ResetAsync(page, key, CancellationToken.None);
            Assert.AreEqual("我的名字", restored.DisplayName);
            Assert.IsTrue(JsonElement.DeepEquals(DefaultControlPresets.Create(page, key).Payload, restored.Payload));
            Assert.IsTrue(JsonElement.DeepEquals(originalSibling!.Payload, (await store.LoadAsync(page, sibling, CancellationToken.None))!.Payload));
        }
        var count = paths.WrittenPaths.Count;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.ResetAsync(ControlPageId.Gpu, PresetKey.All[0], new CancellationToken(true)));
        Assert.HasCount(count, paths.WrittenPaths);
    }

    [TestMethod]
    public void Factory_clock_reset_is_explicit_and_does_not_change_legacy_limits()
    {
        var key = PresetKey.Create(ControlModeId.Turbo, 2);
        var legacy = DefaultControlPresets.Complete(new(1, ControlPageId.Gpu, key, "保留", JsonSerializer.SerializeToElement(new { CoreFrequencyLimitMhz = 2280 }), DateTimeOffset.UtcNow));
        Assert.AreEqual(2280, legacy.Payload.GetProperty("CoreFrequencyLimitMhz").GetInt32());
        Assert.IsFalse(legacy.Payload.GetProperty("ResetCoreFrequencyLimit").GetBoolean());
        var request = DefaultControlPresets.Complete(new(1, ControlPageId.Gpu, PresetKey.Create(ControlModeId.Office, 2), "原厂", JsonSerializer.SerializeToElement(new { ResetCoreFrequencyLimit = true }), DateTimeOffset.UtcNow));
        Assert.AreEqual(JsonValueKind.Null, request.Payload.GetProperty("CoreFrequencyLimitMhz").ValueKind);
        var gpuType = typeof(GpuWorkspaceV2).GetNestedType("GpuWorkspacePreset", BindingFlags.NonPublic)!;
        var validate = typeof(GpuWorkspaceV2).GetMethod("ValidPreset", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsTrue((bool)validate.Invoke(null, [JsonSerializer.Deserialize("{\"ResetCoreFrequencyLimit\":true}", gpuType)])!);
        Assert.IsFalse((bool)validate.Invoke(null, [JsonSerializer.Deserialize("{\"ResetCoreFrequencyLimit\":true,\"CoreFrequencyLimitMhz\":2400}", gpuType)])!);
    }

    [TestMethod]
    public async Task Missing_fields_are_completed_without_overwriting_saved_values_or_files()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Office, 2);
        var inputs = new Dictionary<ControlPageId, string>
        {
            [ControlPageId.Performance] = "{\"temperatureLimitC\":81,\"maxFrequencyMhz\":4000}",
            [ControlPageId.Gpu] = "{\"coreFrequencyLimitMhz\":2280,\"MemoryOffsetKhz\":null}",
            [ControlPageId.Fan] = "{\"Strategy\":\"Fixed\",\"FixedRpm\":2800}",
            [ControlPageId.Lighting] = "{\"Effect\":\"Static\",\"Brightness\":1,\"Hex\":\"#AABBCC\"}"
        };
        foreach (var (page, json) in inputs)
        {
            using var document = JsonDocument.Parse(json);
            await store.SaveAsync(new(1, page, key, "我的配置", document.RootElement.Clone(), DateTimeOffset.UtcNow), CancellationToken.None);
            var path = paths.WrittenPaths.Last();
            var before = await File.ReadAllTextAsync(path);
            var preset = (await store.LoadAsync(page, key, CancellationToken.None))!;
            Assert.AreEqual("我的配置", preset.DisplayName);
            switch (page)
            {
                case ControlPageId.Performance:
                    var cpu = SavedPerformancePreset.ReadDraft(preset, key);
                    Assert.AreEqual(81, cpu.TemperatureLimitC);
                    Assert.AreEqual(4000, cpu.AcMaxFrequencyMhz);
                    Assert.AreEqual(4000, cpu.DcMaxFrequencyMhz);
                    break;
                case ControlPageId.Gpu:
                    Assert.AreEqual(2280, preset.Payload.GetProperty("CoreFrequencyLimitMhz").GetInt32());
                    Assert.AreEqual(JsonValueKind.Null, preset.Payload.GetProperty("MemoryOffsetKhz").ValueKind);
                    break;
                case ControlPageId.Fan:
                    var fan = preset.Payload.Deserialize<FanCurveState>()!;
                    Assert.AreEqual("Fixed", fan.Strategy);
                    Assert.AreEqual(2800, fan.FixedRpm);
                    Assert.IsTrue(fan.IsValid());
                    break;
                case ControlPageId.Lighting:
                    var lighting = preset.Payload.Deserialize<LightingDraft>()!;
                    Assert.AreEqual(1, lighting.Brightness);
                    Assert.AreEqual("#AABBCC", lighting.Hex);
                    Assert.IsTrue(lighting.IsValid());
                    break;
            }
            Assert.AreEqual(before, await File.ReadAllTextAsync(path));
        }
        Assert.HasCount(4, paths.WrittenPaths);
    }

    [TestMethod]
    public async Task Unsupported_schema_or_non_object_payload_uses_default_without_destroying_file()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.All[0];
        foreach (var (schema, json) in new[] { (1, "[]"), (99, "{\"CoreFrequencyLimitMhz\":2400}") })
        {
            using var document = JsonDocument.Parse(json);
            var path = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "control-presets-gpu.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var entry = new PagePresetEnvelope(schema, ControlPageId.Gpu, key, "保留名称", document.RootElement.Clone(), DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Dictionary<string, PagePresetEnvelope> { [$"{key.Mode}:{key.Slot}"] = entry }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var before = await File.ReadAllTextAsync(path);
            var loaded = (await store.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None))!;
            Assert.AreEqual(schema == 99 ? 99 : PagePresetEnvelope.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.AreEqual("保留名称", loaded.DisplayName);
            if (schema != 99) Assert.AreEqual(0, loaded.Payload.GetProperty("CoreOffsetKhz").GetInt32());
            Assert.AreEqual(before, await File.ReadAllTextAsync(path));
        }
    }

    [TestMethod]
    public async Task Defaults_still_respect_cancellation_and_invalid_identities()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.LoadAsync(ControlPageId.Gpu, PresetKey.All[0], canceled.Token));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.LoadAsync((ControlPageId)99, PresetKey.All[0], CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.LoadAsync(ControlPageId.Fan, new(ControlModeId.Office, 0), CancellationToken.None));
        Assert.HasCount(0, paths.WrittenPaths);
    }
}
