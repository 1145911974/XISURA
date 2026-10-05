using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class HomePowerTelemetryTests
{
    [TestMethod]
    public void Hardware_power_readings_are_preserved_for_live_home_display()
    {
        var telemetry = HomeTelemetrySnapshot.FromHardwareSnapshot(
            new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", null, null, null, null)
            {
                AcPowerConnected = false,
                BatteryPercent = 42
            });

        Assert.AreEqual(false, telemetry.AcPowerConnected);
        Assert.AreEqual(42, telemetry.BatteryPercent);

        var unknown = HomeTelemetrySnapshot.FromHardwareSnapshot(
            new HardwareSnapshot(DateTimeOffset.UtcNow, "unknown", null, null, null, null));
        Assert.IsNull(unknown.AcPowerConnected);
        Assert.IsNull(unknown.BatteryPercent);
    }
}
