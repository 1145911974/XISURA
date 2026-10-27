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
    public async Task Process_activity_scope_requires_current_active_owner_and_hardware_epoch()
    {
        using var fixture = new Fixture();
        Assert.IsNull(fixture.Runtime.GetClientAutomationProcessScope(fixture.Owner.UserSid));
        var handle = await fixture.Begin(null);
        var scope = fixture.Runtime.GetClientAutomationProcessScope(fixture.Owner.UserSid);
        Assert.IsNotNull(scope);
        Assert.AreEqual((fixture.Owner.UserSid, fixture.Owner.SessionId, handle.SessionId), scope.Value);
        Assert.IsNull(fixture.Runtime.GetClientAutomationProcessScope("S-1-5-21-other"));
        Assert.IsNull(fixture.Runtime.GetClientAutomationProcessScope(null));
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.FreezeClientSessionWritesAsync(
            Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.IsNull(fixture.Runtime.GetClientAutomationProcessScope(fixture.Owner.UserSid));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        using var stale = new Fixture();
        await stale.Begin(null);
        await stale.Runtime.ReinitializeAsync(CancellationToken.None);
        Assert.IsNull(stale.Runtime.GetClientAutomationProcessScope(stale.Owner.UserSid));
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Application_activity_uses_owned_process_cpu_and_disable_stops_sampling()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var fixture = new Fixture();
        await fixture.Provider.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), token);
        fixture.Provider.JournalEveryModeStep = true;
        fixture.Provider.CapabilitySupportState = DeviceSupportState.Ready;
        var store = new AdaptiveAutomationStateStore();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
        typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store,
            new AtomicJsonStore(fixture.Root, "process-activity-test.json"));
        var bridge = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
        var runtime = new HomeServiceRuntime(bridge, journal: fixture.Journal, requireClientSessions: true);
        var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, token);
        Assert.AreEqual(CommandState.Applied, begun.State);
        var handle = begun.ClientSession!;
        Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(handle.SessionId, null), fixture.Owner, token)).State);
        var office = PresetKey.Create(ControlModeId.Office, 1);
        var game = PresetKey.Create(ControlModeId.Gaming, 1);
        var turbo = PresetKey.Create(ControlModeId.Turbo, 1);
        var config = new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
            new(90, 90, 8, true, 95, 95, 15, 5, 5, 20, 20, 30, true, 85, 80, 1, 1, 1, false, 300,
                [new("app.exe", false, AdaptiveAutomationStageId.Turbo) { RequireActivity = true }])
            { TurboMinimumDwellSeconds = 1 }, new(office, game, turbo, office, game),
            [new(office, PerformanceMode.Quiet, null), new(game, PerformanceMode.Balanced, null),
             new(turbo, PerformanceMode.Turbo, null)]);
        await store.SetConfigurationAsync(config, token, intent: AutomationConfigurationIntent.Enable, ownerSid: fixture.Owner.UserSid);
        var clock = new IdleWorkerClock(DateTimeOffset.UtcNow);
        double processSeconds = 0;
        int captures = 0;
        var reader = new AdaptiveProcessCpuReader(clock, 1, executable =>
        {
            captures++;
            Assert.AreEqual("app.exe", executable);
            return [new(70, 1, executable, fixture.Owner.UserSid, fixture.Owner.SessionId, TimeSpan.FromSeconds(processSeconds))];
        });
        using var worker = new AdaptiveAutomationWorker(store, runtime, bridge,
            NullLogger<AdaptiveAutomationWorker>.Instance, clock, reader);
        var tick = typeof(AdaptiveAutomationWorker).GetMethod("TickAsync", flags)!;
        async Task Observe(bool submitContext = true)
        {
            clock.Now = clock.Now.AddSeconds(1);
            processSeconds += .1;
            fixture.Provider.TelemetryOverride = new(clock.Now, "fake", 60, 50, 8, 0)
            {
                CpuUsagePercent = 10, GpuUsagePercent = 10, AcPowerConnected = true, BatteryPercent = 95, BatterySaver = false,
                CpuUsageObservedAtUtc = clock.Now, GpuUsageObservedAtUtc = clock.Now,
                CpuTemperatureObservedAtUtc = clock.Now, GpuTemperatureObservedAtUtc = clock.Now
            };
            if (submitContext) await store.SetClientContextAsync(new(clock.Now, "other.exe", 0, false)
                { RunningExecutables = ["app.exe"] }, token);
            await bridge.RunAutomationCycleAsync(ct => (Task)tick.Invoke(worker, [ct])!, token);
        }
        for (int i = 0; i < 8; i++) await Observe();
        var active = await store.ReadAsync(token);
        Assert.AreEqual(turbo, active.Status?.SubmittedTarget,
            "The real worker must feed process activity; global load stays below every upward threshold.");
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.IsTrue(captures >= 4);
        await store.SetConfigurationAsync(config, token, expectedRevision: active.Revision,
            intent: AutomationConfigurationIntent.Disable, ownerSid: fixture.Owner.UserSid);
        int captured = captures, writes = fixture.Provider.WriteCount;
        await Observe();
        Assert.AreEqual(captured, captures, "Disabled scheduling must not sample application CPU.");
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        await store.SetConfigurationAsync(config, token, intent: AutomationConfigurationIntent.Enable, ownerSid: fixture.Owner.UserSid);
        clock.Now = clock.Now.AddSeconds(6);
        await Observe(submitContext: false);
        Assert.AreEqual(captured, captures, "Expired client context cannot select a process to sample.");
        Assert.AreEqual(CommandState.Applied, (await runtime.FreezeClientSessionWritesAsync(
            Guid.NewGuid(), handle.SessionId, fixture.Owner, token)).State);
        await Observe();
        Assert.AreEqual(captured, captures, "Closing the authoritative session must stop process sampling.");
        Assert.AreEqual(CommandState.Applied, (await runtime.EndClientControlSessionAsync(
            Guid.NewGuid(), handle.SessionId, fixture.Owner, token)).State);
        Assert.AreEqual(PerformanceMode.Balanced, fixture.Provider.Mode);
    }
}
