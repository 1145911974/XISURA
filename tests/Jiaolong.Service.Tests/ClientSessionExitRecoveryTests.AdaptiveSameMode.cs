using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static PerformanceModeEvidence SameModeEvidence(PerformanceMode mode = PerformanceMode.Turbo,
        byte raw = 1, bool stale = false) => new(DateTimeOffset.UtcNow - (stale ? TimeSpan.FromSeconds(10) : TimeSpan.Zero),
            "fake", 4, raw.ToString("X2"), raw, mode, null, "RequestStateUnverified");

    private static async Task<(AdaptiveAutomationStateStore Store, AdaptiveAutomationHardwareProvider Bridge,
        HomeServiceRuntime Runtime, ClientControlSessionHandle Handle)> SameModeRuntime(Fixture fixture,
            PerformanceMode initialMode = PerformanceMode.Turbo)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var office = PresetKey.Create(ControlModeId.Office, 2);
        var game = PresetKey.Create(ControlModeId.Gaming, 2);
        var turbo = PresetKey.Create(ControlModeId.Turbo, 2);
        var store = new AdaptiveAutomationStateStore();
        typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
        typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store, new AtomicJsonStore(fixture.Root, "same-mode-auto.json"));
        await store.SetConfigurationAsync(
            new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
                new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
                new(office, game, turbo, office, game),
                [new(office, PerformanceMode.Quiet, null), new(game, PerformanceMode.Balanced, null), new(turbo, PerformanceMode.Turbo, null)]),
            CancellationToken.None, intent: AutomationConfigurationIntent.Enable, ownerSid: fixture.Owner.UserSid);
        // This test needs an already-default GPU; the shared fixture starts with a +75 MHz VF node.
        Assert.AreEqual(CommandState.Applied, (await fixture.Provider.ExecuteAsync(new SetGpuVfCurveCommand(Guid.NewGuid(),
            fixture.Provider.Offsets(), new int[127], true), CancellationToken.None)).State);
        fixture.Provider.SetExternalMode(initialMode);
        fixture.Provider.JournalEveryModeStep = true;
        var bridge = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
        var runtime = new HomeServiceRuntime(bridge, journal: fixture.Journal, requireClientSessions: true);
        var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, begun.State);
        var handle = begun.ClientSession!;
        Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(handle.SessionId, null), fixture.Owner, CancellationToken.None)).State);
        return (store, bridge, runtime, handle);
    }

    private static ApplyControlRequestCommand SameModeRequest(ClientControlSessionHandle handle,
        PerformanceMode target = PerformanceMode.Turbo)
    {
        var id = Guid.NewGuid();
        return new(id, new(id, 0, ControlOrigin.Adaptive, ControlModeId.Turbo, target, [])) { ClientSessionId = handle.SessionId };
    }

    [TestMethod]
    public async Task Adaptive_same_mode_without_hardware_pages_does_not_write_or_claim_effective_confirmation()
    {
        foreach (bool priorOwner in new[] { false, true })
        {
            using var fixture = new Fixture();
            var setup = await SameModeRuntime(fixture);
            if (priorOwner)
            {
                var written = await setup.Runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo)
                    { ClientSessionId = setup.Handle.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
                Assert.AreEqual(CommandState.Applied, written.State);
                var saved = await setup.Store.ReadAsync(CancellationToken.None);
                await setup.Store.SetConfigurationAsync(saved.Configuration!, CancellationToken.None,
                    intent: AutomationConfigurationIntent.Enable, ownerSid: fixture.Owner.UserSid);
            }
            int reads = 0;
            fixture.Provider.NativeModeReadOverride = _ => { reads++; return Task.FromResult<PerformanceModeEvidence?>(SameModeEvidence()); };
            var before = await fixture.Journal.ReadAsync(CancellationToken.None);
            var writes = fixture.Provider.WriteCount;
            var result = await setup.Bridge.ExecuteAutomaticCommandAsync(setup.Runtime, SameModeRequest(setup.Handle), CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State,
                $"priorOwner={priorOwner}; reads={reads}; mode={fixture.Provider.Mode}; result={System.Text.Json.JsonSerializer.Serialize(result)}");
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.IsNull(result.VerifiedPerformanceMode);
            Assert.AreEqual(ApplyState.SubmittedUnverified, result.ExecutionState!.NativeModeApplyState);
            Assert.IsTrue(reads >= 2);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            var after = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(before.Current!.NativeModeOwnerClientSessionId, after.Current!.NativeModeOwnerClientSessionId);
            Assert.AreEqual(before.RecoveryScope!.NativeModeTarget, after.RecoveryScope!.NativeModeTarget);
            Assert.AreEqual(before.Current.Fields.Count, after.Current.Fields.Count);
            Assert.AreEqual(CommandState.Applied, (await setup.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
                setup.Handle.SessionId, fixture.Owner, CancellationToken.None)).State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, "No new native ownership means no exit reapplication");
        }
    }

    [TestMethod]
    public async Task Adaptive_same_mode_rejects_unreliable_or_changed_evidence_and_revision_without_writing()
    {
        foreach (var fault in new[] { "first-stale", "second-stale", "second-mode", "second-raw", "revision" })
        {
            using var fixture = new Fixture();
            var setup = await SameModeRuntime(fixture);
            int reads = 0;
            fixture.Provider.NativeModeReadOverride = async _ =>
            {
                var observation = ++reads;
                if (fault == "revision" && observation == 2)
                {
                    var saved = await setup.Store.ReadAsync(CancellationToken.None);
                    await setup.Store.SetConfigurationAsync(saved.Configuration!, CancellationToken.None,
                        intent: AutomationConfigurationIntent.Disable, ownerSid: fixture.Owner.UserSid);
                }
                return SameModeEvidence(fault == "second-mode" && observation == 2 ? PerformanceMode.Quiet : PerformanceMode.Turbo,
                    fault == "second-raw" && observation == 2 ? (byte)2 : (byte)1,
                    fault == "first-stale" || fault == "second-stale" && observation == 2);
            };
            var writes = fixture.Provider.WriteCount;
            var revision = (await setup.Store.ReadAsync(CancellationToken.None)).Revision;
            var result = await setup.Bridge.ExecuteAutomaticCommandAsync(setup.Runtime, SameModeRequest(setup.Handle), CancellationToken.None, revision);
            Assert.AreEqual(CommandState.Rejected, result.State, fault);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, fault);
            Assert.IsNull(result.VerifiedPerformanceMode, fault);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, fault);
        }
    }

    [TestMethod]
    public async Task Adaptive_worker_retention_preserves_prior_uncertain_write_fence_without_claiming_confirmation()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (bool priorUnverifiedWrite in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Provider.CapabilitySupportState = DeviceSupportState.Ready;
            var setup = await SameModeRuntime(fixture, PerformanceMode.Balanced);
            var clock = new IdleWorkerClock(DateTimeOffset.UtcNow);
            using var worker = new AdaptiveAutomationWorker(setup.Store, setup.Runtime, setup.Bridge,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveAutomationWorker>.Instance, clock);
            var fence = typeof(AdaptiveAutomationWorker).GetField("unverifiedNativeMode", flags)!;
            fence.SetValue(worker, priorUnverifiedWrite);
            var tick = typeof(AdaptiveAutomationWorker).GetMethod("TickAsync", flags)!;
            int writes = fixture.Provider.WriteCount;
            for (int i = 0; i < 3; i++)
            {
                clock.Now = clock.Now.AddSeconds(1);
                fixture.Provider.TelemetryOverride = new(clock.Now, "fake", 60, 50, 8, 0)
                {
                    CpuUsagePercent = 10, GpuUsagePercent = 10, AcPowerConnected = true,
                    BatteryPercent = 95, BatterySaver = false
                };
                await setup.Bridge.RunAutomationCycleAsync(ct => (Task)tick.Invoke(worker, [ct])!, CancellationToken.None);
            }
            var saved = await setup.Store.ReadAsync(CancellationToken.None);
            Assert.AreEqual(PresetKey.Create(ControlModeId.Gaming, 2), saved.Status?.SubmittedTarget,
                System.Text.Json.JsonSerializer.Serialize(saved.Status));
            Assert.AreEqual(ApplyState.SubmittedUnverified, saved.Status?.TargetApplyState);
            Assert.IsNull(saved.Status?.CurrentTarget);
            Assert.AreEqual(priorUnverifiedWrite, (bool)fence.GetValue(worker)!);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
        }
    }

    [TestMethod]
    public async Task Adaptive_different_native_target_still_dispatches_existing_mode_command()
    {
        using var fixture = new Fixture();
        var setup = await SameModeRuntime(fixture);
        var writes = fixture.Provider.WriteCount;
        var result = await setup.Bridge.ExecuteAutomaticCommandAsync(setup.Runtime,
            SameModeRequest(setup.Handle, PerformanceMode.Balanced), CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Code.ToString());
        Assert.IsTrue(fixture.Provider.WriteCount > writes);
        Assert.AreEqual(PerformanceMode.Balanced, fixture.Provider.Mode);
    }
}
