using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    [Timeout(30000)]
    public async Task Thermal_worker_releases_fan_before_office_apply_and_end_restores_session()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var fixture = new Fixture();
        fixture.Provider.JournalEveryModeStep = true;
        fixture.Provider.CapabilitySupportState = DeviceSupportState.Ready;
        var originalMode = fixture.Provider.Mode;
        var originalFan = fixture.Provider.Fan;
        var originalStrongCooling = fixture.Provider.Controls.StrongCooling;

        var store = new AdaptiveAutomationStateStore();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
        typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store,
            new AtomicJsonStore(fixture.Root, "thermal-worker-test.json"));
        var bridge = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
        var runtime = new HomeServiceRuntime(bridge, journal: fixture.Journal, requireClientSessions: true);
        var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, token);
        Assert.AreEqual(CommandState.Applied, begun.State);
        var handle = begun.ClientSession!;
        Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(handle.SessionId, null), fixture.Owner, token)).State);
        var fanStarted = await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true)
            { ClientSessionId = handle.SessionId }, token, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        Assert.AreEqual(CommandState.Applied, fanStarted.State);
        Assert.IsTrue(fixture.Provider.Controls.StrongCooling);

        var office = PresetKey.Create(ControlModeId.Office, 2);
        var game = PresetKey.Create(ControlModeId.Gaming, 2);
        var turbo = PresetKey.Create(ControlModeId.Turbo, 2);
        var fanPage = FanPreset(office, strongCooling: true);
        var config = new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
            new(40, 35, 1, true, 85, 90, 1, 20, 15, 1, 20, 30, true, 85, 80, 1, 1, 1, true, 30, []),
            new(office, game, turbo, office, game),
            [new(office, PerformanceMode.Quiet, null)
                { Pages = [fanPage], Selections = [new(ControlPageId.Fan, true, office, 1)] },
             new(game, PerformanceMode.Balanced, null), new(turbo, PerformanceMode.Turbo, null)]);
        await store.SetConfigurationAsync(config, token, intent: AutomationConfigurationIntent.Enable, ownerSid: fixture.Owner.UserSid);

        var clock = new IdleWorkerClock(DateTimeOffset.UtcNow);
        using var worker = new AdaptiveAutomationWorker(store, runtime, bridge, NullLogger<AdaptiveAutomationWorker>.Instance, clock);
        var tick = typeof(AdaptiveAutomationWorker).GetMethod("TickAsync", flags)!;
        var commands = new List<HardwareCommand>();
        fixture.Provider.BeforeWrite = command => { commands.Add(command); return Task.CompletedTask; };
        async Task Observe()
        {
            clock.Now = clock.Now.AddSeconds(1);
            fixture.Provider.TelemetryOverride = new(clock.Now, "fake", 100, 90, 3, 0)
            {
                CpuUsagePercent = 95, GpuUsagePercent = 95, AcPowerConnected = true, BatteryPercent = 95, BatterySaver = false,
                CpuUsageObservedAtUtc = clock.Now, GpuUsageObservedAtUtc = clock.Now,
                CpuTemperatureObservedAtUtc = clock.Now, GpuTemperatureObservedAtUtc = clock.Now
            };
            await bridge.RunAutomationCycleAsync(ct => (Task)tick.Invoke(worker, [ct])!, token);
        }

        for (int i = 0; i < 3; i++) await Observe();
        Assert.AreEqual(originalMode, fixture.Provider.Mode, "Emergency must remain fresh and continuous for 3 seconds.");
        // Once warm, the initial configuration may reapply its unchanged mode before the heat deadline.
        Assert.IsTrue(commands.All(command => command is SetPerformanceModeCommand mode && mode.Mode == originalMode));
        Assert.IsTrue(fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual(0, fixture.Provider.FanReleases);
        commands.Clear();

        fixture.Provider.FailFanRestore = true;
        await Observe();
        Assert.AreEqual(originalMode, fixture.Provider.Mode, "A rejected EC release must prevent the Office request.");
        Assert.IsTrue(fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual(0, fixture.Provider.FanReleases);
        Assert.AreEqual(0, commands.Count);

        fixture.Provider.FailFanRestore = false;
        await Observe();
        var active = await store.ReadAsync(token);
        Assert.AreEqual(PerformanceMode.Quiet, fixture.Provider.Mode, active.Status?.Reason);
        Assert.AreEqual(office, active.Status?.SubmittedTarget);
        Assert.IsNull(active.Status?.LastError);
        Assert.AreEqual(1, fixture.Provider.FanReleases);
        Assert.IsFalse(fixture.Provider.Controls.StrongCooling);
        Assert.IsNull(fixture.Provider.Controls.ActiveFanControlPlan);
        int releaseIndex = commands.FindIndex(command => command is ReleaseFanControlCommand);
        int officeIndex = commands.FindIndex(command => command is SetPerformanceModeCommand { Mode: PerformanceMode.Quiet });
        Assert.IsTrue(releaseIndex >= 0 && officeIndex > releaseIndex, "EC release must be accepted before applying Office.");
        Assert.IsFalse(commands.Skip(releaseIndex + 1).Any(command => command is SetFanControlCommand or SetStrongCoolingCommand),
            "The Office fan preset must stay excluded after thermal EC release.");

        await store.SetConfigurationAsync(config, token, expectedRevision: active.Revision,
            intent: AutomationConfigurationIntent.Disable, ownerSid: fixture.Owner.UserSid);
        int writes = fixture.Provider.WriteCount;
        await Observe();
        Assert.AreEqual(writes, fixture.Provider.WriteCount, "Disable must stop further worker writes.");

        await worker.StopAsync(token);
        var ended = await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, token);
        Assert.AreEqual(CommandState.Applied, ended.State, ended.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.IsNull(ended.Error);
        Assert.AreEqual(originalMode, fixture.Provider.Mode);
        Assert.AreEqual(originalFan.Control, fixture.Provider.Fan.Control);
        Assert.AreEqual(originalFan.CpuTarget, fixture.Provider.Fan.CpuTarget);
        Assert.AreEqual(originalFan.GpuTarget, fixture.Provider.Fan.GpuTarget);
        Assert.AreEqual(originalStrongCooling, fixture.Provider.Controls.StrongCooling);
        var final = await fixture.Journal.ReadAsync(token);
        Assert.AreEqual(0, final.Current!.Fields.Count);
        Assert.AreEqual(0, final.Unresolved.Length);
    }
}
