using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using System.Reflection;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Invalid_scheduling_baseline_keeps_refresh_connected_without_recapture_or_factory_apply(bool unsupportedSchema)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var paths = new ManagedSessionTestPaths();
        var snapshot = CreateState(PerformanceMode.Turbo);
        snapshot = snapshot with { Controls = snapshot.Controls with
        {
            CpuTuning = new(99, 54, 108, 0, true, 8,
                Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), -30, "hardwareReadback")
            { AcMaxFrequencyMhz = 4700, DcMaxFrequencyMhz = 4500 }
        } };
        var baseline = await new DeviceSchedulingBaselineStore(paths).LoadOrCaptureAsync(snapshot, deadline.Token);
        Assert.IsNotNull(baseline);
        string path = Directory.GetFiles(Path.Combine(paths.LocalAppDataRoot,
            "Jiaolong Control Center", "device-baselines"), "*.json").Single();
        string invalid = unsupportedSchema ? JsonSerializer.Serialize(baseline with { SchemaVersion = 99 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) : "broken baseline";
        await File.WriteAllTextAsync(path, invalid, deadline.Token);
        byte[] original = await File.ReadAllBytesAsync(path, deadline.Token);

        string name = "Xisura-invalid-baseline-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(name, snapshot);
        var preferences = new UserPreferencesStore(paths);
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
        typeof(HomeControlSession).GetField("preferences", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, preferences);
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        session.RestoreWarning += warnings.Enqueue;
        await session.StartAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.Connected, session.Status);
        Assert.IsNull(session.SchedulingBaseline);
        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings.Single(), "原文件已保留");

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state =>
        {
            if (state.Controls.PerformanceMode == PerformanceMode.Quiet) refreshed.TrySetResult();
        };
        server.State = snapshot with { Controls = snapshot.Controls with { PerformanceMode = PerformanceMode.Quiet } };
        await refreshed.Task.WaitAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.Connected, session.Status);
        Assert.IsNull(session.SchedulingBaseline);
        Assert.HasCount(1, warnings);

        string? refusal = null;
        session.NotificationRequested += message => refusal = message;
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        Assert.IsNull(await catalog.ApplyAsync(ControlModeId.Gaming, PerformanceMode.Balanced, ControlOrigin.PresetUse,
            ControlPageId.Performance, PresetKey.Create(ControlModeId.Gaming, 2), deadline.Token));
        StringAssert.Contains(refusal!, "尚未读取完整的本机均衡基准");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path, deadline.Token));
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(path)!, "*"));
        Assert.HasCount(0, server.Commands);
    }

    [TestMethod]
    public async Task Partial_baseline_is_captured_on_readback_without_startup_commands_or_placeholder_targets()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var paths = new ManagedSessionTestPaths();
        var snapshot = CreateState(PerformanceMode.Turbo);
        snapshot = snapshot with { Controls = snapshot.Controls with
        {
            CpuTuning = new(null, null, null, null, true, 8,
                Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), -30, "hardwareReadback")
            { AcMaxFrequencyMhz = 4700, DcMaxFrequencyMhz = 4500, AcBoostMode = 2, DcBoostMode = 2 }
        } };
        string name = "Xisura-partial-startup-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(name, snapshot);
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
        typeof(HomeControlSession).GetField("preferences", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, new UserPreferencesStore(paths));
        await session.StartAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.Connected, session.Status);
        Assert.IsNotNull(session.SchedulingBaseline);
        Assert.AreEqual(2, session.SchedulingBaseline.SchemaVersion);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var store = new ControlPresetStore(paths, () => session.State?.Capabilities, () => session.SchedulingBaseline);
        var preset = await store.LoadAsync(ControlPageId.Performance, key, deadline.Token);
        Assert.IsNotNull(preset);
        foreach (var field in new[] { "temperatureLimitC", "splWatts", "spptWatts" })
            Assert.AreEqual(SettingIntent.Keep, preset.Settings[field].Intent);
        Assert.HasCount(0, server.Commands);
    }
}
