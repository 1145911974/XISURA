using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Contracts.Tests;

[TestClass]
public sealed class ErrorContractTests
{
    [TestMethod]
    public void Error_code_wire_values_are_unique_and_lower_camel()
    {
        var values = Enum.GetValues<ErrorCode>().Select(code => code.ToWireValue()).ToArray();
        CollectionAssert.AreEqual(values.Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray(), values.OrderBy(value => value).ToArray());
        Assert.IsTrue(values.All(value => value.Length > 0 && char.IsLower(value[0])));
    }

    [TestMethod]
    public void Out_of_range_cpu_plan_returns_validation_error_without_exception_text()
    {
        var command = new SetCpuTuningCommand(
            Guid.NewGuid(),
            new CpuTuningPlan(200, null, null, null, null, null, null, 1),
            true);
        var error = CommandValidation.Validate(command);
        Assert.IsNotNull(error);
        Assert.AreEqual(ErrorCode.ValidationFailed, error!.Code);
        Assert.IsFalse(error.Details.ContainsKey("exception"));
    }

    [TestMethod]
    public void Service_error_json_contains_only_contract_fields()
    {
        var error = ServiceError.Create(ErrorCode.HardwareReadFailed, Guid.NewGuid(), true);
        var json = JsonSerializer.Serialize(error, ProtocolJsonContext.Default.ServiceError);
        Assert.IsTrue(json.Contains("\"code\":\"hardwareReadFailed\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Exception", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("StackTrace", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Hardware_snapshot_round_trips_cpu_voltage()
    {
        var snapshot = new Jiaolong.Contracts.Models.HardwareSnapshot(
            DateTimeOffset.UtcNow, "normal", 58, 51, 42, 90)
        {
            CpuVoltageVolts = 1.12
        };

        var json = JsonSerializer.Serialize(snapshot, ProtocolJsonContext.Default.HardwareSnapshot);
        var roundTrip = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.HardwareSnapshot);

        Assert.AreEqual(1.12, roundTrip!.CpuVoltageVolts);
    }
}
