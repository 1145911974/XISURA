using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Protocol;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ServiceConnectionStatusTests
{
    [TestMethod]
    public async Task Connected_pipe_stays_connected_when_device_is_readonly_or_needs_repair()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string name = "xisura-support-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new ControlCenterClient(name);
        await using var session = new HomeControlSession(client);
        Assert.IsFalse(session.IsServiceConnected);
        var handshake = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var header = new byte[4];
            await server.ReadExactlyAsync(header, deadline.Token);
            var payload = new byte[BitConverter.ToInt32(header)];
            await server.ReadExactlyAsync(payload, deadline.Token);
            var hello = (HelloEnvelope)JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.MessageEnvelope)!;
            var ack = JsonSerializer.SerializeToUtf8Bytes<MessageEnvelope>(new HelloAckEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, hello.MessageId, "test"), ProtocolJsonContext.Default.MessageEnvelope);
            await server.WriteAsync(BitConverter.GetBytes(ack.Length), deadline.Token);
            await server.WriteAsync(ack, deadline.Token);
            await server.FlushAsync(deadline.Token);
        }, deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await handshake;
        foreach (var status in new[] { HomeSessionStatus.Connected, HomeSessionStatus.ReadOnly, HomeSessionStatus.RepairRequired })
        {
            typeof(HomeControlSession).GetField("status", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, status);
            Assert.IsTrue(session.IsServiceConnected, status.ToString());
        }
        await server.DisposeAsync();
        while (session.IsServiceConnected && !deadline.IsCancellationRequested)
            await Task.Delay(10, deadline.Token);
        Assert.IsFalse(session.IsServiceConnected);
    }
}
