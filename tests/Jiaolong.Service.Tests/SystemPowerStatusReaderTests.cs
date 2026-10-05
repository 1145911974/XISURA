using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class SystemPowerStatusReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Native_status_maps_ac_and_battery_values_without_guessing_unknowns()
    {
        var online = SystemPowerStatusReader.Parse(1, 8, 84);
        Assert.AreEqual(true, online.AcPowerConnected);
        Assert.AreEqual(84, online.BatteryPercent);

        var offline = SystemPowerStatusReader.Parse(0, 0, 35);
        Assert.AreEqual(false, offline.AcPowerConnected);
        Assert.AreEqual(35, offline.BatteryPercent);

        var unknown = SystemPowerStatusReader.Parse(255, 255, 255);
        Assert.IsNull(unknown.AcPowerConnected);
        Assert.IsNull(unknown.BatteryPercent);
    }

    [TestMethod]
    public void No_battery_flag_suppresses_a_battery_percentage()
    {
        var status = SystemPowerStatusReader.Parse(1, 128, 63);

        Assert.AreEqual(true, status.AcPowerConnected);
        Assert.IsNull(status.BatteryPercent);
    }

    [TestMethod]
    public void Read_returns_a_well_formed_live_windows_power_sample()
    {
        var status = SystemPowerStatusReader.Read();

        Assert.IsTrue(status.BatteryPercent is null or (>= 0 and <= 100));
        TestContext.WriteLine($"AC={status.AcPowerConnected?.ToString() ?? "unknown"}; battery={status.BatteryPercent?.ToString() ?? "unknown"}%");
    }
}
