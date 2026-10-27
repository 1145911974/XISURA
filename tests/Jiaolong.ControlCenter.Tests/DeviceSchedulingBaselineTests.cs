using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class DeviceSchedulingBaselineTests
{
    private static HomeStateSnapshot Snapshot(int spl = 54) => new(
        new CapabilitySnapshot([]) { Identity = new("MRID6-23", "V39", "7745HX", "RTX 4070"), SupportState = DeviceSupportState.Ready },
        null, new(PerformanceMode.Balanced, false, []) { CpuTuning = new(99, spl, 108, 0, true, 8,
            Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), -30, "hardwareReadback")
            { AcMaxFrequencyMhz = 0, DcMaxFrequencyMhz = 4500, AcBoostMode = 2, DcBoostMode = 2,
                AcMinActiveCoresPercent = 100, DcMinActiveCoresPercent = 17 },
            GpuVf = new(Enumerable.Range(0, 127).Select(index => new GpuVfNode(450 + index, 210, index == 0 ? 0 : 15000)).ToArray(),
                -200000, 200000, null) { CoreOffsetKhz = 10000, MemoryOffsetKhz = 1000 } });

    [TestMethod]
    public async Task Main_mode_selection_does_not_restore_the_balanced_preset_native_mode()
    {
        var paths = new RecordingPathProvider();
        var snapshot = Snapshot() with { Controls = Snapshot().Controls with { PerformanceMode = PerformanceMode.Turbo } };
        var baseline = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(snapshot, CancellationToken.None);
        await using var session = new HomeControlSession(new ControlCenterClient("mode-mapping-" + Guid.NewGuid().ToString("N")));
        typeof(HomeControlSession).GetProperty("State")!.SetValue(session, snapshot);
        typeof(HomeControlSession).GetField("schedulingBaseline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(session, baseline);
        var preferences = new UserPreferencesStore(paths);
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        foreach (var mode in new[] { ControlModeId.Office, ControlModeId.Gaming, ControlModeId.Turbo })
        {
            Assert.AreEqual(AdaptiveTargetMap.PerformanceModeFor(PresetKey.Create(mode, 2)), catalog.NativeModeForSelection(mode));
            Assert.AreEqual(PerformanceMode.Turbo, catalog.NativeModeForSelection(mode, PresetKey.Create(mode, 2)));
        }
        preferences.Save(preferences.Load() with { CustomBaseModes = new() { ["custom2"] = "office" } });
        Assert.AreEqual(PerformanceMode.Quiet, catalog.NativeModeForSelection(ControlModeId.Custom2));
    }

    [TestMethod]
    public async Task Fractional_readback_survives_baseline_capture_and_reload_without_rounding()
    {
        var snapshot = Snapshot();
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(snapshot.Controls.CpuTuning))!;
        node["SplWatts"] = null; node["SpptWatts"] = null;
        node["ObservedSplWatts"] = 40.5;
        node["AdvancedLimits"] = JsonSerializer.SerializeToNode(new Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan(FastPptWatts: 58.5));
        var cpu = node.Deserialize<CpuTuningState>()!;
        var paths = new RecordingPathProvider();
        var captured = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(
            snapshot with { Controls = snapshot.Controls with { CpuTuning = cpu } }, CancellationToken.None);
        Assert.IsNotNull(captured);
        var reopened = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(Snapshot(70), CancellationToken.None);
        Assert.IsNotNull(reopened);
        Assert.IsNull(reopened.Cpu.SplWatts);
        Assert.IsNull(reopened.Cpu.SpptWatts);
        Assert.AreEqual(40.5d, JsonSerializer.SerializeToElement(reopened.Cpu).GetProperty("ObservedSplWatts").GetDouble());
        Assert.AreEqual(58.5d, reopened.Cpu.AdvancedLimits!.FastPptWatts);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = DefaultPerformancePresets.Create(key, snapshot.Capabilities, reopened);
        Assert.AreEqual(SettingIntent.Keep, preset.Settings["splWatts"].Intent);
        Assert.AreEqual(SettingIntent.Keep, preset.Settings["spptWatts"].Intent);
        Assert.HasCount(1, paths.WrittenPaths);
    }

    [TestMethod]
    public async Task Baseline_is_persistent_and_not_replaced_by_later_manual_values()
    {
        var paths = new RecordingPathProvider();
        var first = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(Snapshot(), CancellationToken.None);
        var reopened = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(Snapshot(70), CancellationToken.None);
        Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first), JsonSerializer.SerializeToElement(reopened)));
        Assert.AreEqual(54, reopened!.Cpu.SplWatts);
        Assert.AreEqual(0, reopened.Cpu.AcMaxFrequencyMhz);
        Assert.AreEqual(1, paths.WrittenPaths.Count);
    }

    [TestMethod]
    public async Task Missing_readback_and_other_devices_cannot_reuse_or_create_a_baseline()
    {
        var paths = new RecordingPathProvider();
        var store = new DeviceSchedulingBaselineStore(paths);
        var missing = Snapshot() with { Controls = Snapshot().Controls with { CpuTuning = Snapshot().Controls.CpuTuning! with { SplWatts = null } } };
        Assert.IsNull(await store.LoadOrCaptureAsync(missing, CancellationToken.None));
        Assert.AreEqual(0, paths.WrittenPaths.Count);
        await store.LoadOrCaptureAsync(Snapshot(), CancellationToken.None);
        var other = missing with { Capabilities = missing.Capabilities with { Identity = new("OTHER", "V39", "7745HX", "RTX 4070") } };
        Assert.IsNull(await store.LoadOrCaptureAsync(other, CancellationToken.None));
        Assert.AreEqual(1, paths.WrittenPaths.Count);
    }

    [TestMethod]
    public async Task All_slots_keep_device_cpu_gpu_values_and_use_explicit_firmware_auto_fan_defaults()
    {
        var paths = new RecordingPathProvider();
        var baseline = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(Snapshot(), CancellationToken.None);
        var store = new ControlPresetStore(paths, baseline: () => baseline);
        foreach (var mode in Enum.GetValues<ControlModeId>())
        {
            var key = PresetKey.Create(mode, 2);
            var preset = (await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None))!;
            var draft = SavedPerformancePreset.ReadDraft(preset, key);
            Assert.AreEqual(99, draft.TemperatureLimitC);
            Assert.AreEqual(54, draft.SplWatts);
            Assert.AreEqual(108, draft.SpptWatts);
            Assert.AreEqual(0, draft.AcMaxFrequencyMhz);
            Assert.AreEqual(4500, draft.DcMaxFrequencyMhz);
            Assert.AreEqual(-30, draft.NegativeCurveOptimizer);
            Assert.AreEqual(PerformanceMode.Balanced, draft.Mode);
            Assert.IsTrue(preset.Settings.Values.All(value => value.Intent == SettingIntent.Keep));
            var commands = PerformanceCommandFactory.CreatePresetCommands(draft, baseline!.Cpu, true, true, true, true);
            Assert.IsNull(commands.Error);
            Assert.HasCount(0, commands.Steps);
            var gpu = (await store.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None))!;
            Assert.AreEqual(10000, gpu.Payload.GetProperty("CoreOffsetKhz").GetInt32());
            Assert.AreEqual(1000, gpu.Payload.GetProperty("MemoryOffsetKhz").GetInt32());
            Assert.AreEqual(15000, gpu.Payload.GetProperty("VfOffsetsKhz")[1].GetInt32());
            Assert.IsFalse(gpu.Payload.GetProperty("ResetCoreFrequencyLimit").GetBoolean());
            Assert.IsTrue(gpu.Settings.Values.All(value => value.Intent == SettingIntent.Keep));
            var fan = (await store.LoadAsync(ControlPageId.Fan, key, CancellationToken.None))!;
            Assert.AreEqual("Auto", fan.Payload.GetProperty("Strategy").GetString());
            Assert.AreEqual(mode is ControlModeId.Office or ControlModeId.Custom1 ? 0 :
                mode is ControlModeId.Turbo or ControlModeId.Custom3 ? 2 : 1, fan.Payload.GetProperty("Profile").GetInt32());
            Assert.AreEqual(SettingIntent.Set, fan.Settings["strategy"].Intent);
            Assert.AreEqual("Auto", fan.Settings["strategy"].Value!.Value.GetString());
            Assert.IsTrue(fan.Settings.Where(pair => pair.Key != "strategy").All(pair => pair.Value.Intent == SettingIntent.Keep));
            Assert.IsTrue(PresetValidation.Validate(gpu).IsValid);
            Assert.IsTrue(PresetValidation.Validate(fan).IsValid);
        }
        var savedKey = PresetKey.Create(ControlModeId.Gaming, 2);
        var factory = DefaultPerformancePresets.Create(savedKey, baseline: baseline);
        var changed = SavedPerformancePreset.ReadDraft(factory, savedKey) with { AcMaxFrequencyMhz = 4200 };
        await store.SaveAsync(factory with { UserModified = true,
            Payload = JsonSerializer.SerializeToElement(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Settings = new(factory.Settings) { ["acMaxFrequencyMhz"] = SettingRequest.Set(4200) } }, CancellationToken.None);
        var loaded = (await store.LoadAsync(ControlPageId.Performance, savedKey, CancellationToken.None))!;
        Assert.AreEqual(4200, SavedPerformancePreset.ReadDraft(loaded, savedKey).AcMaxFrequencyMhz);
        Assert.AreEqual(SettingIntent.Set, loaded.Settings["acMaxFrequencyMhz"].Intent);
        Assert.IsFalse(PerformanceDraftValidator.Validate(changed with { AcMaxFrequencyMhz = 1 }).IsValid);
        var verified = Snapshot().Capabilities with { Fields = new[]
        { "acMaxFrequencyMhz", "dcMaxFrequencyMhz", "isBoostEnabled", "windowsPowerSchemeId", "acMinActiveCoresPercent", "dcMinActiveCoresPercent" }
            .Select(field => new FieldCapability(field, CapabilityState.Available, true, true,
                field.EndsWith("FrequencyMhz") ? 0 : null, field.EndsWith("FrequencyMhz") ? 6000 : null,
                null, "", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1)).ToArray() };
        var targets = DefaultPerformancePresets.Create(savedKey, verified, baseline);
        Assert.AreEqual(SettingIntent.Set, targets.Settings["acMaxFrequencyMhz"].Intent);
        Assert.AreEqual(0, targets.Settings["acMaxFrequencyMhz"].Value!.Value.GetInt32());
        Assert.AreEqual(SettingIntent.Keep, targets.Settings["splWatts"].Intent);
        var commandsToRestore = PerformanceCommandFactory.CreatePresetCommands(SavedPerformancePreset.ReadDraft(targets, savedKey),
            baseline!.Cpu with { AcMaxFrequencyMhz = 4200 }, true, false, false, false);
        Assert.IsNull(commandsToRestore.Error);
        Assert.IsTrue(commandsToRestore.Steps.Any(step => step.Command.Plan.AcMaxFrequencyMhz == 0));
        Assert.IsTrue(commandsToRestore.Steps.All(step => step.Command.Plan.SplWatts is null && step.Command.Plan.NegativeCurveOptimizer is null));
    }

    [TestMethod]
    public async Task Balanced_restores_verified_pbo_from_baseline_without_adding_smu_limits()
    {
        var snapshot = Snapshot();
        var cpu = snapshot.Controls.CpuTuning! with { PboScalar = 4 };
        var baseline = await new DeviceSchedulingBaselineStore(new RecordingPathProvider()).LoadOrCaptureAsync(
            snapshot with { Controls = snapshot.Controls with { CpuTuning = cpu } }, CancellationToken.None);
        var field = new FieldCapability("pboScalar", CapabilityState.Available, true, true, 1, 10, 1,
            "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1);
        var caps = snapshot.Capabilities with { Fields = [field] };
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = DefaultPerformancePresets.Create(key, caps, baseline);
        Assert.AreEqual(SettingIntent.Set, preset.Settings["advancedCpuTuning"].Intent);
        var plan = preset.Settings["advancedCpuTuning"].Value!.Value.Deserialize<Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.AreEqual(new Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan(PboScalar: 4), plan);
        Assert.AreEqual(4, SavedPerformancePreset.ReadDraft(preset, key).AdvancedCpuTuning?.PboScalar);
        var validation = PresetValidation.Validate(preset);
        Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Errors));
        var commands = PerformanceCommandFactory.CreatePresetCommands(SavedPerformancePreset.ReadDraft(preset, key),
            cpu with { PboScalar = 7 }, true, false, true, false);
        Assert.IsNull(commands.Error);
        Assert.AreEqual(new Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan(PboScalar: 4), commands.Steps.Single().Command.Plan.Advanced);
        var draft = SavedPerformancePreset.ReadDraft(preset, key);
        Assert.AreEqual(new Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan(PboScalar: 4),
            PerformanceCommandFactory.Create(draft with { UseOfficialCpuPolicy = false, TemperatureLimitC = 85,
                SplWatts = 55, SpptWatts = 65 }, true).Plan.Advanced);
        var coField = field with { Key = "negativeCurveOptimizer", Minimum = -30, Maximum = 0 };
        var combined = DefaultPerformancePresets.Create(key, caps with { Fields = [field, coField] }, baseline);
        var combinedCommands = PerformanceCommandFactory.CreatePresetCommands(SavedPerformancePreset.ReadDraft(combined, key),
            cpu with { PboScalar = 7 }, true, false, true, true);
        Assert.IsNull(combinedCommands.Error);
        Assert.HasCount(2, combinedCommands.Steps);
        Assert.IsTrue(combinedCommands.Steps.Any(step => step.Command.Plan.NegativeCurveOptimizer == -30));
        Assert.AreEqual(SettingIntent.Set, DefaultPerformancePresets.Create(key,
            caps with { Fields = [field with { Key = "PBOSCALAR" }] }, baseline).Settings["advancedCpuTuning"].Intent);
        foreach (var guarded in new[]
        {
            DefaultPerformancePresets.Create(key, caps with { Fields = [field with { Writable = false }] }, baseline),
            DefaultPerformancePresets.Create(key, caps with { Fields = [field with { Readable = false }] }, baseline),
            DefaultPerformancePresets.Create(key, caps with { Fields = [field with { Maximum = 3 }] }, baseline),
            DefaultPerformancePresets.Create(key, caps, baseline! with { Cpu = cpu with { PboScalar = null } }),
            DefaultPerformancePresets.Create(key, caps, baseline! with { Cpu = cpu with { PboScalar = 11 } })
        }) Assert.AreEqual(SettingIntent.Keep, guarded.Settings["advancedCpuTuning"].Intent);
    }

    [TestMethod]
    public void Balanced_restores_verified_per_core_values_without_unwritable_pbo_or_smu()
    {
        var snapshot = Snapshot();
        var cores = Enumerable.Range(0, 8).ToDictionary(core => core, core => -5 - core);
        var cpu = snapshot.Controls.CpuTuning! with { NegativeCurveOptimizer = null, PboScalar = 4,
            PerCoreCurveOptimizer = cores };
        var baseline = new DeviceSchedulingBaseline(1, snapshot.Capabilities.Identity!, DateTimeOffset.UtcNow,
            PerformanceMode.Balanced, cpu);
        var field = new FieldCapability("negativeCurveOptimizer", CapabilityState.Available, true, true, -30, 0, 1,
            "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        foreach (bool allowPbo in new[] { false, true })
        {
            var caps = snapshot.Capabilities with { Fields = [field,
                field with { Key = "pboScalar", Minimum = 1, Maximum = 10, Writable = allowPbo }] };
            var preset = DefaultPerformancePresets.Create(key, caps, baseline);
            Assert.AreEqual(SettingIntent.Set, preset.Settings["advancedCpuTuning"].Intent);
            var validation = PresetValidation.Validate(preset);
            Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Errors));
            var plan = preset.Settings["advancedCpuTuning"].Value!.Value.Deserialize<Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            CollectionAssert.AreEquivalent(cores.ToArray(), plan.PerCoreCurveOptimizer!.ToArray());
            Assert.AreEqual(allowPbo ? 4 : (int?)null, plan.PboScalar);
            Assert.IsNull(plan.FastPptWatts);
            var draft = SavedPerformancePreset.ReadDraft(preset, key);
            Assert.AreEqual(CpuCurveOptimizerMode.PerCore, draft.AdvancedCpuTuning?.CurveOptimizerMode);
            var commands = PerformanceCommandFactory.CreatePresetCommands(draft,
                cpu with { PboScalar = 7 }, true, false, allowPbo, true);
            Assert.IsNull(commands.Error);
            Assert.HasCount(allowPbo ? 2 : 1, commands.Steps);
            CollectionAssert.AreEquivalent(cores.ToArray(), commands.Steps.Single(step =>
                step.Command.Plan.Advanced?.PerCoreCurveOptimizer is not null).Command.Plan.Advanced!.PerCoreCurveOptimizer!.ToArray());
            ((IDictionary<int, int>)draft.AdvancedCpuTuning!.PerCoreCurveOptimizer!)[0] = -30;
            Assert.AreEqual(-5, baseline.Cpu.PerCoreCurveOptimizer![0], "Editing a draft must not alter the saved baseline.");
        }
    }

    [TestMethod]
    public void Balanced_keeps_unverified_incomplete_or_positive_per_core_baselines()
    {
        var snapshot = Snapshot();
        var cpu = snapshot.Controls.CpuTuning! with { NegativeCurveOptimizer = null,
            PerCoreCurveOptimizer = Enumerable.Range(0, 8).ToDictionary(core => core, core => -5 - core) };
        var baseline = new DeviceSchedulingBaseline(1, snapshot.Capabilities.Identity!, DateTimeOffset.UtcNow,
            PerformanceMode.Balanced, cpu);
        var field = new FieldCapability("negativeCurveOptimizer", CapabilityState.Available, true, true, -30, 0, 1,
            "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1);
        var caps = snapshot.Capabilities with { Fields = [field] };
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        foreach (var guarded in new[]
        {
            DefaultPerformancePresets.Create(key, caps with { Fields = [field with { Writable = false }] }, baseline),
            DefaultPerformancePresets.Create(key, caps with { Fields = [field with { Readable = false }] }, baseline),
            DefaultPerformancePresets.Create(key, caps with { Fields = [field with { Minimum = -10 }] }, baseline),
            DefaultPerformancePresets.Create(key, caps, baseline with { Cpu = cpu with { CurveOptimizerVerification = "cached" } }),
            DefaultPerformancePresets.Create(key, caps, baseline with { Cpu = cpu with { EnabledCoreCount = 7 } }),
            DefaultPerformancePresets.Create(key, caps, baseline with { Cpu = cpu with
                { PerCoreCurveOptimizer = new Dictionary<int, int>(cpu.PerCoreCurveOptimizer!) { [0] = 10 } } })
        })
        {
            Assert.AreEqual(SettingIntent.Keep, guarded.Settings["advancedCpuTuning"].Intent);
            Assert.AreEqual(SettingIntent.Keep, guarded.Settings["negativeCurveOptimizer"].Intent);
        }
    }

    [TestMethod]
    public void Balanced_boost_baseline_restores_exact_modes_without_boolean_coercion()
    {
        var snapshot = Snapshot();
        var caps = snapshot.Capabilities with { Fields = [new("isBoostEnabled", CapabilityState.Available, true, true,
            null, null, null, "", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1)] };
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        foreach (var policy in new CpuBoostPolicy[] { new(4, 1), new(0, 4), new(2, 2), new(0, 0) })
        {
            var cpu = snapshot.Controls.CpuTuning! with { AcBoostMode = policy.AcMode, DcBoostMode = policy.DcMode,
                BoostEnabled = policy.IsEnabled };
            var baseline = new DeviceSchedulingBaseline(1, caps.Identity!, DateTimeOffset.UtcNow, PerformanceMode.Balanced, cpu);
            var preset = DefaultPerformancePresets.Create(key, caps, baseline);
            var draft = SavedPerformancePreset.ReadDraft(preset, key);
            Assert.AreEqual(policy, draft.BoostPolicy);
            Assert.AreEqual(SettingIntent.Set, preset.Settings["isBoostEnabled"].Intent);
            Assert.IsTrue(CpuBoostPolicy.TryRead(preset.Settings["isBoostEnabled"].Value!.Value, out var target));
            Assert.AreEqual(policy, target);
            var validation = PresetValidation.Validate(preset);
            Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Errors));
            var before = cpu with { AcBoostMode = policy.AcMode == 4 ? 2 : 4, DcBoostMode = 1, BoostEnabled = true };
            var commands = PerformanceCommandFactory.CreatePresetCommands(draft, before, true, false, false, false);
            Assert.IsNull(commands.Error);
            Assert.AreEqual(policy, commands.Steps.Single().Command.Plan.BoostPolicy);
            Assert.IsNull(commands.Steps.Single().Command.Plan.BoostEnabled);
            Assert.HasCount(0, PerformanceCommandFactory.CreatePresetCommands(draft, cpu, true, false, false, false).Steps);
        }
    }

    [TestMethod]
    public void Exact_boost_policy_survives_other_edits_and_clears_for_a_changed_switch()
    {
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var policy = new CpuBoostPolicy(4, 1);
        var previous = DefaultPerformancePresets.Create(key);
        var draft = SavedPerformancePreset.ReadDraft(previous, key) with { IsBoostEnabled = true, BoostPolicy = policy };
        previous = previous with { Payload = JsonSerializer.SerializeToElement(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Settings = new(previous.Settings) { ["isBoostEnabled"] = SettingRequest.Set(policy) } };
        Assert.IsTrue(PresetValidation.Validate(previous).IsValid);
        var changed = PresetCatalogController.MergeDraft(previous, previous.DisplayName,
            JsonSerializer.SerializeToElement(draft with { AcMaxFrequencyMhz = 3600 }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.IsFalse(changed.Settings.ContainsKey("boostPolicy"));
        Assert.IsTrue(CpuBoostPolicy.TryRead(changed.Settings["isBoostEnabled"].Value!.Value, out var retained));
        Assert.AreEqual(policy, retained);
        var legacyEdit = PresetCatalogController.MergeDraft(previous, previous.DisplayName,
            JsonSerializer.SerializeToElement(new { acMaxFrequencyMhz = 3600, isBoostEnabled = true, boostPolicy = (CpuBoostPolicy?)null }));
        Assert.IsTrue(CpuBoostPolicy.TryRead(legacyEdit.Settings["isBoostEnabled"].Value!.Value, out var legacyPolicy));
        Assert.AreEqual(policy, legacyPolicy);
        var off = (draft with { IsBoostEnabled = false }).WithBoostPolicy(policy);
        Assert.IsNull(off.BoostPolicy);
        var saved = PresetCatalogController.MergeDraft(previous, previous.DisplayName,
            JsonSerializer.SerializeToElement(off, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.AreEqual(JsonValueKind.False, saved.Settings["isBoostEnabled"].Value!.Value.ValueKind);
        Assert.IsTrue(PresetValidation.Validate(saved).IsValid);
        var missingMetadataOff = PresetCatalogController.MergeDraft(previous, previous.DisplayName,
            JsonSerializer.SerializeToElement(new { isBoostEnabled = false }));
        Assert.AreEqual(JsonValueKind.False, missingMetadataOff.Settings["isBoostEnabled"].Value!.Value.ValueKind);
        Assert.IsTrue(PresetValidation.Validate(missingMetadataOff).IsValid);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<CpuBoostPolicy>("{\"acMode\":4}"));
        Assert.IsFalse(PresetValidation.Validate(previous with
            { Settings = new(previous.Settings) { ["isBoostEnabled"] = SettingRequest.Set(true) } }).IsValid);
        var zero = JsonSerializer.SerializeToElement(new CpuBoostPolicy(0, 0), new JsonSerializerOptions
            { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault });
        Assert.IsTrue(zero.TryGetProperty("AcMode", out _));
        Assert.IsTrue(zero.TryGetProperty("DcMode", out _));
        var legacyOff = off with { SettingRequests = new() { ["isBoostEnabled"] = SettingRequest.Set(false) } };
        var partial = Snapshot().Controls.CpuTuning! with { AcBoostMode = 0, DcBoostMode = 4, BoostEnabled = false };
        var disable = PerformanceCommandFactory.CreatePresetCommands(legacyOff, partial, true, false, false, false);
        Assert.IsNull(disable.Error);
        Assert.AreEqual(false, disable.Steps.Single().Command.Plan.BoostEnabled);
        Assert.IsFalse(PerformanceCommandFactory.MatchesReadBack(partial, disable.Steps.Single().Command.Plan));
        foreach (var malformed in new[] { "{}", "{\"acMode\":\"4\",\"dcMode\":1}", "{\"acMode\":7,\"dcMode\":1}" })
            Assert.IsFalse(CpuBoostPolicy.TryRead(JsonSerializer.Deserialize<JsonElement>(malformed), out _));
    }

    [TestMethod]
    public async Task Corrupt_baseline_is_preserved_and_never_silently_recaptured()
    {
        var paths = new RecordingPathProvider();
        var store = new DeviceSchedulingBaselineStore(paths);
        await store.LoadOrCaptureAsync(Snapshot(), CancellationToken.None);
        var path = paths.WrittenPaths.Single();
        await File.WriteAllTextAsync(path, "broken baseline");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(Snapshot(70), CancellationToken.None));
        Assert.AreEqual("broken baseline", await File.ReadAllTextAsync(path));
    }
}
