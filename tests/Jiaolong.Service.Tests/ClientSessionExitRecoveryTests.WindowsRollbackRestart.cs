using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(Guid OperationId, string ScopePath, string ArchivePath)> SeedInterruptedWindowsRollback(
        Fixture fixture, bool owned, bool beforeScopeRewind)
    {
        var handle = await SeedBeforeWindowsRollback(fixture, owned);
        string? pending = null;
        fixture.Provider.RollbackNextCpuWrite = true;
        fixture.Provider.AfterCpuRollback = () => pending = File.ReadAllText(Path.Combine(fixture.Root, "control-execution.json"));
        var command = WindowsRollbackCommand();
        Assert.AreEqual(CommandState.RolledBack, (await fixture.Write(command, handle)).State);
        fixture.Provider.AfterCpuRollback = null;
        Assert.IsNotNull(pending);
        string archivePath = Path.Combine(fixture.Root, "confirmed-windows-rollbacks", command.OperationId.ToString("N") + ".json");
        string scopePath = Path.Combine(fixture.Root, "client-windows-" + handle.SessionId.ToString("N") + ".json");
        // Replay only persisted records from the real dispatch boundary, never the failed command.
        File.WriteAllText(Path.Combine(fixture.Root, "control-execution.json"), pending);
        if (beforeScopeRewind)
            File.WriteAllText(scopePath, JsonNode.Parse(File.ReadAllText(archivePath))!["attempt"]!.ToJsonString());
        return (command.OperationId, scopePath, archivePath);
    }

    [TestMethod]
    public async Task Windows_rollback_restart_consumes_bound_prior_ownership_before_and_after_scope_rewind()
    {
        foreach (bool owned in new[] { false, true })
        foreach (bool beforeRewind in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = fixture.Provider.ReadScheme(Fixture.Scheme);
            var other = fixture.Provider.ReadScheme(Fixture.OtherScheme);
            var prior = await SeedInterruptedWindowsRollback(fixture, owned, beforeRewind);
            fixture.Provider.BeforeWrite = command =>
            {
                Assert.AreNotEqual(prior.OperationId, command.OperationId, "Do not replay the rolled-back request.");
                return Task.CompletedTask;
            };
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
            Assert.AreEqual(other, fixture.Provider.ReadScheme(Fixture.OtherScheme));
            Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
            Assert.IsTrue(File.Exists(prior.ArchivePath));
        }
    }

    [TestMethod]
    public async Task Windows_rollback_restart_preserves_partial_exit_plan_and_retries_without_replaying_parent()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var first = fixture.Provider.ReadScheme(Fixture.Scheme);
        var other = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        var prior = await SeedInterruptedWindowsRollback(fixture, true, false);
        fixture.Provider.FailDcBoostRestore = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreNotEqual(prior.OperationId, saved.Unresolved.Single().OperationId);
        var exitOperation = saved.Unresolved.Single().OperationId;
        fixture.Provider.FailDcBoostRestore = false;
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(other, fixture.Provider.ReadScheme(Fixture.OtherScheme));
        Assert.AreNotEqual(Guid.Empty, exitOperation);
    }

    [TestMethod]
    public async Task Windows_rollback_restart_rejects_unbound_evidence_external_change_and_live_client_without_writes()
    {
        foreach (string fault in new[] { "missing", "owner", "prior", "actual", "running" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var prior = await SeedInterruptedWindowsRollback(fixture, true, false);
            if (fault is "missing" or "owner" or "prior")
            {
                var archive = JsonNode.Parse(File.ReadAllText(prior.ArchivePath))!;
                if (fault == "missing") archive["attempt"]!["pendingWrite"]!.AsObject().Remove("priorFields");
                if (fault == "owner") archive["attempt"]!["pendingWrite"]!["priorFields"]!["performance.acMaxFrequencyMhz"]!["ownerSid"] = "S-1-5-21-999";
                if (fault == "prior") archive["prior"]!["schemes"]![Fixture.Scheme.ToString()]!["writes"]!["AcMaxFrequencyMhz"]!["lastWritten"] = 4321;
                File.WriteAllText(prior.ArchivePath, archive.ToJsonString());
            }
            if (fault == "actual") fixture.Provider.ExternalSchemeIndex(Fixture.OtherScheme,
                Jiaolong.Hardware.Mechrevo.Controls.WindowsCpuSetting.AcMaxFrequencyMhz, 3512);
            int writes = fixture.Provider.WriteCount;
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = fault == "running" ? ClientProcessState.Running : ClientProcessState.Exited }, CancellationToken.None);
            if (fault == "running") Assert.IsNull(result); else Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(prior.OperationId, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            if (fault == "actual") Assert.AreEqual(3512, fixture.Provider.ReadScheme(Fixture.OtherScheme).AcMaxFrequencyMhz);
        }
    }
}
