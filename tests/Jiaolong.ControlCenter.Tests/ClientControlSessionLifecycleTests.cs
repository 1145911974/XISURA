using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ClientControlSessionLifecycleTests
{
    private static readonly ClientControlSessionHandle Handle = new(Guid.NewGuid(), 1, new string('A', 64), DateTimeOffset.UtcNow);
    private static CommandResult Applied() => new(Guid.NewGuid(), CommandState.Applied, null, RequiredUserAction.None, null, false);

    [TestMethod]
    public async Task Start_configures_before_any_command_and_shares_handle_across_senders()
    {
        var events = new List<string>();
        var lifecycle = new ClientControlSessionLifecycle(_ => { events.Add("begin"); return Task.FromResult(Applied() with { ClientSession = Handle }); },
            (configuration, _) => { Assert.AreEqual(Handle.SessionId, configuration.SessionId); Assert.IsNull(configuration.DeviceDefaults);
                events.Add("configure"); return Task.FromResult(Applied()); }, (_, _) => Task.FromResult(Applied()), key => null);
        Task<CommandResult> Send(HardwareCommand command, CancellationToken token)
        { Assert.AreEqual(Handle.SessionId, command.ClientSessionId); events.Add("write"); return Task.FromResult(Applied()); }
        var command = new SetLidLogoCommand(Guid.NewGuid(), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SendAsync(command, Send, CancellationToken.None));
        await Task.WhenAll(lifecycle.StartAsync(CancellationToken.None), lifecycle.StartAsync(CancellationToken.None));
        await lifecycle.SendAsync(command, Send, CancellationToken.None);
        await lifecycle.SendAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), Send, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "begin", "configure", "write", "write" }, events);
    }

    [TestMethod]
    public async Task Configuration_failure_retries_frozen_handle_and_never_sends()
    {
        int begins = 0, configs = 0, writes = 0;
        var lifecycle = new ClientControlSessionLifecycle(_ => { begins++; return Task.FromResult(Applied() with { ClientSession = Handle }); },
            (_, _) => Task.FromResult(++configs == 1 ? Applied() with { State = CommandState.Rejected } : Applied()),
            (_, _) => Task.FromResult(Applied()), _ => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SendAsync(new SetLidLogoCommand(Guid.NewGuid(), true),
            (_, _) => { writes++; return Task.FromResult(Applied()); }, CancellationToken.None));
        await lifecycle.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, begins); Assert.AreEqual(2, configs); Assert.AreEqual(0, writes);
    }

    [TestMethod]
    public async Task End_drains_inflight_rejects_new_writes_and_can_retry_failure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ends = 0;
        var lifecycle = new ClientControlSessionLifecycle(_ => Task.FromResult(Applied() with { ClientSession = Handle }),
            (_, _) => Task.FromResult(Applied()), (_, _) => Task.FromResult(++ends == 1
                ? Applied() with { State = CommandState.RecoveryRequired } : Applied()), _ => null);
        await lifecycle.StartAsync(CancellationToken.None);
        var command = new SetLidLogoCommand(Guid.NewGuid(), true);
        var pending = lifecycle.SendAsync(command, async (_, _) => { entered.SetResult(); await release.Task; return Applied(); }, CancellationToken.None);
        await entered.Task;
        var end = lifecycle.EndAsync(CancellationToken.None);
        Assert.AreEqual(0, ends);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SendAsync(command, (_, _) => Task.FromResult(Applied()), CancellationToken.None));
        release.SetResult(); await pending;
        Assert.AreEqual(CommandState.RecoveryRequired, (await end)!.State);
        Assert.AreEqual(CommandState.Applied, (await lifecycle.EndAsync(CancellationToken.None))!.State);
        await lifecycle.EndAsync(CancellationToken.None);
        Assert.AreEqual(2, ends);
    }
}
