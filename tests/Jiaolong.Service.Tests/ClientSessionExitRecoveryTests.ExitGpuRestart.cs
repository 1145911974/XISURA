using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static string GpuCommandKind(HardwareCommand command) => command switch
    { SetGpuCoreOffsetCommand => "core", SetGpuVfCurveCommand => "vf", SetGpuMemoryOffsetCommand => "memory", _ => "" };

    private static async Task<Guid> SeedExitGpu(Fixture fixture, string kind)
    {
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10000, true)
            { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle);
        await fixture.Write(new SetGpuVfCurveCommand(Guid.NewGuid(), fixture.Provider.Offsets(),
            Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 75000).ToArray(), true), handle);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        fixture.Provider.BlockGpuWhilePending = fixture.Provider.KeepKnownGpuIntermediate = true;
        fixture.Provider.JournalEveryGpuStep = true;
        bool failed = false;
        fixture.Provider.BeforeWrite = command =>
        {
            if (!failed && GpuCommandKind(command) == kind) fixture.Provider.FailNextGpuWrite = failed = true;
            return Task.CompletedTask;
        };
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        fixture.Provider.BeforeWrite = null;
        var pending = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single();
        Assert.AreEqual("EndClientControlSession", pending.CommandKind);
        Assert.AreEqual(kind, pending.GpuAttempt!.Kind);
        return pending.OperationId;
    }

    private static JsonNode ExitGpuPlan(Fixture fixture) => JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")))!;

    [TestMethod]
    public async Task Prior_exit_gpu_partial_core_vf_and_memory_resume_original_plan_without_repeating_verified_steps()
    {
        foreach (string kind in new[] { "core", "vf", "memory" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var parent = await SeedExitGpu(fixture, kind);
            var originalPlan = ExitGpuPlan(fixture);
            var verified = originalPlan["steps"]!.AsArray().Where(step => step!["status"]!.GetValue<string>() == "Verified")
                .Select(step => step!["key"]!.GetValue<string>()).ToHashSet();
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindGpuJournal(journal);
            fixture.Provider.ReadRecoveryScope = () =>
            {
                if (journal.CurrentGpuOperation is { } scope)
                {
                    Assert.AreEqual(parent, scope.OperationId);
                    Assert.AreEqual(fixture.Runtime.SessionId, scope.SessionId);
                }
                return journal.CurrentGpuOperation?.OperationId;
            };
            var writes = new List<string>();
            fixture.Provider.BeforeWrite = command => { writes.Add(GpuCommandKind(command)); return Task.CompletedTask; };
            fixture.Provider.RecoverOnRequest = true;
            var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, kind + " " + result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(parent.ToString(), ExitGpuPlan(fixture)["operationId"]!.GetValue<string>());
            if (verified.Contains("gpu.coreOffsetKhz")) Assert.IsFalse(writes.Contains("core"));
            if (verified.Contains("gpu.vfOffsetsKhz")) Assert.IsFalse(writes.Contains("vf"));
            CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
            Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
            Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Prior_exit_gpu_lost_recovery_confirmation_retains_plan_and_retries_same_or_new_runtime()
    {
        foreach (bool restart in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var parent = await SeedExitGpu(fixture, "vf");
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindGpuJournal(journal);
            fixture.Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
            fixture.Provider.RecoverOnRequest = true;
            fixture.Provider.FailGpuReconcileAfterRecoveryOnce = true;
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
            Assert.AreEqual(parent, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            Assert.AreEqual(1, fixture.Provider.GpuRestorationWrites);
            if (restart)
            {
                journal = new(fixture.Root);
                runtime = new(fixture.Provider, journal: journal, requireClientSessions: true,
                    readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
                fixture.BindGpuJournal(journal);
            }
            Assert.AreEqual(CommandState.Applied, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
            Assert.AreEqual(1, fixture.Provider.GpuRestorationWrites);
            Assert.AreEqual(parent.ToString(), ExitGpuPlan(fixture)["operationId"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task Prior_exit_gpu_verified_native_target_is_read_only_and_never_rolled_back_on_verification_failure()
    {
        foreach (bool reject in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var parent = await SeedExitGpu(fixture, "vf");
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var target = saved.Unresolved.Single().GpuAttempt!;
            for (int index = 0; index < 127; index++) fixture.Provider.SetExternalVfOffset(index, target.VfOffsetsKhz[index]);
            var plan = ExitGpuPlan(fixture);
            plan["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "gpu.vfOffsetsKhz")!["status"] = "Verified";
            File.WriteAllText(Path.Combine(fixture.Root, "client-session-recovery.json"), plan.ToJsonString());
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindGpuJournal(journal);
            fixture.Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
            fixture.Provider.RejectGpuTargetVerification = reject;
            fixture.Provider.RecoverOnRequest = true;
            int recoveries = fixture.Provider.GpuRecoveries, writes = fixture.Provider.WriteCount;
            var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(reject ? CommandState.RecoveryRequired : CommandState.Applied, result!.State);
            Assert.IsTrue(fixture.Provider.GpuTargetVerifications > 0);
            Assert.AreEqual(recoveries, fixture.Provider.GpuRecoveries, "A Verified step must never roll back to its Before snapshot.");
            if (reject) Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(parent.ToString(), ExitGpuPlan(fixture)["operationId"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task Prior_exit_gpu_rejects_attempt_before_or_target_that_does_not_match_original_plan()
    {
        foreach (string fault in new[] { "before", "target" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedExitGpu(fixture, "vf");
            var plan = ExitGpuPlan(fixture);
            var step = plan["steps"]!.AsArray().Single(item => item!["key"]!.GetValue<string>() == "gpu.vfOffsetsKhz")!;
            step[fault == "before" ? "expected" : "target"] = JsonSerializer.SerializeToNode(SettingRequest.Set(new int[127].Select(_ => 70000).ToArray()));
            File.WriteAllText(Path.Combine(fixture.Root, "client-session-recovery.json"), plan.ToJsonString());
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindGpuJournal(journal);
            fixture.Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
            fixture.Provider.RecoverOnRequest = true;
            int recoveries = fixture.Provider.GpuRecoveries, writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(recoveries, fixture.Provider.GpuRecoveries);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
        }
    }
}
