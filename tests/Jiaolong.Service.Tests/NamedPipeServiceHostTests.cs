using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class NamedPipeServiceHostTests
{
    [TestMethod]
    public async Task A_persistent_client_does_not_block_the_next_connection()
    {
        var pipeName = $"Jiaolong.Test.{Guid.NewGuid():N}";
        var host = new NamedPipeServiceHost(
            pipeName,
            identityVerifier: new AllowLocalClientVerifier());
        using var cancellation = new CancellationTokenSource();
        var hostTask = host.RunAsync(cancellation.Token);

        try
        {
            await using var first = await ConnectAndHelloAsync(host);
            await using var second = await ConnectAndHelloAsync(host);
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

    private static async Task<NamedPipeClientStream> ConnectAndHelloAsync(NamedPipeServiceHost host)
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
        return client;
    }

    private sealed class AllowLocalClientVerifier : IClientIdentityVerifier
    {
        public ValueTask<ClientIdentity> VerifyAsync(SafePipeHandle pipeHandle, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ClientIdentity.LocalInteractive("S-1-5-21-test", 1));
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
