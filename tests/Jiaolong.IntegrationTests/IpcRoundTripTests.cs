using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Ipc;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class IpcRoundTripTests
{
    [TestMethod]
    public async Task Local_named_pipe_completes_v1_hello_round_trip()
    {
        var pipeName = $"Jiaolong.ControlCenter.test.{Guid.NewGuid():N}";
        await using var host = new NamedPipeServiceHost(pipeName);
        var server = host.AcceptOneAsync(CancellationToken.None);
        await using var client = await host.ConnectClientAsync(CancellationToken.None);
        var codec = new FrameCodec();
        var hello = new HelloEnvelope(new ProtocolVersion(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, "integration-test");
        try
        {
            await codec.WriteAsync(client, hello, CancellationToken.None);
        }
        catch
        {
            await server;
            throw;
        }
        var ack = await codec.ReadAsync(client, CancellationToken.None);

        Assert.IsInstanceOfType<HelloAckEnvelope>(ack);
        await server;
    }
}
