using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Lifecycle_over_real_pipes_restores_all_pages_and_reopens_with_a_fresh_baseline()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new LifecyclePipeHost(fixture);
        await using var main = await host.ConnectAsync(deadline.Token);
        await using var peer = await host.ConnectAsync(deadline.Token);
        var lifecycle = PipeLifecycle(main, peer, -30);
        await Task.WhenAll(lifecycle.StartAsync(deadline.Token), lifecycle.StartAsync(deadline.Token));
        Assert.AreEqual(0, fixture.Provider.WriteCount, "Opening and configuring the console must not tune hardware.");
        Assert.AreEqual(1, main.Operations.Count(operation => operation == "beginClientSession"));
        Assert.AreEqual(1, main.Operations.Count(operation => operation == "configureClientSession"));
        var first = ReadCapturedSession(fixture);
        Assert.AreEqual("Active", first.GetProperty("phase").GetString());
        var firstId = first.GetProperty("handle").GetProperty("sessionId").GetGuid();

        foreach (HardwareCommand command in new HardwareCommand[]
        {
            new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true),
            new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true),
            new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12, LogoEnabled = false }),
            new SetStrongCoolingCommand(Guid.NewGuid(), true),
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced)
        }) Assert.AreEqual(CommandState.Applied, (await lifecycle.SendAsync(command, peer.SendAsync, deadline.Token)).State);
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.IsTrue(fixture.Provider.Controls.StrongCooling);
        int writes = fixture.Provider.WriteCount;
        await main.DisposeAsync();
        Assert.IsTrue(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid), "Losing one pipe must not mean full client exit.");
        Assert.AreEqual(writes, fixture.Provider.WriteCount);

        Assert.AreEqual(CommandState.Applied, (await lifecycle.EndAsync(deadline.Token))!.State);
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(4700, fixture.Provider.Cpu.DcFrequency());
        CollectionAssert.AreEqual(Enumerable.Repeat(-30, 8).ToArray(), fixture.Provider.Cpu.PerCoreCurveOptimizer!.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.IsFalse(fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.Control);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.CpuTarget);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.GpuTarget);
        Assert.AreEqual("Completed", ReadCapturedSession(fixture).GetProperty("phase").GetString());
        var journal = await fixture.Runtime.ReadControlJournalAsync(deadline.Token);
        Assert.AreEqual(0, journal!.Current!.Fields.Count);
        Assert.AreEqual(0, journal.Unresolved.Length);
        writes = fixture.Provider.WriteCount;
        await lifecycle.EndAsync(deadline.Token);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(1, peer.Operations.Count(operation => operation == "endClientSession"));

        fixture.Provider.SetExternalAcFrequency(4400);
        var reopened = PipeLifecycle(peer, peer, null);
        await reopened.StartAsync(deadline.Token);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreNotEqual(firstId, ReadCapturedSession(fixture).GetProperty("handle").GetProperty("sessionId").GetGuid());
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "completed-client-sessions", firstId.ToString("N") + ".json")));
        Assert.AreEqual(CommandState.Applied, (await reopened.SendAsync(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4300, -4), true), peer.SendAsync, deadline.Token)).State);
        Assert.AreEqual(CommandState.Applied, (await reopened.EndAsync(deadline.Token))!.State);
        Assert.AreEqual(4400, fixture.Provider.Cpu.AcFrequency(), "A new process must recover its own startup value, not the older session's baseline.");
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
    }

    [TestMethod]
    public async Task Lifecycle_over_real_pipes_drains_writes_and_keeps_failed_exit_retryable()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new LifecyclePipeHost(fixture);
        await using var main = await host.ConnectAsync(deadline.Token);
        await using var peer = await host.ConnectAsync(deadline.Token);
        var lifecycle = PipeLifecycle(main, peer, null);
        await lifecycle.StartAsync(deadline.Token);
        Assert.AreEqual(CommandState.Applied, (await lifecycle.SendAsync(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), peer.SendAsync, deadline.Token)).State);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeWrite = async command =>
        {
            if (command is SetKeyboardLightingCommand { Plan.Red: 12 })
            { entered.TrySetResult(); await release.Task.WaitAsync(deadline.Token); }
        };
        var pending = lifecycle.SendAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 }), main.SendAsync, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            fixture.Provider.FailMemoryRestore = true;
            var end = lifecycle.EndAsync(deadline.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SendAsync(new SetLidLogoCommand(Guid.NewGuid(), false), peer.SendAsync, deadline.Token));
            Assert.AreEqual(0, peer.Operations.Count(operation => operation == "endClientSession"));
            release.TrySetResult();
            Assert.AreEqual(CommandState.Applied, (await pending).State);
            Assert.AreEqual(CommandState.RecoveryRequired, (await end)!.State);
            Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
            Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
            Assert.AreEqual("Closing", ReadCapturedSession(fixture).GetProperty("phase").GetString());
            var retained = await fixture.Runtime.ReadControlJournalAsync(deadline.Token);
            Assert.IsTrue(retained!.Current!.Fields.ContainsKey("gpu.memoryOffsetKhz"));
            Assert.IsTrue(retained.Unresolved.Length > 0);
            Assert.IsFalse(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
            fixture.Provider.FailMemoryRestore = false;
            Assert.AreEqual(CommandState.Applied, (await lifecycle.EndAsync(deadline.Token))!.State);
            Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
            Assert.AreEqual("Completed", ReadCapturedSession(fixture).GetProperty("phase").GetString());
            Assert.AreEqual(2, peer.Operations.Count(operation => operation == "endClientSession"));
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task Lifecycle_pipe_disconnect_waits_for_confirmed_process_exit_and_retries_original_recovery()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new LifecyclePipeHost(fixture);
        await using var pipe = await host.ConnectAsync(deadline.Token);
        var lifecycle = PipeLifecycle(pipe, pipe, -30);
        await lifecycle.StartAsync(deadline.Token);
        Assert.AreEqual(CommandState.Applied, (await lifecycle.SendAsync(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), pipe.SendAsync, deadline.Token)).State);
        Assert.AreEqual(CommandState.Applied, (await lifecycle.SendAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true), pipe.SendAsync, deadline.Token)).State);
        var session = ReadCapturedSession(fixture).GetProperty("handle").GetProperty("sessionId").GetGuid();
        await pipe.DisposeAsync();
        int writes = fixture.Provider.WriteCount;
        var probe = new ProcessProbe(fixture.Owner);
        Assert.IsNull(await fixture.Runtime.RecoverExitedClientSessionAsync(probe, deadline.Token));
        probe.State = ClientProcessState.Unknown;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, deadline.Token))!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual("Active", ReadCapturedSession(fixture).GetProperty("phase").GetString());
        fixture.Provider.FailMemoryRestore = true;
        probe.State = ClientProcessState.Exited;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, deadline.Token))!.State);
        Assert.AreEqual(session, ReadCapturedSession(fixture).GetProperty("handle").GetProperty("sessionId").GetGuid());
        Assert.IsFalse(fixture.Provider.Controls.StrongCooling);
        fixture.Provider.FailMemoryRestore = false;
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, deadline.Token))!.State);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        writes = fixture.Provider.WriteCount;
        Assert.IsNull(await fixture.Runtime.RecoverExitedClientSessionAsync(probe, deadline.Token));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    private static JsonElement ReadCapturedSession(Fixture fixture) =>
        JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));

    private static ClientControlSessionLifecycle PipeLifecycle(LifecyclePipe begin, LifecyclePipe end, int? curve) => new(
        token => begin.CallAsync("beginClientSession", JsonSerializer.SerializeToElement(new { }), token),
        (configuration, token) => begin.CallAsync("configureClientSession", JsonSerializer.SerializeToElement(configuration,
            ProtocolJsonContext.Default.ClientSessionExitConfiguration), token),
        (sessionId, token) => end.CallAsync("endClientSession", JsonSerializer.SerializeToElement(new { sessionId }), token),
        key => curve is int value ? new ExitDeviceDefaults(1, key, value) : null);

    private sealed class LifecyclePipeHost : IAsyncDisposable
    {
        private readonly NamedPipeServiceHost host;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task running;
        private int disposed;

        public LifecyclePipeHost(Fixture fixture) : this(fixture.Runtime, fixture.Owner,
            "Xisura-lifecycle-" + Guid.NewGuid().ToString("N")) { }

        public LifecyclePipeHost(HomeServiceRuntime runtime, ClientIdentity owner, string pipeName)
        {
            host = new NamedPipeServiceHost(pipeName, homeRuntime: runtime, identityVerifier: new LifecycleVerifier(owner));
            running = host.RunAsync(lifetime.Token);
        }

        public async Task<LifecyclePipe> ConnectAsync(CancellationToken token)
        {
            var pipe = new LifecyclePipe(await host.ConnectClientAsync(token));
            await pipe.HelloAsync(token);
            return pipe;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            await host.DisposeAsync();
            try { await running; } catch (OperationCanceledException) { }
            lifetime.Dispose();
        }
    }

    private sealed class LifecycleVerifier(ClientIdentity owner) : IClientIdentityVerifier
    {
        public ValueTask<ClientIdentity> VerifyAsync(SafePipeHandle handle, CancellationToken token) => ValueTask.FromResult(owner);
    }

    private sealed class LifecyclePipe(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly FrameCodec codec = new();
        public List<string> Operations { get; } = [];

        public async Task HelloAsync(CancellationToken token)
        {
            var hello = new HelloEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow, "lifecycle-integration");
            await codec.WriteAsync(pipe, hello, token);
            var response = (HelloAckEnvelope)await codec.ReadAsync(pipe, token);
            Assert.AreEqual(hello.MessageId, response.ReplyToMessageId);
            Assert.IsTrue(response.ServiceInstanceId is Guid id && id != Guid.Empty);
        }

        public Task<CommandResult> SendAsync(HardwareCommand command, CancellationToken token) =>
            CallAsync("executeCommand", JsonSerializer.SerializeToElement(command, ProtocolJsonContext.Default.HardwareCommand), token, command.OperationId);

        public async Task<CommandResult> CallAsync(string operation, JsonElement payload, CancellationToken token, Guid? operationId = null)
        {
            var request = new RequestEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow, operationId ?? Guid.NewGuid(),
                operation, DateTimeOffset.UtcNow.AddSeconds(25), payload);
            Operations.Add(operation);
            await codec.WriteAsync(pipe, request, token);
            var response = (ResponseEnvelope)await codec.ReadAsync(pipe, token);
            Assert.AreEqual(request.MessageId, response.ReplyToMessageId);
            Assert.IsNull(response.Error, response.Error?.ToString());
            return response.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!;
        }

        public ValueTask DisposeAsync() => pipe.DisposeAsync();
    }
}
