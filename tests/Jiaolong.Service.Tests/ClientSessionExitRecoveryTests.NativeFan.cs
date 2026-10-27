using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(ClientControlSessionHandle Handle, FanEcRecoveryScope Scope)> SeedRuntimeFan(Fixture fixture, bool ec = true)
    {
        fixture.Provider.FirmwareStrongCooling = true;
        var handle = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), ec), handle)).State);
        var identity = (await fixture.Journal.ReadAsync(CancellationToken.None)).Identity!;
        var scope = new FanEcRecoveryScope(fixture.Runtime.SessionId, handle.HardwareEpoch, handle.SessionId, fixture.Owner.UserSid, identity);
        if (ec) await fixture.Journal.RecordFanEcCheckpointAsync(scope, new(new()
            { [0x1060] = 0, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 }, new()
            { [0x1060] = 0, [0x0B20] = 0x0A, [0xC83C] = 68, [0xC83D] = 68 }, new(), false), CancellationToken.None);
        await fixture.Journal.RecordFanFirmwareAsync(scope, 1, 0, null, false, CancellationToken.None);
        await fixture.Journal.RecordFanFirmwareAsync(scope, 1, 0, 0, false, CancellationToken.None);
        return (handle, scope);
    }

    private static async Task<CommandResult> CompleteRuntimeFan(Fixture fixture, ControlExecutionJournal journal,
        Guid operation, FanEcRecoveryScope scope, CancellationToken token)
    {
        Assert.AreEqual(new ControlExecutionJournal.FanRecoveryOperation(operation, scope), journal.CurrentFanRecoveryOperation);
        var saved = await journal.ReadAsync(token);
        if (saved.FanEcRecovery is { Checkpoint.Unchanged: false } ec && ec.Scope == scope)
        await journal.RecordFanEcCheckpointAsync(scope, ec.Checkpoint with { Expected = new(ec.Checkpoint.Expected)
            { [0x1060] = 0, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 }, Pending = new(), Released = true }, token);
        await journal.RecordFanFirmwareAsync(scope, 0, 1, null, true, token);
        fixture.Provider.CompleteFanRecovery(true);
        await journal.RecordFanFirmwareAsync(scope, 0, 1, 1, true, token);
        return new(operation, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed };
    }

    [TestMethod]
    public async Task Prior_archived_ec_restore_checks_archive_before_any_dispatch_and_preserves_old_record()
    {
        foreach (string fault in new[] { "none", "missing", "mismatch", "pending" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedRuntimeFan(fixture, ec: false);
            var prior = await FanArchivedRecoveryFixture.SeedAsync(fixture.Root, original.Scope, fault);
            var journal = new ControlExecutionJournal(fixture.Root);
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            int calls = 0, writes = 0;
            fixture.Provider.BeforeWrite = _ => { writes++; return Task.CompletedTask; };
            fixture.Provider.FanRecovery = (id, scope, token) => { calls++; return CompleteRuntimeFan(fixture, journal, id, scope, token); };
            var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(fault == "none" ? CommandState.Applied : CommandState.RecoveryRequired, result!.State, fault);
            Assert.AreEqual(fault == "none" ? 1 : 0, calls, fault);
            Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(prior),
                System.Text.Json.JsonSerializer.Serialize((await journal.ReadAsync(CancellationToken.None)).FanEcRecovery));
            if (fault != "none") Assert.AreEqual(0, writes, "Bad archive must prevent other-group writes too.");
        }
    }

    [TestMethod]
    public async Task Prior_unchanged_fan_restore_retains_unchanged_ec_proof_and_completes_session()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var original = await SeedRuntimeFan(fixture);
        var checkpoint = (await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
        await fixture.Journal.RecordFanEcCheckpointAsync(original.Scope,
            checkpoint with { Expected = new(checkpoint.Baseline), Unchanged = true }, CancellationToken.None);
        fixture.Provider.SetExternalFan(0, 0, 0);
        var journal = new ControlExecutionJournal(fixture.Root);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.FanRecovery = (id, scope, token) => CompleteRuntimeFan(fixture, journal, id, scope, token);
        var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State);
        var saved = await journal.ReadAsync(CancellationToken.None);
        Assert.IsTrue(saved.FanEcRecovery!.Checkpoint.Unchanged);
        Assert.IsFalse(saved.FanEcRecovery.Checkpoint.Released);
        Assert.AreEqual(0, saved.Unresolved.Length);
    }

    [TestMethod]
    public async Task Prior_firmware_only_restore_and_retry_archive_without_inventing_ec_ownership()
    {
        foreach (bool retry in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedRuntimeFan(fixture, ec: false);
            fixture.Provider.FanReadbackAvailable = false;
            var journal = new ControlExecutionJournal(fixture.Root);
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var parents = new List<Guid>();
            fixture.Provider.FanRecovery = (id, scope, token) =>
            {
                parents.Add(id);
                return retry && parents.Count == 1 ? Task.FromResult(new CommandResult(id, CommandState.RecoveryRequired, null,
                    RequiredUserAction.None, null, false) { Mutation = HardwareMutation.NoMutation }) : CompleteRuntimeFan(fixture, journal, id, scope, token);
            };
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            var result = await restarted.RestorePriorClientSessionAsync(probe, CancellationToken.None);
            if (retry)
            {
                Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
                Assert.AreEqual(1, parents.Count);
                result = await restarted.RestorePriorClientSessionAsync(probe, CancellationToken.None);
                Assert.AreEqual(parents[0], parents[1]);
            }
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            var saved = await journal.ReadAsync(CancellationToken.None);
            Assert.IsNull(saved.FanEcRecovery);
            Assert.AreEqual(original.Scope, saved.FanFirmwareRecovery!.Scope);
            Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete(saved.FanFirmwareRecovery));
            Assert.AreEqual(0, saved.Unresolved.Length);
            var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner with { ProcessId = 43 }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, next.State);
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "completed-client-sessions", original.Handle.SessionId.ToString("N") + ".json")));
        }
    }

    [TestMethod]
    public async Task Prior_native_fan_restore_keeps_original_scope_and_archives_both_verified_native_records()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var original = await SeedRuntimeFan(fixture);
        var journal = new ControlExecutionJournal(fixture.Root);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        int calls = 0;
        fixture.Provider.FanRecovery = (id, scope, token) => { calls++; return CompleteRuntimeFan(fixture, journal, id, scope, token); };
        var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(1, calls);
        var completed = await journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(original.Scope, completed.FanEcRecovery!.Scope);
        Assert.IsTrue(completed.FanEcRecovery.Checkpoint.Released);
        Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete(completed.FanFirmwareRecovery!));
        Assert.AreEqual(0, completed.Unresolved.Length);
        var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner with { ProcessId = 43 }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "completed-client-sessions", original.Handle.SessionId.ToString("N") + ".json")));
    }

    [TestMethod]
    public async Task Prior_native_fan_retry_uses_the_original_exit_parent_and_does_not_clear_records_after_a_failure()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var original = await SeedRuntimeFan(fixture);
        var journal = new ControlExecutionJournal(fixture.Root);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var parents = new List<Guid>();
        fixture.Provider.FanRecovery = (id, scope, token) =>
        {
            parents.Add(id);
            return parents.Count == 1 ? Task.FromResult(new CommandResult(id, CommandState.RecoveryRequired, null,
                RequiredUserAction.None, null, false) { Mutation = HardwareMutation.NoMutation }) : CompleteRuntimeFan(fixture, journal, id, scope, token);
        };
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await restarted.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        var failed = await journal.ReadAsync(CancellationToken.None);
        Assert.IsFalse(failed.FanEcRecovery!.Checkpoint.Released);
        Assert.AreEqual(1, failed.Unresolved.Length);
        Assert.AreEqual(original.Scope, failed.FanEcRecovery.Scope);
        var retried = await restarted.RestorePriorClientSessionAsync(probe, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, retried!.State, retried.Error?.Details.GetValueOrDefault("clientSessionRecovery") +
            " calls=" + parents.Count + " " + File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(2, parents.Count);
        Assert.AreEqual(parents[0], parents[1]);
        Assert.AreEqual(original.Scope, (await journal.ReadAsync(CancellationToken.None)).FanFirmwareRecovery!.Scope);
    }
}
