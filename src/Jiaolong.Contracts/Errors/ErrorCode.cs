using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiaolong.Contracts.Errors;

[JsonConverter(typeof(ErrorCodeJsonConverter))]
public enum ErrorCode
{
    InvalidFrame,
    UnsupportedProtocol,
    ValidationFailed,
    UnauthorizedClient,
    CapabilityUnavailable,
    ReadOnlySafeMode,
    DeviceMismatch,
    BiosUnsupported,
    DependencyMissing,
    DependencyVersionUnsupported,
    ConflictDetected,
    CommandInProgress,
    IdempotencyConflict,
    DeadlineExceeded,
    CancelledBeforeApply,
    HardwareReadFailed,
    HardwareWriteFailed,
    ReadBackMismatch,
    RollbackFailed,
    CircuitOpen,
    ServiceUnavailable,
    InternalFailure
}

public static class ErrorCodeExtensions
{
    public static string ToWireValue(this ErrorCode code) => code switch
    {
        ErrorCode.InvalidFrame => "invalidFrame",
        ErrorCode.UnsupportedProtocol => "unsupportedProtocol",
        ErrorCode.ValidationFailed => "validationFailed",
        ErrorCode.UnauthorizedClient => "unauthorizedClient",
        ErrorCode.CapabilityUnavailable => "capabilityUnavailable",
        ErrorCode.ReadOnlySafeMode => "readOnlySafeMode",
        ErrorCode.DeviceMismatch => "deviceMismatch",
        ErrorCode.BiosUnsupported => "biosUnsupported",
        ErrorCode.DependencyMissing => "dependencyMissing",
        ErrorCode.DependencyVersionUnsupported => "dependencyVersionUnsupported",
        ErrorCode.ConflictDetected => "conflictDetected",
        ErrorCode.CommandInProgress => "commandInProgress",
        ErrorCode.IdempotencyConflict => "idempotencyConflict",
        ErrorCode.DeadlineExceeded => "deadlineExceeded",
        ErrorCode.CancelledBeforeApply => "cancelledBeforeApply",
        ErrorCode.HardwareReadFailed => "hardwareReadFailed",
        ErrorCode.HardwareWriteFailed => "hardwareWriteFailed",
        ErrorCode.ReadBackMismatch => "readBackMismatch",
        ErrorCode.RollbackFailed => "rollbackFailed",
        ErrorCode.CircuitOpen => "circuitOpen",
        ErrorCode.ServiceUnavailable => "serviceUnavailable",
        ErrorCode.InternalFailure => "internalFailure",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown error code.")
    };
}

public sealed class ErrorCodeJsonConverter : JsonConverter<ErrorCode>
{
    public override ErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var wireValue = reader.GetString();
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            if (string.Equals(code.ToWireValue(), wireValue, StringComparison.Ordinal))
            {
                return code;
            }
        }

        throw new JsonException("Unknown error code.");
    }

    public override void Write(Utf8JsonWriter writer, ErrorCode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireValue());
}
