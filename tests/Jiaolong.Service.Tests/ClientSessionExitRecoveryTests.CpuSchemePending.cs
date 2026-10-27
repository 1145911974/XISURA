using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static CpuTuningPlan SchemePendingPlan(Guid scheme, int frequency, int parking) =>
        new(null, null, null, null, false, null, scheme, null)
        { AcMaxFrequencyMhz = frequency, DcMaxFrequencyMhz = frequency - 100,
          AcMinActiveCoresPercent = parking, DcMinActiveCoresPercent = parking - 10 };

    private static async Task<(ClientControlSessionHandle Handle, SetCpuTuningBatchCommand Batch)> SeedSchemePendingCpu(
        Fixture fixture, int completedPlans, Func<Task>? inspectBeforeDispatch = null)
    {
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            SchemePendingPlan(Fixture.OtherScheme, 3800, 60), true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            SchemePendingPlan(Fixture.Scheme, 4600, 70), true), handle)).State);
        var command = new SetCpuTuningBatchCommand(Guid.NewGuid(), [SchemePendingPlan(Fixture.OtherScheme, 3700, 40),
            SchemePendingPlan(Fixture.Scheme, 4500, 35), SchemePendingPlan(Fixture.OtherScheme, 3600, 30)], true);
        if (inspectBeforeDispatch is not null) fixture.Provider.BeforeWrite = _ => inspectBeforeDispatch();
        fixture.Provider.InterruptCpuBatchAfterPlans = completedPlans;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(command, handle)).State);
        fixture.Provider.BeforeWrite = null;
        Assert.AreEqual(completedPlans == 2 ? Fixture.Scheme : Fixture.OtherScheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
        Assert.AreEqual(completedPlans == 2 ? 4500 : completedPlans == 1 ? 3700 : 3600, fixture.Provider.Cpu.AcFrequency());
        return (handle, command);
    }

    [TestMethod]
    public async Task Cpu_scheme_pending_restores_every_original_scheme_and_selected_scheme_after_each_prefix()
    {
        foreach (int prefix in new[] { 1, 2, 3 })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = fixture.Provider.ReadScheme(Fixture.Scheme);
            var second = fixture.Provider.ReadScheme(Fixture.OtherScheme);
            await SeedSchemePendingCpu(fixture, prefix);
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
            Assert.AreEqual(second, fixture.Provider.ReadScheme(Fixture.OtherScheme));
            Assert.AreEqual(Fixture.Scheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
            Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Cpu_scheme_pending_partial_index_restore_keeps_original_operation_and_retries_new_runtime()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var first = fixture.Provider.ReadScheme(Fixture.Scheme);
        var second = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        var prior = await SeedSchemePendingCpu(fixture, 1);
        fixture.Provider.FailDcBoostRestore = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(prior.Batch.OperationId, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        Assert.AreEqual(0, fixture.Provider.ReadScheme(Fixture.OtherScheme).DcBoostMode);
        Assert.AreEqual(second.AcMaxFrequencyMhz, fixture.Provider.ReadScheme(Fixture.OtherScheme).AcMaxFrequencyMhz);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
        fixture.Provider.FailDcBoostRestore = false;
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(second, fixture.Provider.ReadScheme(Fixture.OtherScheme));
        Assert.AreEqual(Fixture.Scheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
    }

    [TestMethod]
    public async Task Cpu_scheme_pending_missing_foreign_or_corrupt_index_proof_and_external_inactive_value_are_zero_write()
    {
        foreach (string fault in new[] { "missing", "parent", "target", "before", "beforeValue", "singlePlans", "external" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var prior = await SeedSchemePendingCpu(fixture, 1);
            string path = Path.Combine(fixture.Root, "client-windows-" + prior.Handle.SessionId.ToString("N") + ".json");
            if (fault == "singlePlans")
            {
                string journalPath = Path.Combine(fixture.Root, "control-execution.json");
                var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
                foreach (var state in new[] { journal["current"]!, journal["unresolved"]!.AsArray()[0]!["state"]! }) state["cpuWriteBatch"]!["plans"] = null;
                journal["unresolved"]!.AsArray()[0]!["commandKind"] = nameof(SetCpuTuningCommand);
                File.WriteAllText(journalPath, journal.ToJsonString());
            }
            else if (fault == "external") fixture.Provider.ExternalSchemeIndex(Fixture.Scheme, WindowsCpuSetting.AcMaxFrequencyMhz, 4123);
            else
            {
                var node = JsonNode.Parse(File.ReadAllText(path))!;
                if (fault == "missing") node["pendingWrite"] = null;
                else
                {
                    Assert.IsNotNull(node["pendingWrite"]);
                    if (fault == "parent") node["pendingWrite"]!["operationId"] = Guid.NewGuid();
                    else
                    {
                        var index = node["pendingWrite"]!["schemes"]![Fixture.OtherScheme.ToString()]!["AcMaxFrequencyMhz"]!;
                        if (fault == "target") index["targets"]!.AsArray()[0] = 999;
                        else if (fault == "beforeValue") index["before"] = 3801;
                        else index["before"] = null;
                    }
                }
                File.WriteAllText(path, node.ToJsonString());
            }
            int writes = fixture.Provider.WriteCount;
            var records = OriginalRecords(fixture);
            Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, records);
        }
    }

    [TestMethod]
    public async Task Cpu_scheme_pending_exact_index_before_and_ordered_targets_are_durable_before_dispatch()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        int checks = 0;
        await SeedSchemePendingCpu(fixture, 1, () =>
        {
            string path = Directory.GetFiles(fixture.Root, "client-windows-*.json").Single();
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            var attempt = node["pendingWrite"];
            Assert.IsNotNull(attempt);
            var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "control-execution.json")))!;
            Assert.AreEqual(64, journal["current"]!["windowsWriteProofHash"]!.GetValue<string>().Length);
            Assert.AreEqual(Fixture.Scheme, attempt["initialScheme"]!.GetValue<Guid>());
            var index = attempt["schemes"]![Fixture.OtherScheme.ToString()]!["AcMaxFrequencyMhz"]!;
            Assert.AreEqual(3800, index["before"]!.GetValue<int>(), "Latest pre-command value differs from startup baseline3900.");
            CollectionAssert.AreEqual(new[] { 3700, 3600 }, index["targets"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray());
            checks++;
            return Task.CompletedTask;
        });
        Assert.AreEqual(1, checks);
    }
}
