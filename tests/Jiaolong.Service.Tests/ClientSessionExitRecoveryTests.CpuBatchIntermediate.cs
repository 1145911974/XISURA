using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static SetCpuTuningBatchCommand IntermediateCpuBatch(string kind) => new(Guid.NewGuid(), kind switch
    {
        "curve" => new[] { -15, -10, -5 }.Select(value => new CpuTuningPlan(null, null, null, null, null, null, null, value)).ToArray(),
        "boost" => new[] { false, true }.Select(value => new CpuTuningPlan(null, null, null, null, value, null, null, null)).ToArray(),
        "parking" => new[] { 20, 70 }.Select(value => new CpuTuningPlan(null, null, null, null, null, null, null, null)
            { AcMinActiveCoresPercent = value, DcMinActiveCoresPercent = value + 10 }).ToArray(),
        _ => new[] { 4400, 4600 }.Select(value => new CpuTuningPlan(null, null, null, null, null, null, null, null)
            { AcMaxFrequencyMhz = value, DcMaxFrequencyMhz = value - 100 }).ToArray()
    }, true);

    private static async Task<Guid> SeedIntermediateCpuBatch(Fixture fixture, string kind)
    {
        if (kind == "boost") fixture.Provider.SetExternalBoost(2, 4);
        var handle = await fixture.Begin(kind == "curve" ? -30 : null);
        var command = IntermediateCpuBatch(kind);
        fixture.Provider.InterruptCpuBatchAfterPlans = 1;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(command, handle)).State);
        if (kind == "curve") Assert.IsTrue(fixture.Provider.Cpu.PerCoreCurveOptimizer!.Values.All(value => value == -15));
        if (kind == "boost") Assert.AreEqual(0, fixture.Provider.Cpu.AcBoostMode);
        if (kind == "frequency") Assert.AreEqual(4400, fixture.Provider.Cpu.AcFrequency());
        if (kind == "parking") Assert.AreEqual(20, fixture.Provider.Cpu.AcMinActiveCoresPercent);
        return command.OperationId;
    }

    [TestMethod]
    public async Task Cpu_batch_intermediate_restores_exact_curve_boost_frequency_and_parking_baselines()
    {
        foreach (string kind in new[] { "curve", "boost", "frequency", "parking" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedIntermediateCpuBatch(fixture, kind);
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, kind + ":" + result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.IsTrue(fixture.Provider.Cpu.PerCoreCurveOptimizer!.Values.All(value => value == (kind == "curve" ? -30 : OriginalCurve)));
            Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
            Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.DcFrequency());
            Assert.AreEqual(2, fixture.Provider.Cpu.AcBoostMode);
            Assert.AreEqual(kind == "boost" ? 4 : 2, fixture.Provider.Cpu.DcBoostMode);
            Assert.AreEqual(50, fixture.Provider.Cpu.AcMinActiveCoresPercent);
            Assert.AreEqual(50, fixture.Provider.Cpu.DcMinActiveCoresPercent);
            Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Cpu_batch_intermediate_partial_final_restore_retries_without_replacing_original_parent()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        Guid parent = await SeedIntermediateCpuBatch(fixture, "curve");
        fixture.Provider.FailCpuRestoreAfterHalf = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(parent, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        CollectionAssert.AreEqual(new[] { -30, -30, -30, -30, -15, -15, -15, -15 },
            fixture.Provider.Cpu.PerCoreCurveOptimizer!.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.IsTrue(fixture.Provider.Cpu.PerCoreCurveOptimizer!.Values.All(value => value == -30));
    }

    [TestMethod]
    public async Task Cpu_batch_intermediate_missing_foreign_or_scheme_changing_evidence_and_external_values_are_not_recovered()
    {
        foreach (string fault in new[] { "missing", "parent", "scheme", "details", "external" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedIntermediateCpuBatch(fixture, "curve");
            string path = Path.Combine(fixture.Root, "control-execution.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            if (fault == "external") fixture.Provider.SetExternalCoreCurve(0, -7);
            else
            {
                foreach (var state in new[] { node["current"]!, node["unresolved"]!.AsArray()[0]!["state"]! })
                {
                    if (fault == "missing") state["cpuWriteBatch"] = null;
                    else if (fault == "details") state["fields"]!["performance.negativeCurveOptimizer"]!["writeBeforeDetails"]!["perCoreCurveOptimizer"] = null;
                    else
                    {
                        Assert.IsNotNull(state["cpuWriteBatch"], "Batch evidence must exist before it can be corrupted");
                        if (fault == "parent") state["cpuWriteBatch"]!["operationId"] = Guid.NewGuid();
                        else state["cpuWriteBatch"]!["plans"]!.AsArray()[0]!["windowsPowerSchemeId"] = Fixture.OtherScheme;
                    }
                }
                File.WriteAllText(path, node.ToJsonString());
            }
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, records);
        }
    }

    [TestMethod]
    public async Task Cpu_batch_original_plans_are_durable_before_first_native_dispatch()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var command = IntermediateCpuBatch("frequency");
        int checks = 0;
        fixture.Provider.BeforeWrite = async _ =>
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "control-execution.json")))!;
            var batch = node["current"]!["cpuWriteBatch"];
            Assert.IsNotNull(batch);
            Assert.AreEqual(command.OperationId, batch["operationId"]!.GetValue<Guid>());
            Assert.AreEqual(4400, batch["plans"]!.AsArray()[0]!["acMaxFrequencyMhz"]!.GetValue<int>());
            Assert.AreEqual(4600, batch["plans"]!.AsArray()[1]!["acMaxFrequencyMhz"]!.GetValue<int>());
            Assert.AreEqual(command.OperationId, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            checks++;
        };
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(command, handle)).State);
        Assert.AreEqual(1, checks);
    }
}
