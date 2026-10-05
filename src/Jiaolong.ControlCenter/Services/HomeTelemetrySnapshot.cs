using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public sealed record HomeTelemetrySnapshot(
    double? CpuTemperatureC,
    double? CpuUsagePercent,
    double? CpuFrequencyMhz,
    double? CpuPowerWatts,
    double? GpuTemperatureC,
    double? GpuUsagePercent,
    double? GpuMemoryUsedGb,
    double? GpuMemoryTotalGb,
    double? GpuPowerWatts,
    double? MemoryUsedGb,
    double? MemoryTotalGb,
    double? SystemDriveUsedGb,
    double? SystemDriveTotalGb,
    double? AllDrivesUsedGb,
    double? AllDrivesTotalGb,
    double? CpuFanRpm,
    double? GpuFanRpm)
{
    public DateTimeOffset? CapturedAtUtc { get; init; }
    public bool? AcPowerConnected { get; init; }
    public int? BatteryPercent { get; init; }

    public static HomeTelemetrySnapshot Unknown { get; } = new(
        null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null);

    public static HomeTelemetrySnapshot FromHardwareSnapshot(HardwareSnapshot snapshot) => new(
        snapshot.CpuTemperatureC,
        snapshot.CpuUsagePercent,
        snapshot.CpuFrequencyMhz,
        snapshot.CpuPowerWatts,
        snapshot.GpuTemperatureC,
        snapshot.GpuUsagePercent,
        snapshot.GpuMemoryUsedGb,
        snapshot.GpuMemoryTotalGb,
        snapshot.GpuPowerWatts,
        snapshot.MemoryUsedGb,
        snapshot.MemoryTotalGb,
        snapshot.SystemDriveUsedGb,
        snapshot.SystemDriveTotalGb,
        snapshot.AllDrivesUsedGb,
        snapshot.AllDrivesTotalGb,
        snapshot.CpuFanRpm,
        snapshot.GpuFanRpm)
    {
        CapturedAtUtc = snapshot.CapturedAtUtc,
        AcPowerConnected = snapshot.AcPowerConnected,
        BatteryPercent = snapshot.BatteryPercent
    };
}

public interface IHomeTelemetryReader : IDisposable
{
    HomeTelemetrySnapshot Read();
}
