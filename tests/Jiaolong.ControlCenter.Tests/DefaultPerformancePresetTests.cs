using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class DefaultPerformancePresetTests
{
    [TestMethod]
    public async Task Reset_restores_only_requested_slot_and_preserves_saved_name()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Custom2, 2);
        var other = PresetKey.Create(ControlModeId.Custom2, 1);
        var original = DefaultPerformancePresets.Create(key);
        var otherPreset = DefaultPerformancePresets.Create(other) with { DisplayName = "保留" };
        await store.SaveAsync(original with { SchemaVersion = 1, DisplayName = "我的安静", Payload = JsonSerializer.SerializeToElement(new { temperatureLimitC = 99 }) }, CancellationToken.None);
        await store.SaveAsync(otherPreset, CancellationToken.None);
        var reset = await store.ResetPerformanceAsync(key, CancellationToken.None);
        Assert.AreEqual("我的安静", reset.DisplayName);
        Assert.AreEqual(original.Payload.GetRawText(), reset.Payload.GetRawText());
        var reloaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
        Assert.IsTrue(JsonElement.DeepEquals(reset.Payload, reloaded!.Payload));
        var untouched = await store.LoadAsync(ControlPageId.Performance, other, CancellationToken.None);
        Assert.AreEqual(otherPreset.DisplayName, untouched!.DisplayName);
        Assert.IsTrue(JsonElement.DeepEquals(otherPreset.Payload, untouched.Payload));
        Assert.AreEqual(otherPreset.SavedAtUtc, untouched.SavedAtUtc);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.ResetPerformanceAsync(key, new CancellationToken(true)));
    }

    [TestMethod]
    public async Task Missing_slots_have_eighteen_valid_distinct_identities_without_writes()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        foreach (var key in PresetKey.All)
        {
            var preset = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
            Assert.IsNotNull(preset, key.ToString());
            Assert.AreEqual(key, preset.Key);
            var draft = SavedPerformancePreset.ReadDraft(preset, key);
            Assert.IsTrue(PerformanceDraftValidator.Validate(draft).IsValid);
            Assert.IsTrue(draft.SpptWatts >= draft.SplWatts);
            Assert.IsTrue(draft.SplWatts is >= 45 and <= 75 && draft.SpptWatts is >= 45 and <= 75);
            Assert.IsNull(draft.NegativeCurveOptimizer);
            Assert.IsNull(draft.AdvancedCpuTuning);
            Assert.AreEqual(Guid.Empty, draft.WindowsPowerSchemeId);
            Assert.AreEqual(SettingIntent.Official, preset.Settings["windowsPowerSchemeId"].Intent);
            Assert.AreEqual(SettingIntent.Keep, preset.Settings["acMaxFrequencyMhz"].Intent);
            Assert.AreEqual(SettingIntent.Keep, preset.Settings["negativeCurveOptimizer"].Intent);
            Assert.IsNull(preset.Settings["negativeCurveOptimizer"].Value);
        }
        foreach (var mode in Enum.GetValues<ControlModeId>())
        {
            var tiers = Enumerable.Range(1, 3).Select(slot => DefaultPerformancePresets.CreateDraft(PresetKey.Create(mode, slot))).ToArray();
            Assert.HasCount(3, tiers);
            Assert.IsTrue(tiers.All(tier => tier.AcMinActiveCoresPercent is null && tier.DcMinActiveCoresPercent is null));
        }
        var office = DefaultPerformancePresets.CreateDraft(PresetKey.Create(ControlModeId.Office, 2));
        var gaming = DefaultPerformancePresets.CreateDraft(PresetKey.Create(ControlModeId.Gaming, 2));
        var turbo = DefaultPerformancePresets.CreateDraft(PresetKey.Create(ControlModeId.Turbo, 2));
        Assert.IsTrue(office.UseOfficialCpuPolicy && gaming.UseOfficialCpuPolicy && turbo.UseOfficialCpuPolicy);
        var verified = new CapabilitySnapshot([]) { Identity = new("MRID6-23", "V38", "AMD Ryzen 7 7745HX", "NVIDIA RTX 4070"),
            Fields = [new("acMaxFrequencyMhz", CapabilityState.Available, true, true, 1500, 5400, 1, "MHz", "verifiedProfile", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1)] };
        var resolved = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), verified);
        Assert.AreEqual(SettingIntent.Keep, resolved.Settings["acMaxFrequencyMhz"].Intent);
        Assert.IsNull(resolved.Settings["acMaxFrequencyMhz"].Value);
        var windowsFrequency = verified with { Fields = [verified.Fields[0], verified.Fields[0] with { Key = "dcMaxFrequencyMhz" }] };
        foreach (var (mode, balancedAc, balancedDc) in new[] { (ControlModeId.Office, 4200, 2800),
            (ControlModeId.Gaming, 4800, 3300), (ControlModeId.Turbo, 5100, 3600),
            (ControlModeId.Custom1, 4200, 2800), (ControlModeId.Custom2, 4800, 3300), (ControlModeId.Custom3, 5100, 3600) })
        {
            var balanced = DefaultPerformancePresets.Create(PresetKey.Create(mode, 2), windowsFrequency);
            foreach (var (field, expected) in new[] { ("acMaxFrequencyMhz", balancedAc), ("dcMaxFrequencyMhz", balancedDc) })
            {
                Assert.AreEqual(SettingIntent.Keep, balanced.Settings[field].Intent, $"{mode}:2 must preserve the current Windows frequency cap.");
                Assert.IsNull(balanced.Settings[field].Value);
                Assert.AreEqual(4500, balanced.Payload.GetProperty(field).GetInt32());
            }
            Assert.AreEqual(SettingIntent.Official, balanced.Settings["temperatureLimitC"].Intent);
            var migrated = DefaultControlPresets.Complete(balanced with { FactoryRevision = "2026-10-10.control-v3",
                Revision = 7, DisplayName = "保留名称", Settings = new(balanced.Settings) { ["acMaxFrequencyMhz"] = SettingRequest.Official } }, windowsFrequency, true);
            Assert.AreEqual(SettingIntent.Keep, migrated.Settings["acMaxFrequencyMhz"].Intent);
            Assert.AreEqual(7L, migrated.Revision);
            Assert.AreEqual("保留名称", migrated.DisplayName);
        }
        var unverifiedWindows = verified with { Fields = verified.Fields.Select(field => field with { VerifiedAtUtc = null }).ToArray() };
        foreach (var limits in new (double? Minimum, double? Maximum)[] { (null, null), (1500, null), (null, 5400), (1500, double.PositiveInfinity), (5400, 1500) })
        {
            var withoutRange = verified with { Fields = [verified.Fields[0] with { Minimum = limits.Minimum, Maximum = limits.Maximum }] };
            Assert.AreEqual(SettingIntent.Keep, DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Gaming, 2), withoutRange).Settings["acMaxFrequencyMhz"].Intent,
                "Frequency defaults require a complete finite ordered capability range.");
        }
        Assert.AreEqual(SettingIntent.Keep, DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), unverifiedWindows).Settings["acMaxFrequencyMhz"].Intent,
            "Writable alone must not turn an unverified factory value into a required command");
        var officeBalanced = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 2), verified with
        { Fields = [new("isBoostEnabled", CapabilityState.Available, true, true, null, null, null, "", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 1)] });
        Assert.AreEqual(SettingIntent.Set, officeBalanced.Settings["isBoostEnabled"].Intent);
        Assert.IsFalse(officeBalanced.Settings["isBoostEnabled"].Value!.Value.GetBoolean());
        Assert.IsTrue(PresetValidation.Validate(officeBalanced).IsValid);
        var unreadableWindows = verified with { Fields = verified.Fields.Select(field => field with { Readable = false }).ToArray() };
        Assert.AreEqual(SettingIntent.Keep, DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), unreadableWindows).Settings["acMaxFrequencyMhz"].Intent);
        var mixedEpoch = verified with { Fields = [verified.Fields[0], verified.Fields[0] with { Key = "dcMaxFrequencyMhz", HardwareEpoch = 2 }] };
        Assert.AreEqual(SettingIntent.Keep, DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), mixedEpoch).Settings["acMaxFrequencyMhz"].Intent);
        var unknown = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), verified with { Identity = verified.Identity with { CpuModel = "Ryzen 9 7940HX" } });
        Assert.AreEqual(SettingIntent.Keep, unknown.Settings["acMaxFrequencyMhz"].Intent);
        var testedAt = DateTimeOffset.UtcNow;
        var oem = verified with { IsOnAcPower = true, Fields = verified.Fields.Concat(new FieldCapability[]
        {
            new("temperatureLimitC", CapabilityState.Available, true, true, 45, 100, 1, "°C", "verifiedProfile", "PreservesSelectedMode", testedAt, 1),
            new("splWatts", CapabilityState.Available, true, true, 45, 75, 1, "W", "verifiedProfile", "PreservesSelectedMode", testedAt, 1),
            new("spptWatts", CapabilityState.Available, true, true, 45, 75, 1, "W", "verifiedProfile", "PreservesSelectedMode", testedAt, 1),
            new("isBoostEnabled", CapabilityState.Available, true, true, null, null, null, "", "windows", "PreservesSelectedMode", testedAt, 1)
        }).ToArray() };
        var quiet = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), oem);
        Assert.AreEqual(SettingIntent.Set, quiet.Settings["splWatts"].Intent);
        Assert.AreEqual(45, quiet.Settings["splWatts"].Value!.Value.GetInt32());
        var dc = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), oem with { IsOnAcPower = false });
        Assert.AreEqual(SettingIntent.Official, dc.Settings["splWatts"].Intent);
        var changesMode = oem with { Fields = oem.Fields.Select(field => field with { NativeModeSideEffect = "ChangesToTurbo" }).ToArray() };
        Assert.AreEqual(SettingIntent.Official, DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), changesMode).Settings["splWatts"].Intent);
        var narrow = verified with { Fields = verified.Fields.Select(field => field with { Maximum = 2500 }).ToArray() };
        Assert.AreEqual(SettingIntent.Keep, DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1), narrow).Settings["acMaxFrequencyMhz"].Intent);
        var oldFactoryPaths = new RecordingPathProvider();
        var oldFactoryStore = new ControlPresetStore(oldFactoryPaths);
        await oldFactoryStore.SaveAsync(DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Office, 1)), CancellationToken.None);
        var capableStore = new ControlPresetStore(oldFactoryPaths, () => verified);
        var loadedFactory = await capableStore.LoadAsync(ControlPageId.Performance, PresetKey.Create(ControlModeId.Office, 1), CancellationToken.None);
        Assert.AreEqual(SettingIntent.Keep, loadedFactory!.Settings["acMaxFrequencyMhz"].Intent,
            "Saved factory slots must resolve with the same live capabilities as reset and execution");
        await capableStore.SaveAsync(loadedFactory with
        { Settings = new(loadedFactory.Settings) { ["acMaxFrequencyMhz"] = SettingRequest.Set(4000) },
            Payload = JsonSerializer.SerializeToElement(SavedPerformancePreset.ReadDraft(loadedFactory, loadedFactory.Key)
                with { AcMaxFrequencyMhz = 4000 }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }, CancellationToken.None);
        var userIntent = await capableStore.LoadAsync(ControlPageId.Performance, loadedFactory.Key, CancellationToken.None);
        Assert.IsTrue(userIntent!.UserModified, "Changing an intent alone must stop factory re-resolution");
        Assert.AreEqual(SettingIntent.Set, userIntent.Settings["acMaxFrequencyMhz"].Intent);
        Assert.AreEqual(4000, userIntent.Settings["acMaxFrequencyMhz"].Value!.Value.GetInt32());
        var unavailableReason = typeof(Jiaolong_ControlCenter.Prototype.PrototypeWindow).GetMethod("PresetUnavailableReason",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var withoutCo = new CapabilitySnapshot([new("cpuTuning", CapabilityState.Available, null)])
        { Fields = oem.Fields, Identity = oem.Identity, IsOnAcPower = true };
        var baseline = new CpuTuningState(85, 45, 65, 4500, true, 8,
            new("381b4222-f694-41f0-9685-ff5bb260df2e"), null, null)
        { AcMaxFrequencyMhz = 4500, DcMaxFrequencyMhz = 3300, AcBoostMode = 2, DcBoostMode = 2 };
        var nativeDefaults = DefaultPerformancePresets.Create(PresetKey.Create(ControlModeId.Gaming, 2), withoutCo);
        var snapshot = new HomeStateSnapshot(withoutCo, null, new(PerformanceMode.Balanced, false, []) { CpuTuning = baseline });
        Assert.IsNull(unavailableReason.Invoke(null, [SavedPerformancePreset.ReadDraft(nativeDefaults, nativeDefaults.Key), snapshot]),
            "Optional unverified CO must not disable a tray preset whose Windows fields can execute");
        var noBoost = snapshot with { Capabilities = withoutCo with { Fields = withoutCo.Fields.Where(field => field.Key != "isBoostEnabled").ToArray() } };
        Assert.IsNotNull(unavailableReason.Invoke(null, [SavedPerformancePreset.ReadDraft(nativeDefaults, nativeDefaults.Key), noBoost]),
            "A required saved Windows intent must still reject missing capability");
        Assert.HasCount(0, paths.WrittenPaths);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.LoadAsync(ControlPageId.Performance, PresetKey.All[0], canceled.Token));
        Assert.IsNotNull(await store.LoadAsync(ControlPageId.Fan, PresetKey.All[0], CancellationToken.None));
    }

    [TestMethod]
    public async Task Saved_slot_wins_and_default_siblings_do_not_rewrite_it()
    {
        var paths = new RecordingPathProvider();
        var store = new ControlPresetStore(paths);
        var key = PresetKey.Create(ControlModeId.Custom2, 2);
        var saved = new PagePresetEnvelope(1, ControlPageId.Performance, key, "我的配置",
            JsonSerializer.SerializeToElement(new { temperatureLimitC = 81 }), DateTimeOffset.UtcNow);
        await store.SaveAsync(saved, CancellationToken.None);
        var path = paths.WrittenPaths.Single();
        var bytes = await File.ReadAllBytesAsync(path);
        foreach (var candidate in PresetKey.All)
            Assert.IsNotNull(await store.LoadAsync(ControlPageId.Performance, candidate, CancellationToken.None));
        var loaded = await store.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
        Assert.AreEqual("我的配置", loaded!.DisplayName);
        Assert.AreEqual(81, loaded.Payload.GetProperty("temperatureLimitC").GetInt32());
        Assert.AreEqual(81, SavedPerformancePreset.ReadDraft(loaded, key).TemperatureLimitC);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.HasCount(1, paths.WrittenPaths);
    }
}
