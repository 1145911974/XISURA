using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static FanEcRecoveryCheckpoint RuntimeMappingCheckpoint(bool started, bool mappingOnly = false, bool restored = false)
    {
        Dictionary<ushort, byte> baseline = mappingOnly ? [] : new()
            { [0x1060] = 0x81, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 };
        var expected = new Dictionary<ushort, byte>(baseline);
        if (!mappingOnly)
        {
            expected[0x1060] = restored ? (byte)1 : (byte)0x81;
            expected[0x0B20] = started && !restored ? (byte)0x0A : (byte)0;
            expected[0xC83C] = expected[0xC83D] = started && !restored ? (byte)68 : (byte)0;
        }
        return new(baseline, expected, [], restored && started)
        {
            Unchanged = restored && !started,
            MappingLease = new(1, restored ? (byte)1 : (byte)0x81, [], !mappingOnly, restored)
                { TargetWritesStarted = started }
        };
    }

    private static HomeControlState RuntimeMappingObservation(HomeControlState controls, bool restored, bool started, string fault = "none")
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var node = JsonSerializer.SerializeToNode(controls, options)!;
        node["fanEcMapping"] = JsonSerializer.SerializeToNode(new
        {
            capturedAtUtc = fault == "stale" ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow,
            initialization = restored ? 1 : 0x81,
            originalInitialization = fault == "original" ? 3 : 1,
            restored,
            targetWritesStarted = fault == "stage" ? !started : started,
            targetReleaseVerified = fault != "release"
        }, options);
        node["fanEcControl"] = restored ? null : JsonSerializer.SerializeToNode(controls.FanEcControl! with
            { Initialization = 0x81, ExpectedInitialization = 1 }, options);
        return node.Deserialize<HomeControlState>(options)!;
    }

    [TestMethod]
    public async Task Mapping_fan_ordinary_release_retains_mapping_owner_until_verified_end()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        var scope = new FanEcRecoveryScope(fixture.Runtime.SessionId, handle.HardwareEpoch, handle.SessionId,
            fixture.Owner.UserSid, fixture.Provider.Identity);
        var lease = RuntimeMappingCheckpoint(started: true);
        await fixture.Journal.RecordFanEcCheckpointAsync(scope, lease, CancellationToken.None);
        bool restored = false;
        fixture.Provider.ControlStateTransform = controls => RuntimeMappingObservation(controls, restored, started: true);
        fixture.Provider.BeforeWrite = async command =>
        {
            if (command is not ReleaseFanControlCommand release) return;
            restored = release.Reason == ReleaseReason.SessionEnded;
            lease = restored ? RuntimeMappingCheckpoint(started: true, restored: true) : lease with
                { Expected = new(lease.Expected) { [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 } };
            await fixture.Journal.RecordFanEcCheckpointAsync(scope, lease, CancellationToken.None);
        };
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested), handle)).State);
        var released = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.IsTrue(released.Current!.Fields.Keys.Any(key => key.StartsWith("fan.")), "Mapping ownership must survive a target-only release.");
        Assert.AreEqual(false, released.Current.Fields["fan.strongCooling"].LastWritten!.Value!.Value.GetBoolean(),
            "Retained mapping ownership must follow the restored firmware readback rather than the former strong target.");
        Assert.IsFalse(ControlExecutionJournal.FanCheckpointComplete(released.FanEcRecovery!.Checkpoint));
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.IsTrue(restored);
        var completed = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(completed.FanEcRecovery!.Checkpoint));
        Assert.AreEqual(0, completed.Unresolved.Length);
        Assert.AreEqual(ClientControlSessionPhase.Completed,
            (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
        var next = await fixture.Begin(null);
        Assert.AreNotEqual(handle.SessionId, next.SessionId);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "completed-client-sessions", handle.SessionId.ToString("N") + ".json")));
    }

    [TestMethod]
    public async Task Mapping_fan_prior_pending_recovers_mapping_only_and_partial_target_leases()
    {
        foreach (var scenario in new (bool Started, string Fault)[] { (false, "none"), (true, "none"), (true, "release"),
            (false, "original"), (false, "stale"), (true, "stage") })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
            var scope = new FanEcRecoveryScope(fixture.Runtime.SessionId, handle.HardwareEpoch, handle.SessionId,
                fixture.Owner.UserSid, fixture.Provider.Identity);
            var lease = RuntimeMappingCheckpoint(scenario.Started, mappingOnly: !scenario.Started);
            if (scenario.Started) lease.Pending[0xC83C] = [0, 68];
            await fixture.Journal.RecordFanEcCheckpointAsync(scope, lease, CancellationToken.None);
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var operation = saved.Current!.Fields.First(pair => pair.Key.StartsWith("fan.")).Value.OwnerOperationId;
            var fields = saved.Current.Fields.ToDictionary(pair => pair.Key, pair => pair.Key.StartsWith("fan.")
                ? pair.Value with { WriteState = ApplyState.Unknown } : pair.Value);
            await fixture.Journal.SaveAsync(saved.Current with { Fields = fields, Pages = [] }, saved.Identity!, operation, true,
                CancellationToken.None, fixture.Owner.UserSid, nameof(SetStrongCoolingCommand), recoveryScope: saved.RecoveryScope);
            bool restored = false;
            fixture.Provider.ControlStateTransform = controls => RuntimeMappingObservation(controls, restored, scenario.Started, scenario.Fault);
            var journal = new ControlExecutionJournal(fixture.Root);
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var parents = new List<Guid>();
            fixture.Provider.FanRecovery = async (id, admittedScope, token) =>
            {
                parents.Add(id);
                Assert.AreEqual(scope, admittedScope);
                Assert.AreEqual(new ControlExecutionJournal.FanRecoveryOperation(id, scope), journal.CurrentFanRecoveryOperation);
                await journal.RecordFanEcCheckpointAsync(scope, RuntimeMappingCheckpoint(scenario.Started,
                    mappingOnly: !scenario.Started, restored: true), token);
                fixture.Provider.CompleteFanRecovery(false);
                if (!scenario.Started) fixture.Provider.SetExternalFan(0, 23, 29);
                restored = true;
                return new(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed };
            };
            var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(scenario.Fault == "none" ? CommandState.Applied : CommandState.RecoveryRequired, result!.State, scenario.ToString());
            Assert.AreEqual(operation, parents.First(), "Recover the original pending command before creating an exit parent.");
            if (scenario.Fault == "none")
            {
                var complete = await journal.ReadAsync(CancellationToken.None);
                Assert.AreEqual(0, complete.Unresolved.Length);
                Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(complete.FanEcRecovery!.Checkpoint));
            }
        }
    }
}
