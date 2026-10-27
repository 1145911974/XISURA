using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Cancelled_or_timed_out_execute_late_reply_does_not_complete_next_same_pipe_query_or_resend()
    {
        foreach (bool timeout in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            string name = "Xisura-late-reply-" + Guid.NewGuid().ToString("N");
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await using var client = new ControlCenterClient(name);
            var requests = new List<RequestEnvelope>();

            async Task<MessageEnvelope> ReadAsync()
            {
                var prefix = new byte[4];
                await server.ReadExactlyAsync(prefix, deadline.Token);
                var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
                await server.ReadExactlyAsync(bytes, deadline.Token);
                var message = JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.MessageEnvelope)!;
                if (message is RequestEnvelope request) requests.Add(request);
                return message;
            }

            async Task WriteAsync(MessageEnvelope envelope)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.MessageEnvelope);
                var prefix = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
                await server.WriteAsync(prefix, deadline.Token);
                await server.WriteAsync(bytes, deadline.Token);
            }

            ResponseEnvelope Reply(RequestEnvelope request, JsonElement payload) =>
                new(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow, request.MessageId,
                    request.OperationId, ResponseStatus.Success, payload, null);

            var connecting = client.ConnectAsync(deadline.Token);
            await server.WaitForConnectionAsync(deadline.Token);
            var hello = (HelloEnvelope)await ReadAsync();
            await WriteAsync(new HelloAckEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow,
                hello.MessageId, "late-reply-test"));
            await connecting;
            await using var telemetry = client.SubscribeTelemetryAsync(deadline.Token).GetAsyncEnumerator();
            var markerRead = telemetry.MoveNextAsync().AsTask();
            Assert.AreEqual("subscribeTelemetry", ((RequestEnvelope)await ReadAsync()).Operation);

            var command = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo);
            var execute = client.SendCommandAsync(command, caller.Token);
            var a = (RequestEnvelope)await ReadAsync();
            Assert.AreEqual("executeCommand", a.Operation);
            Assert.AreEqual(command.OperationId, a.OperationId);
            Assert.AreEqual(command.OperationId, a.Payload.Deserialize(ProtocolJsonContext.Default.HardwareCommand)!.OperationId);
            if (!timeout) caller.Cancel();
            try
            {
                await execute;
                Assert.Fail("A must stop waiting without receiving its reply.");
            }
            catch (TimeoutException) when (timeout) { }
            catch (OperationCanceledException) when (!timeout && caller.IsCancellationRequested) { }
            Assert.IsFalse(deadline.IsCancellationRequested);

            var queryB = client.GetHomeStateAsync(deadline.Token);
            var b = (RequestEnvelope)await ReadAsync();
            Assert.AreEqual("getDeviceState", b.Operation);
            Assert.AreNotEqual(a.MessageId, b.MessageId);
            var late = Reply(a, JsonSerializer.SerializeToElement(
                new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false),
                ProtocolJsonContext.Default.CommandResult));
            await WriteAsync(late);

            async Task AssertStillPendingAsync(RequestEnvelope request, Task<HomeStateSnapshot> query, ulong sequence)
            {
                var marker = new HardwareSnapshot(DateTimeOffset.UtcNow, "after-late-" + sequence, 42, 40, 12, 8);
                await WriteAsync(new EventEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow,
                    "telemetry.updated", sequence, JsonSerializer.SerializeToElement(marker, ProtocolJsonContext.Default.HardwareSnapshot)));
                Assert.IsTrue(await markerRead.WaitAsync(deadline.Token));
                Assert.AreEqual(marker, telemetry.Current);
                // FIFO telemetry proves the reader has passed the late reply; inspect the TCS too,
                // since the public async method's continuation might not have run yet.
                var pending = (IDictionary<Guid, TaskCompletionSource<MessageEnvelope>>)typeof(ControlCenterClient)
                    .GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
                Assert.IsTrue(pending.TryGetValue(request.MessageId, out var completion));
                Assert.IsFalse(completion.Task.IsCompleted);
                Assert.IsFalse(pending.ContainsKey(a.MessageId));
                Assert.IsFalse(query.IsCompleted);
                Assert.IsTrue(client.IsConnected);
            }

            await AssertStillPendingAsync(b, queryB, 1);
            var expectedB = CreateState(PerformanceMode.Balanced);
            await WriteAsync(Reply(b, JsonSerializer.SerializeToElement(expectedB, ProtocolJsonContext.Default.HomeStateSnapshot)));
            Assert.AreEqual(expectedB.Controls.PerformanceMode, (await queryB.WaitAsync(deadline.Token)).Controls.PerformanceMode);

            markerRead = telemetry.MoveNextAsync().AsTask();
            var queryC = client.GetHomeStateAsync(deadline.Token);
            var c = (RequestEnvelope)await ReadAsync();
            Assert.AreEqual("getDeviceState", c.Operation);
            Assert.AreNotEqual(b.MessageId, c.MessageId);
            await WriteAsync(late);
            await AssertStillPendingAsync(c, queryC, 2);
            var expectedC = CreateState(PerformanceMode.Quiet);
            await WriteAsync(Reply(c, JsonSerializer.SerializeToElement(expectedC, ProtocolJsonContext.Default.HomeStateSnapshot)));
            Assert.AreEqual(expectedC.Controls.PerformanceMode, (await queryC.WaitAsync(deadline.Token)).Controls.PerformanceMode);
            Assert.AreEqual(4, requests.Count);
            Assert.AreEqual(1, requests.Count(request => request.Operation == "executeCommand"));
            Assert.AreEqual(2, requests.Count(request => request.Operation == "getDeviceState"));
        }
    }
}
