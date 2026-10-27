using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Keep_exit_persists_decision_before_closing_publication_and_restart_never_restores()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(-30);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        int writes = fixture.Provider.WriteCount;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessions = typeof(HomeServiceRuntime).GetField("clientSessions", flags)!.GetValue(fixture.Runtime)!;
        var inner = (AtomicJsonStore)sessions.GetType().GetField("store", flags)!.GetValue(sessions)!;
        typeof(AtomicJsonStore).GetField("beforePublish", flags)!.SetValue(inner,
            (Action)(() => throw new IOException("Simulated interruption before Closing publication.")));

        var result = await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId,
            fixture.Owner, CancellationToken.None, keepChanges: true);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        var decision = Path.Combine(fixture.Root, "client-session-recovery.json");
        Assert.IsTrue(File.Exists(decision), "Keep must be durable before publishing Closing.");
        using var plan = JsonDocument.Parse(File.ReadAllText(decision));
        Assert.IsTrue(plan.RootElement.GetProperty("keepChanges").GetBoolean());
        Assert.AreEqual(ClientControlSessionPhase.Active,
            (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);

        var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
        var recovered = await restarted.RecoverExitedClientSessionAsync(
            new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, recovered!.State, JsonSerializer.Serialize(recovered.Error));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());

        Assert.AreEqual(-5, fixture.Provider.Cpu.PerCoreCurveOptimizer![0]);
    }

    [TestMethod]
    public async Task Exit_drain_cancellation_returns_recovery_and_retry_keeps_stable_inflight_result()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(-30);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeWrite = async _ => { entered.TrySetResult(); await release.Task; };
        var write = fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var result = await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId,
                fixture.Owner, deadline.Token, keepChanges: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            Assert.IsFalse(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")),
                "A pending write cannot supply the retained snapshot.");
        }
        finally { release.TrySetResult(); await write; fixture.Provider.BeforeWrite = null; }
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
            handle.SessionId, fixture.Owner, CancellationToken.None, keepChanges: true)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        using var stalled = new Fixture();
        var stalledHandle = await stalled.Begin(-30);
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = stalled.Runtime.Execution.WithHardwareGateAsync(async () =>
            { gateEntered.TrySetResult(); await gateRelease.Task; return true; });
        await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var timedOut = await stalled.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), stalledHandle.SessionId,
                stalled.Owner, CancellationToken.None, keepChanges: true).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(CommandState.RecoveryRequired, timedOut.State);
            Assert.AreEqual("ExitDeadlineExceeded", timedOut.Error!.Details["clientSessionExit"]);
            Assert.IsFalse(stalled.Runtime.CanRunClientAutomation(stalled.Owner.UserSid));
            Assert.IsFalse(File.Exists(Path.Combine(stalled.Root, "client-session-recovery.json")));
        }
        finally { gateRelease.TrySetResult(); await held; }
        Assert.AreEqual(CommandState.Applied, (await stalled.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
            stalledHandle.SessionId, stalled.Owner, CancellationToken.None, keepChanges: true)).State);
    }

    [TestMethod]
    public async Task Freeze_read_failure_stays_closed_and_authenticated_exit_retry_completes()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(-30);
        var path = Path.Combine(fixture.Root, "client-control-session.json");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.FreezeClientSessionWritesAsync(
                Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.IsFalse(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(ClientControlSessionPhase.Completed,
            (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
    }
}
