using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Worker_handoff_restores_prior_scope_then_admits_only_the_new_client_lease()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(-30);
        foreach (HardwareCommand command in new HardwareCommand[]
        {
            new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true),
            new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true),
            new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 }),
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced)
        }) Assert.AreEqual(CommandState.Applied, (await fixture.Write(command, handle)).State);
        var runtime = RestartForPartialCpuEnd(fixture);
        int scopedWrites = 0;
        fixture.Provider.BeforeWrite = async _ =>
        {
            scopedWrites++;
            var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.AreEqual(fixture.Runtime.SessionId, saved.RecoveryScope!.ServiceSessionId);
            Assert.AreEqual(handle.HardwareEpoch, saved.Current!.HardwareEpoch);
            Assert.IsTrue(saved.Current.Fields.Values.All(field => field.ClientSessionId == handle.SessionId));
        };
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        var result = await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result?.State, result?.Error?.ToString());
        Assert.IsTrue(scopedWrites > 0);
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        fixture.Provider.BeforeWrite = null;
        int writes = fixture.Provider.WriteCount;
        Assert.IsNull(await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var nextOwner = fixture.Owner with { ProcessId = 43, ConnectionId = Guid.NewGuid() };
        var next = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), nextOwner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreNotEqual(handle.SessionId, next.ClientSession!.SessionId);
        Assert.AreNotEqual(handle.HardwareEpoch, next.ClientSession.HardwareEpoch);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var stale = await runtime.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false) { ClientSessionId = handle.SessionId },
            CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        Assert.AreEqual(CommandState.Rejected, stale.State);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, stale.Error!.Code);
        Assert.AreEqual(HardwareMutation.NoMutation, stale.Mutation);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(next.ClientSession.SessionId, null), nextOwner, CancellationToken.None)).State);
        var fresh = await runtime.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false) { ClientSessionId = next.ClientSession.SessionId },
            CancellationToken.None, nextOwner.ConnectionId, nextOwner.UserSid, nextOwner);
        Assert.AreEqual(CommandState.Applied, fresh.State);
        Assert.IsFalse(fixture.Provider.Light.LogoEnabled);
        Assert.AreEqual(CommandState.Applied, (await runtime.EndClientControlSessionAsync(Guid.NewGuid(),
            next.ClientSession.SessionId, nextOwner, CancellationToken.None)).State);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    [TestMethod]
    public async Task Worker_handoff_keeps_live_unknown_external_and_power_conflicts_zero_write()
    {
        foreach (string condition in new[] { "running", "unknown", "external", "power" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
            if (condition == "external") fixture.Provider.SetExternalVfOffset(17, 12345);
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var runtime = Restarted(fixture, power: condition == "power"
                ? _ => Task.FromResult<PowerContinuityEvidence?>(new(400, 300)) : ReadRestartPower);
            var result = await runtime.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = condition == "running" ? ClientProcessState.Running : condition == "unknown" ? ClientProcessState.Unknown : ClientProcessState.Exited },
                CancellationToken.None);
            if (condition == "running") Assert.IsNull(result);
            else Assert.AreEqual(CommandState.RecoveryRequired, result?.State, condition);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, condition);
            AssertOriginalRecords(fixture, original);
        }
    }

    [TestMethod]
    public async Task Worker_handoff_failed_restore_keeps_the_exit_parent_and_retries_without_replaying_verified_steps()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 }), handle);
        var runtime = RestartForPartialCpuEnd(fixture);
        fixture.Provider.FailMemoryRestore = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))?.State);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        var before = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(1, before.Unresolved.Length);
        var exitId = before.Unresolved[0].OperationId;
        Assert.AreEqual(fixture.Runtime.SessionId, before.RecoveryScope!.ServiceSessionId);
        int lightWrites = 0;
        fixture.Provider.BeforeWrite = command => { if (command is SetKeyboardLightingCommand) lightWrites++; return Task.CompletedTask; };
        fixture.Provider.FailMemoryRestore = false;
        Assert.AreEqual(CommandState.Applied, (await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))?.State);
        Assert.AreEqual(0, lightWrites);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        var after = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, after.Unresolved.Length);
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(exitId, plan.RootElement.GetProperty("operationId").GetGuid());
        Assert.AreEqual(0, after.Current!.Fields.Count);
    }

    [TestMethod]
    public async Task Worker_handoff_hosted_loop_recovers_the_prior_capture_and_stops_without_reapplying()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetLidLogoCommand(Guid.NewGuid(), false), handle);
        var runtime = RestartForPartialCpuEnd(fixture);
        using var worker = new ClientSessionRecoveryWorker(runtime, new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, NullLogger<ClientSessionRecoveryWorker>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await worker.StartAsync(deadline.Token);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    using var stream = new FileStream(Path.Combine(fixture.Root, "client-control-session.json"),
                        FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    using var saved = JsonDocument.Parse(stream);
                    if (saved.RootElement.GetProperty("phase").GetString() == "Completed") break;
                }
                catch (FileNotFoundException)
                {
                    // Observe backup rotation without locking the worker's rename; only Completed finishes the wait.
                }
                await Task.Delay(25, deadline.Token);
            }
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.IsTrue(worker.ExecuteTask!.IsCompleted);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        int writes = fixture.Provider.WriteCount;
        Assert.IsNull(await runtime.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }
}
