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
            switch (page)
            {
                case ControlPageId.Performance:
                    Assert.IsTrue(PerformanceDraftValidator.Validate(SavedPerformancePreset.ReadDraft(preset, key)).IsValid);
                    break;
                case ControlPageId.Gpu:
                    Assert.IsTrue((bool)gpuValidate.Invoke(null, [JsonSerializer.Deserialize(preset.Payload.GetRawText(), gpuType)])!);
                    Assert.AreEqual(2400, preset.Payload.GetProperty("CoreFrequencyLimitMhz").GetInt32());
                    Assert.AreEqual(0, preset.Payload.GetProperty("CoreOffsetKhz").GetInt32());
                    Assert.AreEqual(0, preset.Payload.GetProperty("MemoryOffsetKhz").GetInt32());
                    Assert.HasCount(127, preset.Payload.GetProperty("VfOffsetsKhz").EnumerateArray().ToArray());
                    Assert.IsFalse(preset.Payload.TryGetProperty("MuxMode", out _));
                    break;
                case ControlPageId.Fan:
                    var fan = preset.Payload.Deserialize<FanCurveState>()!;
                    Assert.IsTrue(fan.IsValid());
                    Assert.HasCount(0, FanCurveSafety.Assess(fan));
                    break;
                case ControlPageId.Lighting:
                    Assert.IsTrue(preset.Payload.Deserialize<LightingDraft>()!.IsValid());
                    break;
            }
        }
        Assert.HasCount(0, paths.WrittenPaths);
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
                    Assert.AreEqual(0, preset.Payload.GetProperty("MemoryOffsetKhz").GetInt32());
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
            await store.SaveAsync(new(schema, ControlPageId.Gpu, key, "保留名称", document.RootElement.Clone(), DateTimeOffset.UtcNow), CancellationToken.None);
            var path = paths.WrittenPaths.Last();
            var before = await File.ReadAllTextAsync(path);
            var loaded = (await store.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None))!;
            Assert.AreEqual(1, loaded.SchemaVersion);
            Assert.AreEqual("保留名称", loaded.DisplayName);
            Assert.AreEqual(0, loaded.Payload.GetProperty("CoreOffsetKhz").GetInt32());
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
