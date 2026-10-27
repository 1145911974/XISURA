using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    // Automatic hardware ownership persists the configured user SID; adaptive:<SID> is only the preset-catalog owner.
    [TestMethod]
    public async Task Automatic_lighting_owner_restores_from_the_same_session_after_service_restart()
    {
        foreach (bool interrupted in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var key = PresetKey.Create(ControlModeId.Turbo, 2);
            var store = new AdaptiveAutomationStateStore();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
            typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store,
                new AtomicJsonStore(fixture.Root, "adaptive-test.json"));
            typeof(AdaptiveAutomationStateStore).GetField("configurationOwnerSid", flags)!.SetValue(store, fixture.Owner.UserSid);
            typeof(AdaptiveAutomationStateStore).GetField("configuration", flags)!.SetValue(store,
                new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
                    new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
                    new(key, key, key, key, key), []));

            var adaptive = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
            var runtime = new HomeServiceRuntime(adaptive, journal: fixture.Journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, begun.State);
            var handle = begun.ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                new(handle.SessionId, null), fixture.Owner, CancellationToken.None)).State);

            var original = fixture.Provider.Light;
            var target = original with { Red = 12, Green = 34 };
            string hex = $"#{target.Red:X2}{target.Green:X2}{target.Blue:X2}";
            var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Lighting, key,
                "automatic lighting", JsonSerializer.SerializeToElement(new
                {
                    effect = "Static", brightness = original.BrightnessLevel!.Value,
                    hex, logo = original.LogoEnabled == true, speed = original.Speed
                }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["hex"] = SettingRequest.Set(hex) } };
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(
                new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), CancellationToken.None,
                userSid: HomeServiceRuntime.AutomaticCatalogOwner(fixture.Owner.UserSid))).State);
            if (interrupted)
            {
                fixture.Provider.FailNextLightingWrite = true;
                fixture.Provider.LightingWriteFault = plan => plan with { Red = original.Red };
            }
            var operation = Guid.NewGuid();
            // Admission must join the active session without a caller-supplied session id.
            var applied = await adaptive.ExecuteAutomaticCommandAsync(runtime,
                new ApplyControlRequestCommand(operation, new(operation, 0, ControlOrigin.Adaptive,
                    ControlModeId.Turbo, PerformanceMode.Turbo, [new(ControlPageId.Lighting, true, key, 1)])), CancellationToken.None);
            fixture.Provider.LightingWriteFault = null;
            Assert.AreEqual(interrupted ? CommandState.RecoveryRequired : CommandState.Applied,
                applied.State, applied.Error?.Code.ToString());
            Assert.AreEqual(interrupted ? target with { Red = original.Red } : target, fixture.Provider.Light);

            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var lighting = saved.Current!.Fields.Where(pair => pair.Key.StartsWith("lighting", StringComparison.Ordinal)).ToArray();
            Assert.IsTrue(lighting.Length > 0);
            if (interrupted) Assert.AreEqual(operation, saved.Unresolved.Single().OperationId);
            foreach (var pair in lighting)
            {
                Assert.AreEqual(ControlOrigin.Adaptive, pair.Value.Origin, pair.Key);
                Assert.AreEqual(fixture.Owner.UserSid, saved.RecoveryScope!.Fields[pair.Key].OwnerSid, pair.Key);
                Assert.AreEqual(handle.SessionId, pair.Value.ClientSessionId, pair.Key);
                Assert.AreEqual(handle.HardwareEpoch, saved.RecoveryScope.Fields[pair.Key].HardwareEpoch, pair.Key);
            }
            var writesBeforeRestore = fixture.Provider.WriteCount;
            var restarted = Restarted(fixture);
            var restored = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);

            Assert.AreEqual(CommandState.Applied, restored!.State, restored.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(original, fixture.Provider.Light);
            Assert.IsTrue(fixture.Provider.WriteCount > writesBeforeRestore);
            var final = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.AreEqual(0, final.Unresolved.Length);
            Assert.AreEqual(0, final.Current!.Fields.Count);
            var completed = await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.AreEqual(handle.SessionId, completed!.Handle.SessionId);
            Assert.AreEqual(ClientControlSessionPhase.Completed, completed.Phase);
        }
    }
}
