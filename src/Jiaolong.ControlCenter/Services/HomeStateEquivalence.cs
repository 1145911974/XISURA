using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public static class HomeStateEquivalence
{
    // Telemetry has its own stream; fast control polling must not repaint every workspace.
    public static bool HaveSameControls(HomeStateSnapshot left, HomeStateSnapshot right) =>
        AreEquivalent(left.Capabilities, right.Capabilities) &&
        AreEquivalent(left.Controls, right.Controls) &&
        left.Telemetry?.HardwareState == right.Telemetry?.HardwareState &&
        left.Telemetry?.AcPowerConnected == right.Telemetry?.AcPowerConnected &&
        left.Telemetry?.BiosVersion == right.Telemetry?.BiosVersion;

    public static bool AreEquivalent(HomeStateSnapshot left, HomeStateSnapshot right) =>
        AreEquivalent(left.Capabilities, right.Capabilities) &&
        AreEquivalent(left.Telemetry, right.Telemetry) &&
        AreEquivalent(left.Controls, right.Controls);

    private static bool AreEquivalent(CapabilitySnapshot left, CapabilitySnapshot right) =>
        left.SupportState == right.SupportState &&
        string.Equals(left.Reason, right.Reason, StringComparison.Ordinal) &&
        Equals(left.Identity, right.Identity) &&
        left.Items.Length == right.Items.Length &&
        left.Items.Zip(right.Items).All(pair =>
            string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal) &&
            pair.First.State == pair.Second.State &&
            string.Equals(pair.First.Reason, pair.Second.Reason, StringComparison.Ordinal));

    private static bool AreEquivalent(HardwareSnapshot? left, HardwareSnapshot? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.HardwareState == right.HardwareState &&
            left.CpuTemperatureC == right.CpuTemperatureC &&
            left.GpuTemperatureC == right.GpuTemperatureC &&
            left.CpuPowerWatts == right.CpuPowerWatts &&
            left.GpuPowerWatts == right.GpuPowerWatts &&
            left.CpuUsagePercent == right.CpuUsagePercent &&
            left.CpuFrequencyMhz == right.CpuFrequencyMhz &&
            left.GpuUsagePercent == right.GpuUsagePercent &&
            left.GpuFrequencyMhz == right.GpuFrequencyMhz &&
            left.GpuCoreVoltageVolts == right.GpuCoreVoltageVolts &&
            left.GpuPerformanceState == right.GpuPerformanceState &&
            left.GpuPerformanceLimitReason == right.GpuPerformanceLimitReason &&
            left.GpuMemoryUsedGb == right.GpuMemoryUsedGb &&
            left.GpuMemoryTotalGb == right.GpuMemoryTotalGb &&
            left.MemoryUsedGb == right.MemoryUsedGb &&
            left.MemoryTotalGb == right.MemoryTotalGb &&
            left.SystemDriveUsedGb == right.SystemDriveUsedGb &&
            left.SystemDriveTotalGb == right.SystemDriveTotalGb &&
            left.AllDrivesUsedGb == right.AllDrivesUsedGb &&
            left.AllDrivesTotalGb == right.AllDrivesTotalGb &&
            left.CpuFanRpm == right.CpuFanRpm &&
            left.GpuFanRpm == right.GpuFanRpm &&
            left.BiosVersion == right.BiosVersion &&
            left.AcPowerConnected == right.AcPowerConnected &&
            left.BatteryPercent == right.BatteryPercent;
    }

    private static bool AreEquivalent(HomeControlState left, HomeControlState right) =>
        left.PerformanceMode == right.PerformanceMode &&
        left.StrongCooling == right.StrongCooling &&
        left.MuxMode == right.MuxMode &&
        Equals(left.GpuClockLimit, right.GpuClockLimit) &&
        Equals(left.GpuPowerLimit, right.GpuPowerLimit) &&
        Equals(left.GpuPowerPolicy, right.GpuPowerPolicy) &&
        Equals(left.CpuTuning, right.CpuTuning) &&
        Equals(left.KeyboardLighting, right.KeyboardLighting) &&
        left.KeyboardLightingNativeCycleAvailable == right.KeyboardLightingNativeCycleAvailable &&
        string.Equals(left.KeyboardLightingError, right.KeyboardLightingError, StringComparison.Ordinal) &&
        (ReferenceEquals(left.GpuVf, right.GpuVf) ||
            (left.GpuVf is { } leftVf && right.GpuVf is { } rightVf &&
             leftVf.Error == rightVf.Error && leftVf.MemoryOffsetKhz == rightVf.MemoryOffsetKhz &&
             leftVf.CoreOffsetKhz == rightVf.CoreOffsetKhz &&
             leftVf.VoltageBoostError == rightVf.VoltageBoostError &&
             leftVf.VoltageBoostPercent == rightVf.VoltageBoostPercent && leftVf.MinimumOffsetKhz == rightVf.MinimumOffsetKhz &&
             leftVf.MaximumOffsetKhz == rightVf.MaximumOffsetKhz && leftVf.Nodes.SequenceEqual(rightVf.Nodes))) &&
        left.QuickSettings.Length == right.QuickSettings.Length &&
        left.QuickSettings.Zip(right.QuickSettings).All(pair =>
            pair.First.Setting == pair.Second.Setting &&
            pair.First.Enabled == pair.Second.Enabled &&
            string.Equals(pair.First.Reason, pair.Second.Reason, StringComparison.Ordinal));
}
