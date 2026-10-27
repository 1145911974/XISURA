using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Diagnostics;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class NamedPipeServiceHostTests
{
    [TestMethod]
    public async Task Running_service_retains_telemetry_without_a_subscriber()
    {
        var buffer = new TelemetryRingBuffer();
        var host = new NamedPipeServiceHost(
            $"Jiaolong.Test.{Guid.NewGuid():N}",
            homeRuntime: new HomeServiceRuntime(new TelemetryProvider()),
            telemetryRingBuffer: buffer);
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(2300));
            Assert.IsTrue(buffer.Snapshot().Count >= 2, "The diagnostic history must fill while the client is closed.");
        }
        finally
        {
            cancellation.Cancel();
            await host.DisposeAsync();
            try { await hostTask; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task A_persistent_client_does_not_block_the_next_connection()
    {
        var pipeName = $"Jiaolong.Test.{Guid.NewGuid():N}";
        var dispatcher = new BlockingDispatcher();
        var host = new NamedPipeServiceHost(
            pipeName,
            dispatcher: dispatcher,
            identityVerifier: new AllowLocalClientVerifier());
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);

        try
        {
            Guid? instance = null;
            void CheckInstance(Guid value) { instance ??= value; Assert.AreEqual(instance.Value, value); }
            await using var first = await ConnectAndHelloAsync(host, CheckInstance);
            await using var second = await ConnectAndHelloAsync(host, CheckInstance);
            var codec = new FrameCodec();
            RequestEnvelope Request(string operation) => new(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow,
                Guid.NewGuid(), operation, DateTimeOffset.UtcNow.AddSeconds(5), JsonSerializer.SerializeToElement(true));
            var blocked = Request("blocked");
            var latest = Request("latest");
            await codec.WriteAsync(first, blocked, cancellation.Token);
            await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await codec.WriteAsync(first, latest, cancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = (ResponseEnvelope)await codec.ReadAsync(first, deadline.Token);
            Assert.AreEqual(latest.MessageId, response.ReplyToMessageId, "A new intent must be admitted before the old operation completes.");
            Assert.IsFalse(dispatcher.Release.Task.IsCompleted);
            dispatcher.Release.TrySetResult();
            response = (ResponseEnvelope)await codec.ReadAsync(first, deadline.Token);
            Assert.AreEqual(blocked.MessageId, response.ReplyToMessageId);
        }
        finally
        {
            cancellation.Cancel();
            await host.DisposeAsync();
            try { await hostTask; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task Telemetry_subscription_uses_the_requested_half_second_interval()
    {
        var pipeName = $"Jiaolong.Test.{Guid.NewGuid():N}";
        var host = new NamedPipeServiceHost(
            pipeName,
            homeRuntime: new HomeServiceRuntime(new TelemetryProvider()),
            identityVerifier: new AllowLocalClientVerifier());
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);

        try
        {
            await using var client = await ConnectAndHelloAsync(host);
            var codec = new FrameCodec();
            var subscribe = new RequestEnvelope(
                new ProtocolVersion(1, 0),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Guid.Empty,
                "subscribeTelemetry",
                DateTimeOffset.UtcNow.AddSeconds(5),
                JsonSerializer.SerializeToElement(new { intervalSeconds = 0.5 }));
            await codec.WriteAsync(client, subscribe, CancellationToken.None);
            _ = await codec.ReadAsync(client, CancellationToken.None);

            var first = (EventEnvelope)await codec.ReadAsync(client, CancellationToken.None);
            var second = (EventEnvelope)await codec.ReadAsync(client, CancellationToken.None);
            var elapsed = second.SentAtUtc - first.SentAtUtc;

            Assert.AreEqual("telemetry.updated", second.EventName);
            Assert.IsTrue(elapsed >= TimeSpan.FromMilliseconds(350), $"interval was {elapsed.TotalMilliseconds:0} ms");
            Assert.IsTrue(elapsed <= TimeSpan.FromMilliseconds(900), $"interval was {elapsed.TotalMilliseconds:0} ms");
        }
        finally
        {
            cancellation.Cancel();
            await host.DisposeAsync();
            try { await hostTask; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task Normal_disconnect_is_silent_but_a_truncated_frame_is_logged()
    {
        foreach (var truncated in new[] { false, true })
        {
            var logger = new FrameWarningLogger();
            await using var host = new NamedPipeServiceHost(
                $"Jiaolong.Test.{Guid.NewGuid():N}",
                identityVerifier: new AllowLocalClientVerifier(), logger: logger);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var accepting = host.AcceptOneAsync(cancellation.Token);
            await using (var client = await host.ConnectClientAsync(cancellation.Token))
            {
                if (truncated)
                    await client.WriteAsync(new byte[] { 4 }, cancellation.Token);
            }
            await accepting.WaitAsync(cancellation.Token);
            Assert.AreEqual(truncated ? 1 : 0, logger.Messages.Count);
            if (truncated)
                StringAssert.Contains(logger.Messages[0], "malformed or unsupported pipe frame: invalidFrame");
        }
    }

    private sealed class FrameWarningLogger : ILogger<NamedPipeServiceHost>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAndHelloAsync(NamedPipeServiceHost host, Action<Guid>? observeInstance = null)
    {
        var client = await host.ConnectClientAsync(CancellationToken.None);
        var codec = new FrameCodec();
        var hello = new HelloEnvelope(
            new ProtocolVersion(1, 0),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "test-client");

        await codec.WriteAsync(client, hello, CancellationToken.None);
        var response = await codec.ReadAsync(client, CancellationToken.None);

        Assert.IsInstanceOfType<HelloAckEnvelope>(response);
        Assert.AreEqual(hello.MessageId, ((HelloAckEnvelope)response).ReplyToMessageId);
        Assert.IsTrue(((HelloAckEnvelope)response).ServiceInstanceId is Guid instance && instance != Guid.Empty);
        observeInstance?.Invoke(((HelloAckEnvelope)response).ServiceInstanceId!.Value);
        return client;
    }

    private sealed class AllowLocalClientVerifier : IClientIdentityVerifier
    {
        public ValueTask<ClientIdentity> VerifyAsync(SafePipeHandle pipeHandle, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ClientIdentity.LocalAdministrator("S-1-5-21-test", 1));
    }

    private sealed class BlockingDispatcher : IRequestDispatcher
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, ClientIdentity client, CancellationToken token)
        {
            if (request.Operation == "blocked") { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new(request.ProtocolVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, request.MessageId,
                request.OperationId, ResponseStatus.Success, JsonSerializer.SerializeToElement(true), null);
        }
    }

    private sealed class TelemetryProvider : IHomeHardwareProvider
    {
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 40, 50, 20, 50));

        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
