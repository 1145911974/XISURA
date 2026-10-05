using System.Text.Json;
using System.Text.Json.Serialization;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;

namespace Jiaolong.Contracts.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HelloEnvelope), "hello")]
[JsonDerivedType(typeof(HelloAckEnvelope), "helloAck")]
[JsonDerivedType(typeof(RequestEnvelope), "request")]
[JsonDerivedType(typeof(ResponseEnvelope), "response")]
[JsonDerivedType(typeof(EventEnvelope), "event")]
[JsonDerivedType(typeof(CancelEnvelope), "cancel")]
[JsonDerivedType(typeof(PingEnvelope), "ping")]
[JsonDerivedType(typeof(PongEnvelope), "pong")]
public abstract record MessageEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc);

public sealed record HelloEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    string ClientVersion) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

public sealed record HelloAckEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    Guid ReplyToMessageId,
    string ServiceVersion) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

public sealed record RequestEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    Guid OperationId,
    string Operation,
    DateTimeOffset DeadlineUtc,
    JsonElement Payload) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

[JsonConverter(typeof(LowerCamelEnumConverter<ResponseStatus>))]
public enum ResponseStatus
{
    Success,
    Error
}

public sealed record ResponseEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    Guid ReplyToMessageId,
    Guid? OperationId,
    ResponseStatus Status,
    JsonElement? Payload,
    ServiceError? Error) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

public sealed record EventEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    string EventName,
    ulong Sequence,
    JsonElement Payload) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

public sealed record CancelEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    Guid OperationId) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

public sealed record PingEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    ulong Nonce) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

public sealed record PongEnvelope(
    ProtocolVersion ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    ulong Nonce) : MessageEnvelope(ProtocolVersion, MessageId, SentAtUtc);

public static class ResponseValidator
{
    public static bool IsValid(ResponseStatus status, JsonElement? payload, ServiceError? error) =>
        status switch
        {
            ResponseStatus.Success => payload is { ValueKind: not JsonValueKind.Undefined } && error is null,
            ResponseStatus.Error => payload is null && error is not null,
            _ => false
        };
}

public static class ProtocolCodec
{
    public static bool TryDeserializeEnvelope(string json, out MessageEnvelope? envelope, out ServiceError? error)
    {
        try
        {
            envelope = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MessageEnvelope);
            if (envelope is null)
            {
                error = ServiceError.Create(ErrorCode.InvalidFrame, Guid.Empty, false);
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException)
        {
            envelope = null;
            error = ServiceError.Create(ErrorCode.InvalidFrame, Guid.Empty, false);
            return false;
        }
    }
}
