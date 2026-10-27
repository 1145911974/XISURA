using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Unedited_capture_cannot_configure_a_preparing_session_after_its_journal_write_fails()
    {
        using var fixture = new Fixture();
        fixture.Provider.ReadBaselineOverride = baseline =>
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, "control-execution.json"));
            return baseline;
        };
        int writes = fixture.Provider.WriteCount;
        var failed = await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, failed.State);
        fixture.Provider.ReadBaselineOverride = null;
        var retry = await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, retry.State);
        string before = File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json"));
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(retry.ClientSession!.SessionId, null), fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Unedited_capture_persists_its_own_recovery_scope_before_and_after_completed_session_handoff()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.SetExternalVfOffset(60, 0);
        var runtime = fixture.Runtime;
        for (int round = 0; round < 2; round++)
        {
            int writes = fixture.Provider.WriteCount;
            var begin = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, begin.State);
            var handle = begin.ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                new(handle.SessionId, null), fixture.Owner, CancellationToken.None)).State);
            var captured = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.IsNotNull(captured.Current, "A session with no parameter edit still needs durable recovery authority.");
            Assert.AreEqual(handle.HardwareEpoch, captured.Current.HardwareEpoch);
            Assert.IsNotNull(captured.RecoveryScope);
            Assert.AreEqual(runtime.SessionId, captured.RecoveryScope.ServiceSessionId);
            Assert.AreEqual(0, captured.Current.Fields.Count);
            Assert.AreEqual(0, captured.Unresolved.Length);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);

            var restarted = Restarted(fixture);
            Assert.AreEqual(CommandState.Applied, (await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            var completed = ReadCapturedSession(fixture);
            Assert.AreEqual("Completed", completed.GetProperty("phase").GetString());
            Assert.AreEqual(handle.HardwareEpoch, completed.GetProperty("handle").GetProperty("hardwareEpoch").GetInt64());
            Assert.AreEqual(handle.SessionId, completed.GetProperty("handle").GetProperty("sessionId").GetGuid());
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            runtime = restarted;
        }
    }
}
