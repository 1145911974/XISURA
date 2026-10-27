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
        left.IsOnAcPower == right.IsOnAcPower && left.Fields.SequenceEqual(right.Fields) &&
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
        SameAdaptive(left.AdaptiveAutomation, right.AdaptiveAutomation) &&
        SameExecution(left.ExecutionState, right.ExecutionState) &&
        left.StrongCooling == right.StrongCooling &&
        left.MuxMode == right.MuxMode &&
        Equals(left.GpuClockLimit, right.GpuClockLimit) &&
        Equals(left.GpuPowerLimit, right.GpuPowerLimit) &&
        Equals(left.GpuPowerPolicy, right.GpuPowerPolicy) &&
        SameCpu(left.CpuTuning, right.CpuTuning) &&
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

    private static bool SameAdaptive(AdaptiveAutomationStatus? left, AdaptiveAutomationStatus? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.Enabled == right.Enabled &&
        left.Running == right.Running && left.Revision == right.Revision && left.CurrentTarget == right.CurrentTarget &&
        left.SubmittedTarget == right.SubmittedTarget && left.TargetApplyState == right.TargetApplyState &&
        left.CandidateTarget == right.CandidateTarget && left.Reason == right.Reason && left.LastError == right.LastError;
    private static bool SameCpu(CpuTuningState? left, CpuTuningState? right) => ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        Equals(left with { PerCoreCurveOptimizer = null, AdvancedLimits = null }, right with { PerCoreCurveOptimizer = null, AdvancedLimits = null }) &&
        SameDictionary(left.PerCoreCurveOptimizer, right.PerCoreCurveOptimizer) &&
        (ReferenceEquals(left.AdvancedLimits, right.AdvancedLimits) || left.AdvancedLimits is { } a && right.AdvancedLimits is { } b &&
            Equals(a with { PerCoreCurveOptimizer = null, PerCoreOcClockMhz = null }, b with { PerCoreCurveOptimizer = null, PerCoreOcClockMhz = null }) &&
            SameDictionary(a.PerCoreCurveOptimizer, b.PerCoreCurveOptimizer) && SameDictionary(a.PerCoreOcClockMhz, b.PerCoreOcClockMhz));
    private static bool SameDictionary(IReadOnlyDictionary<int, int>? left, IReadOnlyDictionary<int, int>? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out int value) && value == pair.Value);
    private static bool SameExecution(ControlExecutionState? left, ControlExecutionState? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.Generation == right.Generation &&
        left.HardwareEpoch == right.HardwareEpoch && left.ConfigurationRevision == right.ConfigurationRevision &&
        left.LogicalMode == right.LogicalMode && left.NativeMode == right.NativeMode && left.NativeModeApplyState == right.NativeModeApplyState &&
        left.Pages.Length == right.Pages.Length && left.Pages.Zip(right.Pages).All(pair =>
            pair.First.Page == pair.Second.Page && pair.First.Key == pair.Second.Key && pair.First.Revision == pair.Second.Revision &&
            pair.First.State == pair.Second.State && pair.First.ErrorCode == pair.Second.ErrorCode &&
            pair.First.ConfirmedFields.SequenceEqual(pair.Second.ConfirmedFields) &&
            pair.First.UnverifiedFields.SequenceEqual(pair.Second.UnverifiedFields) && pair.First.SkippedFields.SequenceEqual(pair.Second.SkippedFields));
}
