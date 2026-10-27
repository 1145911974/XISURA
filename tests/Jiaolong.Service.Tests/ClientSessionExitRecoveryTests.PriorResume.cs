using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(ClientControlSessionHandle Handle, HomeServiceRuntime Runtime, Guid Plan)> FailedPriorExit(Fixture fixture)
    {
        var handle = await fixture.Begin(-30);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 }), handle);
        fixture.Provider.FailMemoryRestore = true;
        var runtime = Restarted(fixture);
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        return (handle, runtime, saved.RootElement.GetProperty("operationId").GetGuid());
    }

    [TestMethod]
    public async Task Prior_resume_keeps_original_plan_and_skips_verified_steps_in_the_same_runtime()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var prior = await FailedPriorExit(fixture);
        fixture.Provider.FailMemoryRestore = false;
        var writes = new List<string>();
        fixture.Provider.BeforeWrite = command => { writes.Add(command.GetType().Name); return Task.CompletedTask; };
        var result = await prior.Runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        CollectionAssert.AreEqual(new[] { "SetGpuMemoryOffsetCommand" }, writes);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(prior.Plan, plan.RootElement.GetProperty("operationId").GetGuid());
        var journal = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, journal.Unresolved.Length);
        Assert.AreEqual(fixture.Runtime.SessionId, journal.RecoveryScope!.ServiceSessionId);
        Assert.AreEqual(prior.Handle.HardwareEpoch, journal.Current!.HardwareEpoch);
    }

    [TestMethod]
    public async Task Prior_resume_after_another_service_restart_completes_the_original_parent_without_replaying_completed_steps()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var prior = await FailedPriorExit(fixture);
        fixture.Provider.FailMemoryRestore = false;
        var restarted = Restarted(fixture);
        var writes = new List<string>();
        fixture.Provider.BeforeWrite = command => { writes.Add(command.GetType().Name); return Task.CompletedTask; };
        var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        CollectionAssert.AreEqual(new[] { "SetGpuMemoryOffsetCommand" }, writes);
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(prior.Plan, plan.RootElement.GetProperty("operationId").GetGuid());
        Assert.IsTrue(plan.RootElement.GetProperty("steps").EnumerateArray().All(step => step.GetProperty("status").GetString() == "Verified"));
        var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner with { ProcessId = 43 }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreNotEqual(prior.Handle.SessionId, next.ClientSession!.SessionId);
        await AssertIdleFanReleaseExitRecovers();
    }

    [TestMethod]
    public async Task Prior_resume_rejects_missing_or_corrupt_plan_and_changed_restore_target_without_recreating_evidence()
    {
        await AssertIdleFanReleaseExitRejectsUnprovenEvidence();
        foreach (string damage in new[] { "missing", "corrupt", "target", "missing-step" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await FailedPriorExit(fixture);
            string path = Path.Combine(fixture.Root, "client-session-recovery.json");
            if (damage == "missing") File.Delete(path);
            if (damage == "corrupt") File.WriteAllText(path, "{}");
            if (damage == "target")
            {
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                var step = json["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "gpu.memoryOffsetKhz")!;
                step["target"]!["value"] = 1000;
                File.WriteAllText(path, json.ToJsonString());
            }
            if (damage == "missing-step")
            {
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                var steps = json["steps"]!.AsArray();
                steps.Remove(steps.Single(step => step!["key"]!.GetValue<string>() == "gpu.memoryOffsetKhz"));
                File.WriteAllText(path, json.ToJsonString());
            }
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.IsNotNull(result);
            Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }

    [TestMethod]
    public async Task Prior_resume_preserves_external_changes_to_pending_and_already_verified_steps()
    {
        foreach (bool verified in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await FailedPriorExit(fixture);
            fixture.Provider.FailMemoryRestore = false;
            fixture.Provider.ReadBaselineOverride = controls => verified ? controls with
                { KeyboardLighting = controls.KeyboardLighting! with { Red = 12 } } : controls with
                { GpuVf = controls.GpuVf! with { MemoryOffsetKhz = 2000 } };
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.IsNotNull(result);
            Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            Assert.AreEqual(verified ? "PartialExitLiveStateConflict:lighting" : "PartialExitLiveStateConflict:gpu.memoryOffsetKhz",
                result.Error!.Details["clientSessionRecovery"]);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }
}
