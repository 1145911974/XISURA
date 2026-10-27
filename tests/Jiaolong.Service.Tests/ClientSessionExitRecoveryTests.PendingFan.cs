using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(FanEcRecoveryScope Scope, Guid Operation)> SeedPendingFan(Fixture fixture,
        string kind = nameof(SetStrongCoolingCommand))
    {
        var original = await SeedRuntimeFan(fixture);
        if (kind != nameof(SetStrongCoolingCommand))
        {
            HardwareCommand command = kind == nameof(SetFanControlCommand)
                ? new SetFanControlCommand(Guid.NewGuid(), new([new(30, 30), new(100, 100)]) { Strategy = "Fixed", FixedRpm = 4000 }, true)
                : new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested);
            fixture.Provider.BeforeWrite = _ => throw new IOException("fakeInterruptedFanCommand");
            try { await fixture.Write(command, original.Handle); }
            finally { fixture.Provider.BeforeWrite = null; }
            var pending = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single();
            Assert.AreEqual(kind, pending.CommandKind);
            return (original.Scope, pending.OperationId);
        }
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        var operation = saved.Current!.Fields.First(pair => pair.Key.StartsWith("fan.")).Value.OwnerOperationId;
        var fields = saved.Current.Fields.ToDictionary(pair => pair.Key, pair => pair.Key.StartsWith("fan.")
            ? pair.Value with { WriteState = ApplyState.Unknown } : pair.Value);
        await fixture.Journal.SaveAsync(saved.Current with { Fields = fields, Pages = [] }, saved.Identity!, operation, true,
            CancellationToken.None, fixture.Owner.UserSid, nameof(SetStrongCoolingCommand), recoveryScope: saved.RecoveryScope);
        return (original.Scope, operation);
    }

    [TestMethod]
    public async Task Prior_pending_fan_restores_under_original_operation_before_creating_exit_parent()
    {
        foreach (string kind in new[] { nameof(SetStrongCoolingCommand), nameof(SetFanControlCommand), nameof(ReleaseFanControlCommand) })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedPendingFan(fixture, kind);
            var journal = new ControlExecutionJournal(fixture.Root);
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var parents = new List<Guid>();
            fixture.Provider.FanRecovery = async (id, scope, token) =>
            {
                parents.Add(id);
                var pending = (await journal.ReadAsync(token)).Unresolved.Single(item => item.OperationId == id);
                if (parents.Count == 1)
                {
                    Assert.AreEqual(original.Operation, id);
                    Assert.AreEqual(kind, pending.CommandKind);
                    Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
                    return await CompleteRuntimeFan(fixture, journal, id, scope, token);
                }
                Assert.AreEqual("EndClientControlSession", pending.CommandKind);
                Assert.AreNotEqual(original.Operation, id);
                return new(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.NoMutation };
            };
            var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(2, parents.Count);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Prior_pending_fan_lost_completion_resumes_original_record_in_same_or_new_service()
    {
        foreach (bool restart in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedPendingFan(fixture);
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            int calls = 0;
            fixture.Provider.FanRecovery = async (id, scope, token) =>
            {
                calls++;
                if (calls == 1)
                {
                    Assert.AreEqual(original.Operation, id);
                    await CompleteRuntimeFan(fixture, journal, id, scope, token);
                    return new(id, CommandState.RecoveryRequired, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Unknown };
                }
                if (calls == 2) Assert.AreEqual(original.Operation, id);
                return new(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.NoMutation };
            };
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
            var pending = (await journal.ReadAsync(CancellationToken.None)).Unresolved.Single();
            Assert.AreEqual(original.Operation, pending.OperationId);
            Assert.AreEqual(nameof(SetStrongCoolingCommand), pending.CommandKind);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
            if (restart)
            {
                journal = new(fixture.Root);
                runtime = new(fixture.Provider, journal: journal, requireClientSessions: true,
                    readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            }
            var result = await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(3, calls);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Prior_pending_fan_rejects_foreign_kind_owner_and_unrelated_unknown_fields_before_any_write()
    {
        foreach (string fault in new[] { "kind", "owner", "field" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedPendingFan(fixture);
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            if (fault == "kind") saved = saved with { Unresolved = [saved.Unresolved[0] with { CommandKind = nameof(SetPerformanceModeCommand) }] };
            if (fault == "owner") saved = saved with { Unresolved = [saved.Unresolved[0] with { OwnerSid = "foreign" }] };
            if (fault == "field")
            {
                var fields = saved.Current!.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
                var first = fields.First();
                fields["performance.unproven"] = first.Value with { WriteState = ApplyState.Unknown };
                var changed = saved.Current with { Fields = fields };
                var owners = new Dictionary<string, ControlFieldRecoveryScope>(saved.RecoveryScope!.Fields)
                    { ["performance.unproven"] = saved.RecoveryScope.Fields.First().Value };
                saved = saved with { Current = changed, Unresolved = [saved.Unresolved[0] with { State = changed }],
                    RecoveryScope = saved.RecoveryScope with { Fields = owners } };
            }
            await new AtomicJsonStore(fixture.Root, "control-execution.json").WriteAsync(saved, CancellationToken.None);
            var journal = new ControlExecutionJournal(fixture.Root);
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            int writes = 0;
            fixture.Provider.BeforeWrite = _ => { writes++; return Task.CompletedTask; };
            fixture.Provider.FanRecovery = (id, scope, token) => throw new AssertFailedException("Invalid original evidence must not dispatch native recovery.");
            Assert.AreEqual(CommandState.RecoveryRequired, (await restarted.RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(0, writes);
        }
    }

    [TestMethod]
    public async Task Prior_closing_without_exit_plan_rechecks_live_state_and_builds_plan_after_manual_recovery_completed()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var original = await SeedRuntimeFan(fixture);
        var store = new ClientControlSessionStore(fixture.Root);
        await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing },
            CancellationToken.None, requireExisting: true);
        using (fixture.Journal.BindFanRecovery(new(Guid.NewGuid(), original.Scope)))
        {
            var operation = fixture.Journal.CurrentFanRecoveryOperation!.OperationId;
            await CompleteRuntimeFan(fixture, fixture.Journal, operation, original.Scope, CancellationToken.None);
        }
        var journal = new ControlExecutionJournal(fixture.Root);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.FanRecovery = (id, scope, token) => Task.FromResult(new CommandResult(id, CommandState.Applied, null,
            RequiredUserAction.None, null, false) { Mutation = HardwareMutation.NoMutation });
        var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
    }
}
