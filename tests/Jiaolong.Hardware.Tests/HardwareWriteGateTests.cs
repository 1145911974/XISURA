using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Safety;

namespace Jiaolong.Hardware.Tests;

[TestClass]
[TestCategory("HardwareWrite")]
public sealed class HardwareWriteGateTests
{
    [TestMethod]
    public void Write_gate_requires_compatible_manifest_no_conflict_and_healthy_dependency()
    {
        var cases = new[]
        {
            (CompatibilityMode.Writable, true, true, true),
            (CompatibilityMode.ReadOnlySafeMode, true, true, false),
            (CompatibilityMode.Writable, false, true, false),
            (CompatibilityMode.Writable, true, false, false)
        };
        foreach (var (mode, noConflict, healthyDependency, allowed) in cases)
        {
            var result = new HardwareWriteGate().Validate(
                new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced),
                new CompatibilityDecision(mode, new CapabilitySnapshot([]), null, []),
                new SystemState(!noConflict, healthyDependency));

            Assert.AreEqual(allowed, result.IsValid, $"mode={mode}, conflict={!noConflict}, healthy={healthyDependency}");
        }
    }
}
