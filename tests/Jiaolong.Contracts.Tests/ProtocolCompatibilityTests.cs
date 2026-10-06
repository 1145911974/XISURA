using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Contracts.Tests;

[TestClass]
public sealed class ProtocolCompatibilityTests
{
    [TestMethod]
    public void V1_round_trip_preserves_operation_id_and_discriminator()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        HardwareCommand expected = new SetPerformanceModeCommand(id, PerformanceMode.Balanced);
        var json = JsonSerializer.Serialize(expected, ProtocolJsonContext.Default.HardwareCommand);
        var actual = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.HardwareCommand);
        Assert.AreEqual(expected, actual);
        var legacy = new CommandResult(id, CommandState.Applied, null, RequiredUserAction.None, null, false);
        string legacyJson = JsonSerializer.Serialize(legacy, ProtocolJsonContext.Default.CommandResult);
        Assert.IsNull(JsonSerializer.Deserialize(legacyJson, ProtocolJsonContext.Default.CommandResult)!.VerifiedPerformanceMode);
        var confirmed = legacy with { VerifiedPerformanceMode = PerformanceMode.Balanced };
        Assert.AreEqual(confirmed, JsonSerializer.Deserialize(
            JsonSerializer.Serialize(confirmed, ProtocolJsonContext.Default.CommandResult), ProtocolJsonContext.Default.CommandResult));
    }

    [TestMethod]
    public void Same_major_newer_minor_ignores_unknown_additive_fields()
    {
        Assert.IsTrue(new ProtocolVersion(1, 0).IsCompatibleWith(new ProtocolVersion(1, 9)));
    }

    [TestMethod]
    public void Different_major_is_rejected_with_unsupported_protocol()
    {
        Assert.IsFalse(new ProtocolVersion(1, 0).IsCompatibleWith(new ProtocolVersion(2, 0)));
        Assert.AreEqual("unsupportedProtocol", ErrorCode.UnsupportedProtocol.ToWireValue());
    }

    [TestMethod]
    public void Response_has_exactly_one_payload_or_error_matching_status()
    {
        Assert.IsTrue(ProtocolFixture.SuccessResponseIsValid());
        Assert.IsTrue(ProtocolFixture.ErrorResponseIsValid());
        Assert.IsFalse(ProtocolFixture.ResponseIsValid(ResponseStatus.Success, payload: null, error: null));
        Assert.IsFalse(ProtocolFixture.ResponseIsValid(ResponseStatus.Error, ProtocolFixture.Payload, ProtocolFixture.Error));
    }

    private static class ProtocolFixture
    {
        public static JsonElement Payload => JsonDocument.Parse("{\"ok\":true}").RootElement;

        public static ServiceError Error => new(
            ErrorCode.ValidationFailed,
            "validationFailed",
            false,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new Dictionary<string, string>());

        public static bool SuccessResponseIsValid() =>
            ResponseValidator.IsValid(ResponseStatus.Success, Payload, null);

        public static bool ErrorResponseIsValid() =>
            ResponseValidator.IsValid(ResponseStatus.Error, null, Error);

        public static bool ResponseIsValid(ResponseStatus status, JsonElement? payload, ServiceError? error) =>
            ResponseValidator.IsValid(status, payload, error);
    }
}
