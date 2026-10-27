using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(ClientControlSessionHandle Handle, Guid Parent)> FailedPartialCpuEnd(Fixture fixture)
    {
        var handle = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            new(null, null, null, null, null, null, null, -5), true), handle)).State);
        fixture.Provider.FailCpuRestoreAfterHalf = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        CollectionAssert.AreEqual(new[] { -30, -30, -30, -30, -5, -5, -5, -5 },
            fixture.Provider.Cpu.PerCoreCurveOptimizer!.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
        return (handle, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
    }

    private static HomeServiceRuntime RestartForPartialCpuEnd(Fixture fixture)
    {
        var journal = new ControlExecutionJournal(fixture.Root);
        fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
        return new(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
    }

    [TestMethod]
    public async Task Exit_cpu_partial_curve_resumes_original_plan_in_same_or_new_service()
    {
        foreach (bool restart in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var prior = await FailedPartialCpuEnd(fixture);
            var result = restart ? await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None) : await fixture.End(prior.Handle);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.IsTrue(fixture.Provider.Cpu.PerCoreCurveOptimizer!.Values.All(value => value == -30));
            var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")))!;
            Assert.AreEqual(prior.Parent, plan["operationId"]!.GetValue<Guid>());
            Assert.IsTrue(plan["steps"]!.AsArray().All(step => step!["status"]!.GetValue<string>() == "Verified"));
            Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Exit_cpu_partial_curve_repeated_interruption_keeps_parent_and_verified_steps()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var prior = await FailedPartialCpuEnd(fixture);
        fixture.Provider.FailCpuRestoreAfterHalf = true;
        var runtime = RestartForPartialCpuEnd(fixture);
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(prior.Parent, saved.Unresolved.Single().OperationId);
        var steps = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")))!["steps"]!.AsArray();
        Assert.AreEqual("Failed", steps.Single(step => step!["key"]!.GetValue<string>() == "performance.negativeCurveOptimizer")!["status"]!.GetValue<string>());
        Assert.IsTrue(steps.Where(step => !step!["key"]!.GetValue<string>().StartsWith("performance.")).All(step => step!["status"]!.GetValue<string>() == "Verified"));
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.IsTrue(fixture.Provider.Cpu.PerCoreCurveOptimizer!.Values.All(value => value == -30));
    }

    [TestMethod]
    public async Task Exit_cpu_partial_curve_rejects_unknown_value_missing_checkpoint_or_unstarted_step()
    {
        foreach (string fault in new[] { "external", "checkpoint", "pending", "verified", "late" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var prior = await FailedPartialCpuEnd(fixture);
            string path = Path.Combine(fixture.Root, "client-session-recovery.json");
            if (fault == "external") fixture.Provider.SetExternalCoreCurve(0, -7);
            if (fault is "checkpoint" or "pending" or "verified")
            {
                var plan = JsonNode.Parse(File.ReadAllText(path))!;
                var step = plan["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "performance.negativeCurveOptimizer")!;
                if (fault == "checkpoint") step["expectedDetails"] = null;
                else step["status"] = fault == "pending" ? "Pending" : "Verified";
                File.WriteAllText(path, plan.ToJsonString());
            }
            int reads = 0;
            if (fault == "late") fixture.Provider.ReadBaselineOverride = controls =>
            {
                if (++reads == 3) fixture.Provider.SetExternalCoreCurve(0, -7);
                return controls;
            };
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(prior.Parent, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            if (fault != "late") AssertOriginalRecords(fixture, records);
            else Assert.AreEqual(-7, fixture.Provider.Cpu.PerCoreCurveOptimizer![0]);
        }
    }

    [TestMethod]
    public async Task Exit_cpu_windows_boost_partial_restore_resumes_only_unfinished_dc_index()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.SetExternalBoost(2, 4);
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            new(null, null, null, null, false, null, null, null), true), handle)).State);
        fixture.Provider.FailDcBoostRestore = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        CollectionAssert.AreEqual(new[] { WindowsCpuSetting.AcBoostMode }, fixture.Provider.WindowsRestorationWrites);
        var parent = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId;
        fixture.Provider.FailDcBoostRestore = false;
        fixture.Provider.WindowsRestorationWrites.Clear();
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
            new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
        CollectionAssert.AreEqual(new[] { WindowsCpuSetting.DcBoostMode }, fixture.Provider.WindowsRestorationWrites);
        Assert.AreEqual(2, HomeServiceRuntime.CaptureCpuRestoration("isBoostEnabled", fixture.Provider.Cpu)!.AcBoostMode);
        Assert.AreEqual(4, HomeServiceRuntime.CaptureCpuRestoration("isBoostEnabled", fixture.Provider.Cpu)!.DcBoostMode);
        Assert.AreEqual(parent, JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")))!["operationId"]!.GetValue<Guid>());
    }
}
