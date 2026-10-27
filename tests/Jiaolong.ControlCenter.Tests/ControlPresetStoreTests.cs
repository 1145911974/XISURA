using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ControlPresetStoreTests
{
    [TestMethod]
    public void Advanced_curve_payloads_use_the_supported_range_for_all_and_per_core_curves()
    {
        foreach (int value in new[] { -50, -30, 0, -51, 1, 30 })
        {
            bool expected = value is >= -50 and <= 0;
            foreach (JsonElement payload in new[]
            {
                JsonSerializer.SerializeToElement(new { advancedCpuTuning = new { curveOptimizerAll = value } }),
                JsonSerializer.SerializeToElement(new { advancedCpuTuning = new {
                    perCoreCurveOptimizer = new Dictionary<int, int> { [0] = value } } })
            })
                Assert.AreEqual(expected, PresetValidation.ValidatePayload(ControlPageId.Performance, payload).IsValid,
                    $"{value}: {payload}");
        }
    }

    [TestMethod]
    public async Task Lighting_follow_snapshots_cover_native_modes_when_adaptive_targets_are_empty()
    {
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Update(saved => saved with { LightingFollowPreset = true,
            LightingPresetSlots = new() { ["Office"] = 3, ["Gaming"] = 1, ["Turbo"] = 2 } });
        await using var session = new HomeControlSession(new ControlCenterClient("unused-lighting-snapshots-" + Guid.NewGuid().ToString("N")),
            automaticRestore: new AppliedConfigurationRestore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        var snapshots = await catalog.ReadAutomaticServicePresetsAsync([], CancellationToken.None);
        Assert.HasCount(3, snapshots);
        foreach (var mode in new[] { ControlModeId.Office, ControlModeId.Gaming, ControlModeId.Turbo })
        {
            var snapshot = snapshots.Single(item => item.Key == PresetKey.Create(mode, 1));
            Assert.AreEqual(AdaptiveTargetMap.HardwareModeFor(snapshot.Key), snapshot.PerformanceMode);
            var lighting = snapshot.Selections.Single(item => item.Page == ControlPageId.Lighting);
            Assert.IsTrue(lighting.Follow);
            Assert.AreEqual(preferences.Load().LightingPresetSlots[mode.ToString()], lighting.Key.Slot);
            Assert.IsFalse(snapshot.Selections.Any(item => item.Page != ControlPageId.Lighting && item.Follow));
        }
    }

    [TestMethod]
    public void Preset_key_exposes_six_modes_and_three_slots()
    {
        Assert.HasCount(18, PresetKey.All);
        Assert.AreEqual(18, PresetKey.All.Distinct().Count());
        Assert.IsTrue(PresetKey.All.Contains(new PresetKey(ControlModeId.Office, 1)));
        Assert.IsTrue(PresetKey.All.Contains(new PresetKey(ControlModeId.Custom3, 3)));
        var requests = new Dictionary<string, SettingRequest>
        { ["keep"] = SettingRequest.Keep, ["official"] = SettingRequest.Official, ["setZero"] = SettingRequest.Set(0) };
        var envelope = DefaultControlPresets.Create(ControlPageId.Gpu, PresetKey.All[0]) with { Settings = requests };
        var json = JsonSerializer.Serialize(envelope, Jiaolong.Contracts.Protocol.ProtocolJsonContext.Default.PagePresetEnvelope);
        var roundTrip = JsonSerializer.Deserialize(json, Jiaolong.Contracts.Protocol.ProtocolJsonContext.Default.PagePresetEnvelope)!;
        Assert.AreEqual(SettingIntent.Keep, roundTrip.Settings["keep"].Intent);
        Assert.AreEqual(SettingIntent.Official, roundTrip.Settings["official"].Intent);
        Assert.AreEqual(0, roundTrip.Settings["setZero"].Value!.Value.GetInt32());
        Assert.IsFalse(PresetValidation.Validate(envelope with { Payload = default }).IsValid);
        Assert.IsFalse(PresetValidation.Validate(envelope with { Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Set(-1) } }).IsValid);
        var cpu = DefaultControlPresets.Create(ControlPageId.Performance, PresetKey.All[0]);
        foreach (var (field, number) in new[] { ("temperatureLimitC", 75.5), ("splWatts", 55.5), ("spptWatts", 65.5),
            ("acMaxFrequencyMhz", 3200.5), ("dcMaxFrequencyMhz", 2400.5), ("acMinActiveCoresPercent", 50.5), ("dcMinActiveCoresPercent", 50.5) })
        {
            var payload = System.Text.Json.Nodes.JsonNode.Parse(cpu.Payload.GetRawText())!.AsObject();
            payload[field] = number;
            Assert.IsFalse(PresetValidation.Validate(cpu with { Payload = JsonSerializer.SerializeToElement(payload) }).IsValid, field);
            Assert.IsFalse(PresetValidation.Validate(cpu with { Settings = new() { [field] = SettingRequest.Set(number) } }).IsValid, field + " intent");
        }
        Assert.IsFalse(PresetValidation.Validate(cpu with
            { Settings = new(cpu.Settings) { ["acMaxFrequencyMhz"] = SettingRequest.Set(3200) } }).IsValid, "Contradictory Set value");
        Assert.IsTrue(PresetValidation.Validate(cpu with
            { Settings = new(cpu.Settings) { ["acMaxFrequencyMhz"] = SettingRequest.Set(4500) } }).IsValid);
        using var decimalInteger = JsonDocument.Parse("{\"temperatureLimitC\":75.0}");
        Assert.IsFalse(PresetValidation.ValidatePayload(ControlPageId.Performance,
            decimalInteger.RootElement).IsValid, "Integer-shaped JSON must deserialize as int");
        Assert.IsTrue(PresetValidation.ValidatePayload(ControlPageId.Performance,
            JsonSerializer.SerializeToElement(new { advancedCpuTuning = new { stapmWatts = 55.5 } })).IsValid);
        Assert.IsTrue(PresetValidation.ValidatePayload(ControlPageId.Lighting,
            JsonSerializer.SerializeToElement(new { speed = .75 })).IsValid);
        var factory = DefaultControlPresets.Create(ControlPageId.Gpu, PresetKey.All[0]);
        foreach (var failure in new Exception[]
        {
            new InvalidOperationException("预设目录版本冲突，当前请求尚未下发。"),
            new Exception("wrapper", new IOException("磁盘空间不足"))
        })
        {
            var text = PresetCatalogController.DescribeApplyFailure(failure);
            StringAssert.Contains(text, failure.GetBaseException().Message);
            Assert.AreNotEqual("预设应用未完成，请重试", text);
        }
        foreach (var cause in new[] { "presetRevisionRegressed", "presetRevisionContentConflict" })
        {
            var error = Jiaolong.Contracts.Errors.ServiceError.Create(Jiaolong.Contracts.Errors.ErrorCode.IdempotencyConflict, Guid.NewGuid(), false)
                with { Details = new() { ["stage"] = "CatalogPublish", ["cause"] = cause } };
            var text = PresetCatalogController.DescribeApplyFailure(new InvalidOperationException(PresetCatalogController.DescribePublicationFailure(error)));
            StringAssert.Contains(text, "预设存档与服务当前版本冲突");
            StringAssert.Contains(text, "本次未下发硬件请求");
            StringAssert.Contains(text, "刷新预设");
        }
        var unchanged = PresetCatalogController.MergeDraft(factory, "rename", factory.Payload);
        Assert.AreEqual(SettingIntent.Official, unchanged.Settings["coreOffsetKhz"].Intent);
        Assert.AreEqual(SettingIntent.Keep, unchanged.Settings["muxMode"].Intent);
        var cpuFactory = DefaultControlPresets.Create(ControlPageId.Performance, PresetKey.All[0]);
        var cpuEdited = PresetCatalogController.MergeDraft(cpuFactory, "one field", JsonSerializer.SerializeToElement(new
            { temperatureLimitC = 80, useOfficialCpuPolicy = false }));
        Assert.AreEqual(SettingIntent.Set, cpuEdited.Settings["temperatureLimitC"].Intent);
        Assert.AreEqual(SettingIntent.Official, cpuEdited.Settings["splWatts"].Intent, "Changing one numeric field must retain the other Official placeholders");
        Assert.AreEqual(SettingIntent.Official, cpuEdited.Settings["spptWatts"].Intent);
        var partial = PresetCatalogController.MergeDraft(factory, "partial", JsonSerializer.SerializeToElement(new { CoreOffsetKhz = 15000 }));
        Assert.AreEqual(SettingIntent.Set, partial.Settings["coreOffsetKhz"].Intent);
        Assert.AreEqual(15000, partial.Settings["coreOffsetKhz"].Value!.Value.GetInt32());
        Assert.AreEqual(SettingIntent.Official, partial.Settings["memoryOffsetKhz"].Intent);
        Assert.AreEqual(127, partial.Payload.GetProperty("VfOffsetsKhz").GetArrayLength());
        Assert.IsTrue(PresetValidation.Validate(partial).IsValid);
        var zero = PresetCatalogController.MergeDraft(partial, "zero", JsonSerializer.SerializeToElement(new { CoreOffsetKhz = 0 }));
        Assert.AreEqual(SettingIntent.Set, zero.Settings["coreOffsetKhz"].Intent);
        Assert.AreEqual(0, zero.Settings["coreOffsetKhz"].Value!.Value.GetInt32());
        var metadata = factory with { Settings = new(factory.Settings) { ["futureField"] = SettingRequest.Keep } };
        Assert.AreEqual(SettingIntent.Keep, PresetCatalogController.MergeDraft(metadata, "metadata", factory.Payload).Settings["futureField"].Intent);
        Assert.ThrowsExactly<InvalidOperationException>(() => PresetCatalogController.MergeDraft(factory with { SchemaVersion = 99 }, "future", factory.Payload));
    }

    [TestMethod]
    public void Preset_key_rejects_slots_outside_one_to_three()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetKey.Create(ControlModeId.Office, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetKey.Create(ControlModeId.Office, 4));
    }

    [TestMethod]
    public async Task Save_then_load_round_trips_one_page_preset_without_hardware_application()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Office, 2);
        using var document = JsonDocument.Parse("{\"temperatureLimitC\":76}");
        var expected = new PagePresetEnvelope(
            1,
            ControlPageId.Performance,
            key,
            "办公 · 预设 2",
            document.RootElement.Clone(),
            DateTimeOffset.UtcNow);

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(expected.DisplayName, loaded.DisplayName);
        Assert.AreEqual(expected.Payload.GetProperty("temperatureLimitC").GetInt32(), loaded.Payload.GetProperty("temperatureLimitC").GetInt32());
        Assert.AreEqual(76, SavedPerformancePreset.ReadDraft(loaded, key).TemperatureLimitC);
        var storagePath = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "control-presets-performance.json");
        var rawBefore = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(storagePath))!;
        rawBefore[$"{key.Mode}:{key.Slot}"]!["extraUserMetadata"] = "retain";
        await File.WriteAllTextAsync(storagePath, rawBefore.ToJsonString());
        Assert.IsTrue(await store.TryAdvanceRevisionAsync(loaded, 10, CancellationToken.None));
        var rawAfter = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(storagePath))!;
        rawAfter[$"{key.Mode}:{key.Slot}"]!["revision"] = rawBefore[$"{key.Mode}:{key.Slot}"]!["revision"]!.DeepClone();
        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(rawBefore, rawAfter), "Revision synchronization preserves every saved field.");
        var synchronized = (await store.LoadAsync(loaded.Page, key, CancellationToken.None))!;
        Assert.AreEqual(10L, synchronized.Revision);
        Assert.IsTrue(JsonElement.DeepEquals(loaded.Payload, synchronized.Payload));
        Assert.AreEqual(loaded.SavedAtUtc, synchronized.SavedAtUtc);
        Assert.AreEqual(loaded.DisplayName, synchronized.DisplayName);
        Assert.IsFalse(await store.TryAdvanceRevisionAsync(loaded, 20, CancellationToken.None), "A concurrent edit/revision must not be overwritten.");
        Assert.IsFalse(await store.TryAdvanceRevisionAsync(synchronized, 9, CancellationToken.None));
        Assert.IsFalse(await store.TryAdvanceRevisionAsync(synchronized, long.MaxValue, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(synchronized with { Revision = long.MaxValue }, CancellationToken.None));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => store.TryAdvanceRevisionAsync(synchronized, 11, cancelled.Token));
        }
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var draft = new Jiaolong_ControlCenter.ViewModels.PerformanceDraft
        {
            AcMaxFrequencyMhz = 4200, DcMaxFrequencyMhz = 3500, NegativeCurveOptimizer = null
        };
        var complete = expected with { Payload = JsonSerializer.SerializeToElement(draft, options) };
        var decoded = SavedPerformancePreset.ReadDraft(complete, key);
        Assert.AreEqual(4200, decoded.AcMaxFrequencyMhz);
        Assert.AreEqual(3500, decoded.DcMaxFrequencyMhz);
        Assert.IsNull(decoded.NegativeCurveOptimizer);
        Assert.IsNull(decoded.AdvancedCpuTuning);
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(null, key));
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(complete, PresetKey.Create(ControlModeId.Custom3, 2)));
        var incomplete = System.Text.Json.Nodes.JsonNode.Parse(complete.Payload.GetRawText())!.AsObject();
        incomplete.Remove("negativeCurveOptimizer");
        Assert.ThrowsExactly<InvalidOperationException>(() => SavedPerformancePreset.ReadDraft(
            complete with { Payload = JsonSerializer.SerializeToElement(incomplete) }, key));
        Assert.HasCount(2, paths.WrittenPaths);
        Assert.IsFalse(paths.WrittenPaths.Any(path => path.Contains("hardware", StringComparison.OrdinalIgnoreCase)));
        var draftPaths = new RecordingPathProvider();
        var draftStore = new ControlPresetStore(draftPaths);
        var preferenceStore = new UserPreferencesStore(draftPaths);
        preferenceStore.Update(saved => saved with { PerformanceFollowPreset = false,
            GpuPresetSlots = new() { ["Office"] = 3 }, FanPresetSlots = new() { ["Office"] = 1 } });
        await using var session = new HomeControlSession(new ControlCenterClient("unused-preset-test-" + Guid.NewGuid().ToString("N")),
            automaticRestore: new AppliedConfigurationRestore(draftPaths));
        var catalog = new PresetCatalogController(session, draftStore, preferenceStore);
        int completedSaves = 0;
        catalog.Saved += (_, _) => completedSaves++;
        var gpu = DefaultControlPresets.Create(ControlPageId.Gpu, key);
        catalog.ScheduleSave(ControlPageId.Gpu, key, "first", gpu.Payload);
        catalog.ScheduleSave(ControlPageId.Gpu, key, "latest", JsonSerializer.SerializeToElement(new { CoreOffsetKhz = 10000 }));
        await Task.Delay(80);
        Assert.AreEqual(0, completedSaves, "Edits wait for the debounce or flush");
        await catalog.FlushAsync();
        Assert.AreEqual(1, completedSaves, "Superseded draft does not write");
        var autosaved = await draftStore.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None);
        Assert.AreEqual(gpu.DisplayName, autosaved!.DisplayName, "Draft labels must not rename the stored preset");
        Assert.AreEqual(10000, autosaved.Settings["coreOffsetKhz"].Value!.Value.GetInt32());
        Assert.AreEqual(SettingIntent.Official, autosaved.Settings["memoryOffsetKhz"].Intent);
        var frozen = await catalog.ReadAutomaticServicePresetsAsync([PresetKey.Create(ControlModeId.Office, 1)], CancellationToken.None);
        Assert.HasCount(4, frozen[0].Pages);
        Assert.HasCount(4, frozen[0].Selections);
        Assert.IsFalse(frozen[0].Selections.Single(p => p.Page == ControlPageId.Performance).Follow);
        Assert.AreEqual(1, frozen[0].Selections.Single(p => p.Page == ControlPageId.Performance).Key.Slot);
        Assert.AreEqual(3, frozen[0].Selections.Single(p => p.Page == ControlPageId.Gpu).Key.Slot);
        Assert.AreEqual(1, frozen[0].Selections.Single(p => p.Page == ControlPageId.Fan).Key.Slot);
        Assert.AreEqual(3, preferenceStore.Load().GpuPresetSlots["Office"]);
        Assert.AreEqual(PerformanceMode.Quiet, catalog.NativeModeFor(PresetKey.Create(ControlModeId.Custom1, 2)));
        Assert.AreEqual(PerformanceMode.Balanced, catalog.NativeModeFor(PresetKey.Create(ControlModeId.Custom2, 2)));
        Assert.AreEqual(PerformanceMode.Turbo, catalog.NativeModeFor(PresetKey.Create(ControlModeId.Custom3, 2)));
    }

    [TestMethod]
    public async Task Failed_autosave_keeps_latest_draft_until_flush_can_retry()
    {
        var paths = new RecordingPathProvider();
        var directory = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center");
        var blocker = Path.Combine(directory, "control-presets-gpu.json");
        Directory.CreateDirectory(blocker);
        try
        {
            await using var session = new HomeControlSession(
                new ControlCenterClient("unused-preset-retry-" + Guid.NewGuid().ToString("N")),
                automaticRestore: new AppliedConfigurationRestore(paths));
            var store = new ControlPresetStore(paths);
            var catalog = new PresetCatalogController(session, store, new UserPreferencesStore(paths));
            var key = PresetKey.Create(ControlModeId.Office, 2);
            catalog.ScheduleSave(ControlPageId.Gpu, key, "first", JsonSerializer.SerializeToElement(new { CoreOffsetKhz = 5000 }));
            catalog.ScheduleSave(ControlPageId.Gpu, key, "latest", JsonSerializer.SerializeToElement(new { CoreOffsetKhz = 10000 }));

            await Assert.ThrowsAsync<IOException>(() => catalog.FlushAsync());
            Assert.IsFalse(File.Exists(blocker));
            Directory.Delete(blocker);
            await catalog.FlushAsync();

            var saved = await store.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None);
            Assert.IsNotNull(saved);
            Assert.AreEqual(10000, saved.Settings["coreOffsetKhz"].Value!.Value.GetInt32());
        }
        finally { Directory.Delete(paths.LocalAppDataRoot, recursive: true); }
    }

    [TestMethod]
    public async Task Superseded_follow_enable_does_not_publish_or_apply()
    {
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Update(current => current with { GpuFollowPreset = true });
        await using var session = new HomeControlSession(
            new ControlCenterClient("unused-stale-follow-" + Guid.NewGuid().ToString("N")),
            automaticRestore: new AppliedConfigurationRestore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        int failures = 0;
        int resyncs = 0;
        session.NotificationRequested += _ => failures++;
        catalog.FollowChanged += () => resyncs++;
        bool superseded = false;
        var key = PresetKey.Create(ControlModeId.Office, 2);
        catalog.ScheduleSave(ControlPageId.Gpu, key, "draft", DefaultControlPresets.Create(ControlPageId.Gpu, key).Payload,
            () => { preferences.Update(current => current with { GpuFollowPreset = false }); superseded = true; });

        await catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, enabled: true);

        Assert.IsTrue(superseded, "The newer toggle must arrive while the older request flushes.");
        Assert.AreEqual(0, failures, "An obsolete toggle must not attempt a service request.");
        Assert.AreEqual(0, resyncs, "An obsolete toggle must not reconfigure automation.");
        Assert.IsFalse(preferences.Load().GpuFollowPreset);
    }

    [TestMethod]
    public async Task Failed_follow_publication_still_requests_automation_resync()
    {
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Update(current => current with { GpuFollowPreset = false });
        await using var session = new HomeControlSession(
            new ControlCenterClient("unused-follow-resync-" + Guid.NewGuid().ToString("N")),
            automaticRestore: new AppliedConfigurationRestore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        int resyncs = 0, failures = 0;
        catalog.FollowChanged += () => resyncs++;
        session.NotificationRequested += _ => failures++;
        preferences.Update(current => current with { GpuFollowPreset = true });

        await catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, enabled: true, token: new CancellationToken(true));

        Assert.AreEqual(1, resyncs, "A persisted follow request must remain pending for service reconnect.");
        Assert.AreEqual(1, failures, "The current request must not appear confirmed while publication failed.");
        Assert.IsTrue(preferences.Load().GpuFollowPreset);
    }

    [TestMethod]
    public async Task Corrupt_preset_json_is_quarantined_and_returns_factory_default()
    {
        var paths = new RecordingPathProvider();
        var directory = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "control-presets-performance.json");
        await File.WriteAllTextAsync(path, "{not-json");

        var loaded = await new ControlPresetStore(paths).LoadAsync(
            ControlPageId.Performance,
            PresetKey.Create(ControlModeId.Office, 1),
            CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.IsNotNull(SavedPerformancePreset.ReadDraft(loaded, loaded.Key));
        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(1, Directory.GetFiles(directory, "control-presets-performance.json.corrupt-*"));
    }

    [TestMethod]
    public async Task Saving_a_second_version_replaces_the_file_without_leaving_temp_files()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Gaming, 1);

        await store.SaveAsync(CreatePreset(key, "第一版", 75), CancellationToken.None);
        await store.SaveAsync(CreatePreset(key, "第二版", 84), CancellationToken.None);
        var loaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
        var directory = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("第二版", loaded.DisplayName);
        Assert.HasCount(1, Directory.GetFiles(directory, "control-presets-performance.json"));
        Assert.HasCount(0, Directory.GetFiles(directory, "control-presets-performance.json.tmp-*"));
    }

    private static PagePresetEnvelope CreatePreset(PresetKey key, string name, int value)
    {
        using var document = JsonDocument.Parse($"{{\"temperatureLimitC\":{value}}}");
        return new PagePresetEnvelope(1, ControlPageId.Performance, key, name, document.RootElement.Clone(), DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task G1_G2_preserve_entries_null_future_schema_and_serializable_saves()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var keys = PresetKey.All.ToArray();
        await Task.WhenAll(keys.Select((key, index) => new ControlPresetStore(paths).SaveAsync(
            DefaultControlPresets.Create(ControlPageId.Gpu, key) with { DisplayName = $"用户{index}" }, CancellationToken.None)));
        foreach (var (key, index) in keys.Select((key, index) => (key, index)))
            Assert.AreEqual($"用户{index}", (await store.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None))!.DisplayName);
        var path = paths.WrittenPaths[0];
        Assert.IsTrue(File.Exists(path + ".bak"));
        var unreadPaths = new RecordingPathProvider();
        var unreadStore = new ControlPresetStore(unreadPaths);
        await unreadStore.SaveAsync(DefaultControlPresets.Create(ControlPageId.Gpu, keys[0]), CancellationToken.None);
        var unreadPath = unreadPaths.WrittenPaths[0];
        var unreadRoot = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(unreadPath))!.AsObject();
        unreadRoot["Office:1"]!["payload"] = System.Text.Json.Nodes.JsonNode.Parse("{\"CoreFrequencyLimitMhz\":\"broken-original\"}");
        await File.WriteAllTextAsync(unreadPath, unreadRoot.ToJsonString());
        var unreadOriginal = await File.ReadAllBytesAsync(unreadPath);
        await unreadStore.SaveAsync(DefaultControlPresets.Create(ControlPageId.Gpu, keys[0]), CancellationToken.None);
        await unreadStore.SaveAsync(DefaultControlPresets.Create(ControlPageId.Gpu, keys[1]), CancellationToken.None);
        var unreadBackup = Directory.GetFiles(Path.GetDirectoryName(unreadPath)!, "*.corrupt-*");
        Assert.HasCount(1, unreadBackup, "Repair without loading must preserve corrupt original");
        CollectionAssert.AreEqual(unreadOriginal, await File.ReadAllBytesAsync(unreadBackup[0]));
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["Office:1"] = System.Text.Json.Nodes.JsonNode.Parse("{\"schemaVersion\":1,\"page\":\"gpu\",\"key\":{\"mode\":\"office\",\"slot\":1},\"displayName\":\"坏项\",\"payload\":[]}");
        await File.WriteAllTextAsync(path, root.ToJsonString());
        Assert.AreEqual("用户1", (await store.LoadAsync(ControlPageId.Gpu, keys[1], CancellationToken.None))!.DisplayName);
        Assert.IsNotNull(await store.LoadAsync(ControlPageId.Gpu, keys[0], CancellationToken.None));
        Assert.IsTrue(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*").Length > 0);
        var explicitNull = DefaultControlPresets.Complete(new(1, ControlPageId.Gpu, keys[0], "空值",
            JsonSerializer.SerializeToElement(new { MemoryOffsetKhz = (int?)null, CoreOffsetKhz = 0 }), DateTimeOffset.UtcNow));
        Assert.AreEqual(1L, explicitNull.Revision, "Migrated user presets must have a publishable initial revision");
        foreach (var page in Enum.GetValues<ControlPageId>())
        {
            var factory = DefaultControlPresets.Create(page, keys[0]);
            var migrated = DefaultControlPresets.Complete(factory with
            { SchemaVersion = 1, FactoryRevision = "", UserModified = true, Revision = 0 });
            Assert.AreEqual(1L, migrated.Revision, $"Publishable migration: {page}");
            Assert.AreEqual(7L, DefaultControlPresets.Complete(migrated with { Revision = 7 }).Revision,
                "Published revisions must not be renumbered");
        }
        Assert.AreEqual(JsonValueKind.Null, explicitNull.Payload.GetProperty("MemoryOffsetKhz").ValueKind);
        int writes = paths.WrittenPaths.Count;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(
            DefaultControlPresets.Create(ControlPageId.Gpu, keys[0]) with { Payload = JsonSerializer.SerializeToElement(new { CoreFrequencyLimitMhz = -1 }) }, CancellationToken.None));
        Assert.HasCount(writes, paths.WrittenPaths);
        root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["Office:2"]!["schemaVersion"] = 99;
        await File.WriteAllTextAsync(path, root.ToJsonString());
        var bytes = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(
            DefaultControlPresets.Create(ControlPageId.Gpu, keys[2]), CancellationToken.None));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
    }
}
