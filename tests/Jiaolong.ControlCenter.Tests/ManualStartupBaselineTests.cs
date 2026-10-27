using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ManualStartupBaselineTests
{
    [TestMethod]
    public void Fresh_preferences_leave_all_preset_following_off()
    {
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths).Load();
        Assert.IsFalse(preferences.PerformanceFollowPreset);
        Assert.IsFalse(preferences.GpuFollowPreset);
        Assert.IsFalse(preferences.FanFollowPreset);
        Assert.IsFalse(preferences.LightingFollowPreset);
        Assert.IsFalse(preferences.AdaptiveModeEnabled);
        Assert.HasCount(0, paths.WrittenPaths);
    }

    [TestMethod]
    public async Task Startup_mode_restore_requires_explicit_performance_following()
    {
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Save(preferences.Load() with { PerformanceFollowPreset = false });
        await using var session = new HomeControlSession(new ControlCenterClient("baseline-policy-" + Guid.NewGuid().ToString("N")),
            radioController: new NoRadioAccess());
        typeof(HomeControlSession).GetField("preferences", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, preferences);
        typeof(HomeControlSession).GetProperty("State")!.SetValue(session, new HomeStateSnapshot(
            new CapabilitySnapshot([new("performanceMode", CapabilityState.Available, null)])
                { SupportState = DeviceSupportState.Ready }, null, new(PerformanceMode.Balanced, false, [])));
        var policy = typeof(HomeControlSession).GetMethod("IsRestoreAllowed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var command = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced);
        Assert.IsFalse((bool)policy.Invoke(session, [command])!);
        preferences.Save(preferences.Load() with { PerformanceFollowPreset = true });
        Assert.IsTrue((bool)policy.Invoke(session, [command])!);
        Assert.IsFalse((bool)policy.Invoke(session, [new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo)])!,
            "Opt-in must not bypass the AC safety condition");
        string? failure = null;
        session.NotificationRequested += message => failure = message;
        var store = new ControlPresetStore(paths);
        var catalog = new PresetCatalogController(session, store, preferences);
        Assert.IsNull(await catalog.ApplyAsync(ControlModeId.Gaming, PerformanceMode.Balanced, ControlOrigin.PresetUse,
            ControlPageId.Performance, PresetKey.Create(ControlModeId.Gaming, 2)));
        Assert.IsNotNull(failure);
        StringAssert.Contains(failure, "尚未读取完整的本机均衡基准");
        Assert.HasCount(0, preferences.Load().PerformancePresetSlots);
    }

    private sealed class NoRadioAccess : IInteractiveRadioController
    {
        public Task<HomeRadioSnapshot> ReadAsync(CancellationToken token) => throw new InvalidOperationException("No hardware access in policy tests");
        public Task<bool> SetAsync(QuickSettingKind setting, bool enabled, CancellationToken token) => throw new InvalidOperationException("No hardware access in policy tests");
    }
}
