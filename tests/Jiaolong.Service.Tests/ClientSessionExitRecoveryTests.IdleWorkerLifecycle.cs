using System.Reflection;
using System.Text.Json;
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
    public async Task Idle_worker_owns_current_session_disable_stops_and_end_restores_declared_targets()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var fixture = new Fixture();
        fixture.Provider.JournalEveryModeStep = true;
        fixture.Provider.CapabilitySupportState = DeviceSupportState.Ready;
        var originalCpu = JsonSerializer.SerializeToElement(fixture.Provider.Cpu);
        var originalLight = fixture.Provider.Light;
        var originalMode = fixture.Provider.Mode;
        var originalSchemes = new[] { fixture.Provider.ReadScheme(Fixture.Scheme), fixture.Provider.ReadScheme(Fixture.OtherScheme) };
        var originalOffsets = fixture.Provider.Offsets();
        Assert.AreEqual(75000, originalOffsets[60], "The original VF is intentionally nonzero; exit must use its official zero target.");

        var store = new AdaptiveAutomationStateStore();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
        typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store,
            new AtomicJsonStore(fixture.Root, "idle-worker-test.json"));
        var bridge = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
        var runtime = new HomeServiceRuntime(bridge, journal: fixture.Journal, requireClientSessions: true);
        var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, token);
        Assert.AreEqual(CommandState.Applied, begun.State);
        var handle = begun.ClientSession!;
        Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(handle.SessionId, null), fixture.Owner, token)).State);

        var office = PresetKey.Create(ControlModeId.Office, 2);
        var game = PresetKey.Create(ControlModeId.Gaming, 2);
        var turbo = PresetKey.Create(ControlModeId.Turbo, 2);
        const string hex = "#0C2238";
        var light = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Lighting, office, "idle lighting",
            JsonSerializer.SerializeToElement(new
            {
                effect = "Static", brightness = originalLight.BrightnessLevel!.Value, hex,
                logo = originalLight.LogoEnabled == true, speed = originalLight.Speed
            }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["hex"] = SettingRequest.Set(hex) } };
        var config = new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
            new(40, 35, 1, true, 85, 90, 1, 20, 15, 1, 20, 30, true, 85, 80, 1, 1, 1, true, 30, [])
            { TurboMinimumDwellSeconds = 1 }, new(office, game, turbo, office, game),
            [new(office, PerformanceMode.Quiet, null)
                { Pages = [light], Selections = [new(ControlPageId.Lighting, true, office, 1)] },
             new(game, PerformanceMode.Balanced, null), new(turbo, PerformanceMode.Turbo, null)]);
        await store.SetConfigurationAsync(config, token, intent: AutomationConfigurationIntent.Enable, ownerSid: fixture.Owner.UserSid);
        var enabled = await store.ReadAsync(token);
        Assert.IsNull(enabled.Recovery);
        Assert.IsTrue(enabled.Configuration!.Enabled);
        Assert.AreEqual(fixture.Owner.UserSid, enabled.OwnerSid);

        var clock = new IdleWorkerClock(DateTimeOffset.UtcNow);
        using var worker = new AdaptiveAutomationWorker(store, runtime, bridge, NullLogger<AdaptiveAutomationWorker>.Instance, clock);
        var tick = typeof(AdaptiveAutomationWorker).GetMethod("TickAsync", flags)!;
        async Task Observe(int idle)
        {
            clock.Now = clock.Now.AddSeconds(1);
            fixture.Provider.TelemetryOverride = new(clock.Now, "fake", 60, 50, 8, 0)
            {
                CpuUsagePercent = 3, GpuUsagePercent = 0, AcPowerConnected = true, BatteryPercent = 95, BatterySaver = false,
                CpuUsageObservedAtUtc = clock.Now, GpuUsageObservedAtUtc = clock.Now,
                CpuTemperatureObservedAtUtc = clock.Now, GpuTemperatureObservedAtUtc = clock.Now
            };
            await store.SetClientContextAsync(new(clock.Now, null, idle, false), token);
            await bridge.RunAutomationCycleAsync(ct => (Task)tick.Invoke(worker, [ct])!, token);
        }

        for (int i = 0; i < 4; i++) await Observe(0);
        Assert.AreNotEqual(PerformanceMode.Quiet, fixture.Provider.Mode, "Low load alone must wait for the configured idle interval.");
        Assert.AreEqual(originalLight, fixture.Provider.Light);
        Assert.AreNotEqual(office, (await store.ReadAsync(token)).Status?.SubmittedTarget);
        await Observe(30);
        var active = await store.ReadAsync(token);
        Assert.AreEqual(PerformanceMode.Quiet, fixture.Provider.Mode, active.Status?.Reason);
        Assert.AreEqual(originalLight with { Red = 12, Green = 34, Blue = 56 }, fixture.Provider.Light);
        Assert.AreEqual(office, active.Status!.SubmittedTarget);
        Assert.IsNotNull(active.Status.LastApplyUtc);
        Assert.IsNull(active.Status.LastError);
        Assert.IsTrue(store.ReadDecisions().Any(d => d.OperationId is not null && d.Pages?.Any(p => p.Page == ControlPageId.Lighting) == true));

        var owned = await fixture.Journal.ReadAsync(token);
        Assert.AreEqual(handle.SessionId, owned.Current!.NativeModeOwnerClientSessionId);
        var lighting = owned.Current.Fields.Where(pair => pair.Key.StartsWith("lighting.", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(lighting.Length > 0);
        Assert.AreEqual(runtime.SessionId, owned.RecoveryScope!.ServiceSessionId);
        foreach (var pair in lighting)
        {
            Assert.AreEqual(ControlOrigin.Adaptive, pair.Value.Origin, pair.Key);
            Assert.AreEqual(handle.SessionId, pair.Value.ClientSessionId, pair.Key);
            Assert.AreEqual(fixture.Owner.UserSid, owned.RecoveryScope.Fields[pair.Key].OwnerSid, pair.Key);
            Assert.AreEqual(handle.HardwareEpoch, owned.RecoveryScope.Fields[pair.Key].HardwareEpoch, pair.Key);
        }
        Assert.IsNull(owned.Current.PendingStep);
        Assert.AreEqual(0, owned.Unresolved.Length);

        await store.SetConfigurationAsync(config, token, expectedRevision: active.Revision,
            intent: AutomationConfigurationIntent.Disable, ownerSid: fixture.Owner.UserSid);
        int writes = fixture.Provider.WriteCount;
        for (int i = 0; i < 3; i++) await Observe(30);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(PerformanceMode.Quiet, fixture.Provider.Mode, "Disable stops scheduling without restoring an old snapshot.");
        Assert.AreEqual(originalLight with { Red = 12, Green = 34, Blue = 56 }, fixture.Provider.Light);
        Assert.IsFalse((await store.ReadAsync(token)).Configuration!.Enabled);
        Assert.AreEqual(handle.SessionId, (await fixture.Journal.ReadAsync(token)).Current!.NativeModeOwnerClientSessionId);

        Assert.AreEqual(CommandState.Applied, (await runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, token)).State);
        await store.SetConfigurationAsync(config, token, intent: AutomationConfigurationIntent.Enable, ownerSid: fixture.Owner.UserSid);
        int reads = fixture.Provider.TelemetryReads;
        await Observe(30);
        Assert.AreEqual(reads, fixture.Provider.TelemetryReads, "The frozen session must block an enabled worker before probing.");
        Assert.AreEqual(writes, fixture.Provider.WriteCount);

        var ended = await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, token);
        Assert.AreEqual(CommandState.Applied, ended.State, ended.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.IsNull(ended.Error);
        Assert.AreEqual(originalMode, fixture.Provider.Mode);
        Assert.AreEqual(originalLight, fixture.Provider.Light);
        Assert.IsTrue(JsonElement.DeepEquals(originalCpu, JsonSerializer.SerializeToElement(fixture.Provider.Cpu)));
        Assert.AreEqual(originalSchemes[0], fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(originalSchemes[1], fixture.Provider.ReadScheme(Fixture.OtherScheme));
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        var final = await fixture.Journal.ReadAsync(token);
        Assert.AreEqual(0, final.Current!.Fields.Count);
        Assert.IsNull(final.Current.NativeModeOwnerClientSessionId);
        Assert.AreEqual(0, final.Unresolved.Length);
        var completed = await new ClientControlSessionStore(fixture.Root).ReadAsync(token);
        Assert.AreEqual(handle.SessionId, completed!.Handle.SessionId);
        Assert.AreEqual(ClientControlSessionPhase.Completed, completed.Phase);
        using var recovery = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "client-session-recovery.json"), token));
        Assert.AreEqual(handle.SessionId, recovery.RootElement.GetProperty("sessionId").GetGuid());
        Assert.IsTrue(recovery.RootElement.GetProperty("gpuRecoveryReady").GetBoolean());
        var steps = recovery.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        CollectionAssert.IsSubsetOf(new[] { "mode", "lighting", "gpu.coreOffsetKhz", "gpu.memoryOffsetKhz", "gpu.vfOffsetsKhz" },
            steps.Select(s => s.GetProperty("key").GetString()!).ToArray());
        Assert.IsTrue(steps.All(s => s.GetProperty("status").GetString() == "Verified"));
        reads = fixture.Provider.TelemetryReads;
        writes = fixture.Provider.WriteCount;
        await Observe(30);
        Assert.AreEqual(reads, fixture.Provider.TelemetryReads);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    private sealed class IdleWorkerClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
