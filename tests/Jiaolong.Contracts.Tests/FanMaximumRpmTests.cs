using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;

namespace Jiaolong.Contracts.Tests;

[TestClass]
public sealed class FanMaximumRpmTests
{
    [TestMethod]
    public void Fan_ceiling_validates_and_survives_ipc_without_changing_legacy_defaults()
    {
        var plan = new FanControlPlan([new(30, 20), new(100, 100)]) { MaximumRpm = 4200 };
        HardwareCommand command = new SetFanControlCommand(Guid.NewGuid(), plan, true);
        Assert.IsNull(CommandValidation.Validate(command));
        string json = JsonSerializer.Serialize(command, ProtocolJsonContext.Default.HardwareCommand);
        Assert.AreEqual(4200, ((SetFanControlCommand)JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.HardwareCommand)!).Plan.MaximumRpm);
        Assert.IsNotNull(CommandValidation.Validate(new SetFanControlCommand(Guid.NewGuid(), plan with { MaximumRpm = 1799 }, true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetFanControlCommand(Guid.NewGuid(), plan with { MaximumRpm = 5801 }, true)));
        Assert.IsNull(new FanControlPlan([new(30, 20), new(100, 100)]).MaximumRpm);
        var controls = new HomeControlState(PerformanceMode.Balanced, false, [])
        { ActiveFanControlPlan = plan with { Strategy = "Auto" } };
        var controlsJson = JsonSerializer.Serialize(controls, ProtocolJsonContext.Default.HomeControlState);
        var readback = JsonSerializer.Deserialize(controlsJson, ProtocolJsonContext.Default.HomeControlState);
        Assert.AreEqual("Auto", readback!.ActiveFanControlPlan!.Strategy);
        Assert.AreEqual(4200, readback.ActiveFanControlPlan.MaximumRpm);
        Assert.IsNull(CommandValidation.Validate(new SetFanControlCommand(Guid.NewGuid(), plan with { Strategy = "Auto" }, true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetFanControlCommand(Guid.NewGuid(), plan with { Strategy = "Auto", MaximumRpm = null }, true)));
    }
}
