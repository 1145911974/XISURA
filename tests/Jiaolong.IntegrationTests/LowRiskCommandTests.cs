using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Safety;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class LowRiskCommandTests
{
    [TestMethod]
    public void Simulator_safe_mode_rejects_low_risk_command_before_transport()
    {
        var result = new HardwareWriteGate().Validate(
            new SetMuxModeCommand(Guid.NewGuid(), MuxMode.Discrete, true),
            new CompatibilityDecision(CompatibilityMode.ReadOnlySafeMode, new CapabilitySnapshot([]), null, []),
            new SystemState(false, true));

        Assert.IsFalse(result.IsValid);
    }
}
