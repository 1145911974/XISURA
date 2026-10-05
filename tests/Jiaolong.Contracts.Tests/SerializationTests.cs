using System.Text.Json;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Contracts.Tests;

[TestClass]
public sealed class SerializationTests
{
    [TestMethod]
    public void All_v1_envelope_kinds_round_trip_without_runtime_type_names()
    {
        var protocol = new ProtocolVersion(1, 0);
        var timestamp = DateTimeOffset.Parse("2026-08-21T00:00:00Z");
        var envelopes = new MessageEnvelope[]
        {
            new HelloEnvelope(protocol, Guid.NewGuid(), timestamp, "test"),
            new HelloAckEnvelope(protocol, Guid.NewGuid(), timestamp, Guid.NewGuid(), "service"),
            new RequestEnvelope(protocol, Guid.NewGuid(), timestamp, Guid.NewGuid(), "read", timestamp, JsonDocument.Parse("{}").RootElement.Clone()),
            new ResponseEnvelope(protocol, Guid.NewGuid(), timestamp, Guid.NewGuid(), null, ResponseStatus.Error, null,
                ServiceError.Create(ErrorCode.ValidationFailed, Guid.NewGuid(), false)),
            new EventEnvelope(protocol, Guid.NewGuid(), timestamp, "telemetry", 1, JsonDocument.Parse("{}").RootElement.Clone()),
            new CancelEnvelope(protocol, Guid.NewGuid(), timestamp, Guid.NewGuid()),
            new PingEnvelope(protocol, Guid.NewGuid(), timestamp, 42),
            new PongEnvelope(protocol, Guid.NewGuid(), timestamp, 42)
        };

        foreach (var envelope in envelopes)
        {
            var json = JsonSerializer.Serialize(envelope, ProtocolJsonContext.Default.MessageEnvelope);
            Assert.IsTrue(json.Contains("\"kind\"", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("Jiaolong.Contracts", StringComparison.Ordinal));
            var actual = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MessageEnvelope);
            Assert.AreEqual(envelope.GetType(), actual?.GetType());
        }
    }

    [TestMethod]
    public void Unknown_minor_fields_are_ignored()
    {
        const string json = "{\"kind\":\"hello\",\"protocolVersion\":{\"major\":1,\"minor\":0},\"messageId\":\"11111111-1111-1111-1111-111111111111\",\"sentAtUtc\":\"2026-08-21T00:00:00Z\",\"clientVersion\":\"test\",\"futureField\":true}";
        var envelope = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MessageEnvelope);
        Assert.IsInstanceOfType<HelloEnvelope>(envelope);
        Assert.AreEqual("test", ((HelloEnvelope)envelope!).ClientVersion);
    }

    [TestMethod]
    public void Unknown_envelope_returns_stable_invalid_frame_error()
    {
        var success = ProtocolCodec.TryDeserializeEnvelope("{\"kind\":\"future\"}", out _, out var error);
        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.AreEqual(ErrorCode.InvalidFrame, error!.Code);
        Assert.IsFalse(error.Details.ContainsKey("exception"));
    }
}
