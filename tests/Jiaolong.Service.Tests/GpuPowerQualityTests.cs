using System.Reflection;
using Jiaolong.Hardware.Mechrevo.Telemetry;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Home;
using LibreHardwareMonitor.Hardware;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class GpuPowerQualityTests
{
    [TestMethod]
    public void Gpu_power_replay_quarantines_idle_590_and_preserves_valid_spikes_and_source_evidence()
    {
        const string adapter = "NVIDIA GeForce RTX 4060 Laptop GPU";
        var hardware = DispatchProxy.Create<IHardware, HardwareProxy>();
        var now = DateTimeOffset.UtcNow;
        WindowsHomeHardwareProvider.GpuPowerSelection Select(float raw, NvidiaSmiReading driver, double usage = 0,
            DateTimeOffset? sensorTime = null, DateTimeOffset? driverTime = null) =>
            WindowsHomeHardwareProvider.SelectGpuPower(
                [new(hardware, SensorType.Power, "GPU Package", raw)], sensorTime ?? now,
                driver, driverTime ?? now, adapter, usage, 16, 0, now);
        var goodDriver = new NvidiaSmiReading(49, 0, 16, DataQuality.Good)
        { RawPowerWatts = 16, AdapterName = adapter, MaximumPowerLimitWatts = 140 };
        foreach (var raw in new[] { 16f, 590f, 16f })
        {
            var result = Select(raw, goodDriver);
            Assert.AreEqual(16, result.Watts);
            Assert.AreEqual((double)raw, result.RawWatts);
            Assert.AreEqual(raw == 590 ? "invalid" : "good", result.RawQuality);
        }
        Assert.IsNull(Select(590, NvidiaSmiReading.Unknown).Watts);
        Assert.AreEqual(140, Select(140, goodDriver with { RawPowerWatts = 140 }, 98).Watts);
        Assert.AreEqual(140, Select(140, goodDriver, 98).Watts);
        Assert.AreEqual(140, Select(140, NvidiaSmiReading.Unknown, 98).Watts);
        Assert.IsNull(Select(590, goodDriver, driverTime: now.AddSeconds(-4)).Watts);
        Assert.IsNull(Select(float.NaN, NvidiaSmiReading.Unknown).Watts);
        Assert.AreEqual(16, Select(590, goodDriver, sensorTime: now.AddSeconds(-4)).Watts);
        Assert.IsNull(Select(590, goodDriver with { AdapterName = "GameViewer Virtual Display" }).Watts);
    }

    public class HardwareProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Name" => "NVIDIA GeForce RTX 4060 Laptop GPU",
            "get_HardwareType" => HardwareType.GpuNvidia,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }
}
