namespace Jiaolong.Contracts.Models;

public sealed record HardwareSnapshot(
    DateTimeOffset CapturedAtUtc,
    string HardwareState,
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    int? CpuPowerWatts,
    int? GpuPowerWatts)
{
    public double? CpuUsagePercent { get; init; }
    public DateTimeOffset? CpuUsageObservedAtUtc { get; init; }
    public DateTimeOffset? GpuUsageObservedAtUtc { get; init; }
    public DateTimeOffset? CpuTemperatureObservedAtUtc { get; init; }
    public DateTimeOffset? GpuTemperatureObservedAtUtc { get; init; }
    public bool? BatterySaver { get; init; }
    public double? CpuFrequencyMhz { get; init; }
    public double? CpuVoltageVolts { get; init; }
    public double? GpuUsagePercent { get; init; }
    public double? GpuFrequencyMhz { get; init; }
    public double? GpuCoreVoltageVolts { get; init; }
    public string? GpuPerformanceState { get; init; }
    public string? GpuPerformanceLimitReason { get; init; }
    public double? GpuEnforcedPowerLimitWatts { get; init; }
    public double? GpuMaximumPowerLimitWatts { get; init; }
    public double? GpuPowerRawWatts { get; init; }
    public DateTimeOffset? GpuPowerRawObservedAtUtc { get; init; }
    public DateTimeOffset? GpuPowerObservedAtUtc { get; init; }
    public string? GpuPowerRawSource { get; init; }
    public string? GpuPowerSource { get; init; }
    public string? GpuPowerUnit { get; init; }
    public string? GpuPowerQuality { get; init; }
    public string? GpuPowerRawQuality { get; init; }
    public string? GpuPowerSelectionReason { get; init; }
    public double? GpuMemoryUsedGb { get; init; }
    public double? GpuMemoryTotalGb { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? SystemDriveUsedGb { get; init; }
    public double? SystemDriveTotalGb { get; init; }
    public double? AllDrivesUsedGb { get; init; }
    public double? AllDrivesTotalGb { get; init; }
    public double? CpuFanRpm { get; init; }
    public double? GpuFanRpm { get; init; }
    public string? BiosVersion { get; init; }
    public bool? AcPowerConnected { get; init; }
    public int? BatteryPercent { get; init; }
}
