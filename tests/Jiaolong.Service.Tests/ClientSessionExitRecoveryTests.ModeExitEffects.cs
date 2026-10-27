using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<Guid> SeedModeExitEffects(Fixture fixture, bool gpu)
    {
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), handle);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        if (gpu)
        {
            await fixture.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10000, true)
                { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle);
            await fixture.Write(new SetGpuVfCurveCommand(Guid.NewGuid(), fixture.Provider.Offsets(),
                Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 75000).ToArray(), true), handle);
            fixture.Provider.JournalEveryGpuStep = fixture.Provider.BlockGpuWhilePending = fixture.Provider.KeepKnownGpuIntermediate = true;
            bool interrupted = false;
            fixture.Provider.BeforeWrite = command =>
            {
                if (!interrupted && GpuCommandKind(command) == "core") fixture.Provider.FailNextGpuWrite = interrupted = true;
                return Task.CompletedTask;
            };
        }
        fixture.Provider.JournalEveryModeStep = true;
        fixture.Provider.ModeWriteEffects = () => fixture.Provider.SetExternalCurve(-10);
        fixture.Provider.RejectCpuRestores = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        fixture.Provider.BeforeWrite = null;
        fixture.Provider.ModeWriteEffects = null;
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(-10, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(PerformanceMode.Turbo, saved.Unresolved.Single().NativeModeAttempt!.Observed);
        return saved.Unresolved.Single().OperationId;
    }

    [TestMethod]
    public async Task Prior_mode_exit_uses_saved_post_mode_cpu_values_and_restores_exact_original_curve()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var parent = await SeedModeExitEffects(fixture, gpu: false);
        var journal = new ControlExecutionJournal(fixture.Root);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
        fixture.Provider.RejectCpuRestores = false;
        int modeWrites = fixture.Provider.NativeModeScopes.Count;
        var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(modeWrites, fixture.Provider.NativeModeScopes.Count);
        Assert.AreEqual(parent.ToString(), ExitGpuPlan(fixture)["operationId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Prior_mode_and_gpu_exit_attempts_resume_same_parent_without_replaying_verified_mode()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var parent = await SeedModeExitEffects(fixture, gpu: true);
        var journal = new ControlExecutionJournal(fixture.Root);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
        fixture.Provider.RejectCpuRestores = false;
        fixture.Provider.RecoverOnRequest = true;
        int modeWrites = fixture.Provider.NativeModeScopes.Count;
        var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(modeWrites, fixture.Provider.NativeModeScopes.Count);
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        Assert.AreEqual(parent.ToString(), ExitGpuPlan(fixture)["operationId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Prior_mode_exit_rejects_foreign_native_scope_wrong_observation_and_missing_exact_cpu_checkpoint()
    {
        foreach (string fault in new[] { "scope", "observed", "details" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedModeExitEffects(fixture, gpu: false);
            var journalPath = Path.Combine(fixture.Root, "control-execution.json");
            var node = JsonNode.Parse(File.ReadAllText(journalPath))!;
            var native = node["unresolved"]!.AsArray()[0]!["nativeModeAttempt"]!;
            if (fault == "scope") native["serviceSessionId"] = Guid.NewGuid().ToString();
            if (fault == "observed") native["observed"] = (int)PerformanceMode.Balanced;
            if (fault != "details") File.WriteAllText(journalPath, node.ToJsonString());
            else
            {
                var plan = ExitGpuPlan(fixture);
                plan["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "performance.negativeCurveOptimizer")!["expectedDetails"] = null;
                File.WriteAllText(Path.Combine(fixture.Root, "client-session-recovery.json"), plan.ToJsonString());
            }
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
            fixture.Provider.RejectCpuRestores = false;
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State, fault);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(-10, fixture.Provider.Cpu.NegativeCurveOptimizer);
        }
    }

    [TestMethod]
    public async Task Prior_mode_exit_without_post_mode_checkpoint_does_not_infer_cpu_side_effect_values()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        await SeedModeExitEffects(fixture, gpu: false);
        var plan = ExitGpuPlan(fixture);
        plan["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "mode")!["status"] = "Applying";
        var curve = plan["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "performance.negativeCurveOptimizer")!;
        curve["expectedDetails"]!["perCoreCurveOptimizer"]!.AsObject().ToList().ForEach(pair =>
            curve["expectedDetails"]!["perCoreCurveOptimizer"]![pair.Key] = -5);
        curve["expected"]!["value"] = -5;
        File.WriteAllText(Path.Combine(fixture.Root, "client-session-recovery.json"), plan.ToJsonString());
        var journal = new ControlExecutionJournal(fixture.Root);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
        fixture.Provider.RejectCpuRestores = false;
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(
            new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(-10, fixture.Provider.Cpu.NegativeCurveOptimizer);
    }
}
