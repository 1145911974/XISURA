using System.Text.Json.Serialization;

namespace Jiaolong.Contracts.Models;

[JsonConverter(typeof(Jiaolong.Contracts.Commands.LowerCamelEnumConverter<CapabilityState>))]
public enum CapabilityState
{
    Available,
    ReadOnly,
    Unavailable
}

public sealed record CapabilityDescriptor(string Key, CapabilityState State, string? Reason);

public sealed record FieldCapability(string Key, CapabilityState Availability, bool Readable, bool Writable,
    double? Minimum, double? Maximum, double? Step, string Unit, string Source,
    string NativeModeSideEffect, DateTimeOffset? VerifiedAtUtc, long HardwareEpoch);

[JsonConverter(typeof(Jiaolong.Contracts.Commands.LowerCamelEnumConverter<DeviceSupportState>))]
public enum DeviceSupportState
{
    Diagnosing,
    Ready,
    ReadOnly,
    Repairing,
    RepairRequired
}

public sealed record HardwareIdentity(
    string BoardProduct,
    string BiosVersion,
    string CpuModel,
    string GpuName);

public sealed record QuickSettingStatus(QuickSettingKind Setting, bool? Enabled, string? Reason);

public sealed record CpuTuningState(
    int? TemperatureLimitC,
    int? SplWatts,
    int? SpptWatts,
    int? MaxFrequencyMhz,
    bool? BoostEnabled,
    int? EnabledCoreCount,
    Guid? WindowsPowerSchemeId,
    int? NegativeCurveOptimizer,
    string? CurveOptimizerVerification)
{
    public int? AcMaxFrequencyMhz { get; init; }
    public int? DcMaxFrequencyMhz { get; init; }
    public int? AcMinActiveCoresPercent { get; init; }
    public int? DcMinActiveCoresPercent { get; init; }
    public int? AcBoostMode { get; init; }
    public int? DcBoostMode { get; init; }
    public string? WindowsPowerSchemeName { get; init; }
    public int? PboScalar { get; init; }
    public IReadOnlyDictionary<int, int>? PerCoreCurveOptimizer { get; init; }
    public Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan? AdvancedLimits { get; init; }
    public double? ObservedSplWatts { get; init; }
    public bool? OemCustomPowerMode { get; init; }
}

public static class CpuTuningStateExtensions
{
    public static bool HasCompleteCurveValues(this CpuTuningState state) =>
        state.PerCoreCurveOptimizer is { Count: > 0 and <= 16 } cores &&
        state.EnabledCoreCount is int count && cores.Count == count &&
        Enumerable.Range(0, cores.Count).All(core => cores.TryGetValue(core, out int value) && value is >= -50 and <= 30);

    public static int? AcFrequency(this CpuTuningState state) =>
        state.AcMaxFrequencyMhz ?? state.MaxFrequencyMhz;

    public static int? DcFrequency(this CpuTuningState state) =>
        state.DcMaxFrequencyMhz ?? state.MaxFrequencyMhz;
}

public sealed record HomeControlState(
    PerformanceMode? PerformanceMode,
    bool? StrongCooling,
    QuickSettingStatus[] QuickSettings)
{
    public ControlExecutionState? ExecutionState { get; init; }
    public Jiaolong.Contracts.Commands.PerformanceModeEvidence? ModeEvidence { get; init; }
    public AdaptiveAutomationStatus? AdaptiveAutomation { get; init; }
    public MuxMode? MuxMode { get; init; }
    public CpuTuningState? CpuTuning { get; init; }
    public KeyboardLightingPlan? KeyboardLighting { get; init; }
    public string? KeyboardLightingError { get; init; }
    public double? KeyboardLightingElapsedSeconds { get; init; }
    public bool KeyboardLightingPreviewAvailable { get; init; }
    public bool KeyboardLightingNativeCycleAvailable { get; init; }
    public bool KeyboardLightingPreviewActive { get; init; }
    public bool FanTargetCeilingAvailable { get; init; }
    public bool FanAutomaticCeilingAvailable { get; init; }
    public bool GpuCurveFactoryResetAvailable { get; init; }
    public bool FanAutomaticCeilingActive { get; init; }
    public FanControlPlan? ActiveFanControlPlan { get; init; }
    public GpuVfState? GpuVf { get; init; }
    public GpuClockLimitState? GpuClockLimit { get; init; }
    public GpuPowerLimitState? GpuPowerLimit { get; init; }
    public GpuPowerPolicyState? GpuPowerPolicy { get; init; }
    public FanEcControlState? FanEcControl { get; init; }
    public FanEcMappingState? FanEcMapping { get; init; }
}

public sealed record GpuClockLimitState(int MinimumMhz, int MaximumMhz, int? SubmittedMhz, bool HasSubmission, string? Error);
public sealed record GpuPowerLimitState(int MinimumWatts, int MaximumWatts, int DefaultWatts, int? SubmittedWatts, string? Error);
public sealed record GpuPowerPolicyState(uint MinimumMilliPercent, uint MaximumMilliPercent,
    uint? CurrentMilliPercent, uint? SubmittedMilliPercent, bool DriverRangeAvailable, string? Error);
public sealed record FanEcControlState(DateTimeOffset CapturedAtUtc, byte Initialization, byte Control,
    byte CpuTarget, byte GpuTarget, byte? ExpectedInitialization);

public sealed record FanEcMappingState(DateTimeOffset CapturedAtUtc, byte Initialization, byte OriginalInitialization,
    bool Restored, bool TargetWritesStarted, bool TargetReleaseVerified);

public sealed record GpuVfNode(int VoltageMv, int BaseFrequencyMhz, int OffsetKhz);

public sealed record GpuVfState(GpuVfNode[] Nodes, int MinimumOffsetKhz, int MaximumOffsetKhz, string? Error)
{
    public DateTimeOffset? ObservedAtUtc { get; init; }
    public string? Source { get; init; }
    public int? MemoryOffsetKhz { get; init; }
    public int? MemoryMinimumOffsetKhz { get; init; }
    public int? MemoryMaximumOffsetKhz { get; init; }
    public int? CoreOffsetKhz { get; init; }
    public int? CoreMinimumOffsetKhz { get; init; }
    public int? CoreMaximumOffsetKhz { get; init; }
    public uint? VoltageBoostPercent { get; init; }
    public string? VoltageBoostError { get; init; }

    public (int MinimumKhz, int MaximumKhz)? PstateOffsetRange(bool memory, bool preserveCurve = true)
    {
        int vfMinimum = Math.Max(-200_000, MinimumOffsetKhz), vfMaximum = Math.Min(200_000, MaximumOffsetKhz);
        if (Error is not null || Nodes is not { Length: 127 } || Nodes[0].OffsetKhz != 0 || vfMinimum > vfMaximum ||
            Nodes.Skip(1).Any(node => node.OffsetKhz < vfMinimum || node.OffsetKhz > vfMaximum)) return null;
        int? current = memory ? MemoryOffsetKhz : CoreOffsetKhz;
        int? driverMinimum = memory ? MemoryMinimumOffsetKhz : CoreMinimumOffsetKhz;
        int? driverMaximum = memory ? MemoryMaximumOffsetKhz : CoreMaximumOffsetKhz;
        int envelopeMaximum = memory ? 3_000_000 : 1_000_000;
        if (current is null || driverMinimum is null || driverMaximum is null || driverMinimum > driverMaximum ||
            current < driverMinimum || current > driverMaximum || current < -1_000_000 || current > envelopeMaximum) return null;
        int minimum = Math.Max(-1_000_000, driverMinimum.Value), maximum = Math.Min(envelopeMaximum, driverMaximum.Value);
        if (!memory && preserveCurve)
        {
            // A P0 core write shifts every editable V/F node; preserve the whole table.
            foreach (var node in Nodes.Skip(1))
            {
                minimum = Math.Max(minimum, current.Value + vfMinimum - node.OffsetKhz);
                maximum = Math.Min(maximum, current.Value + vfMaximum - node.OffsetKhz);
            }
        }
        minimum = (int)(Math.Ceiling(minimum / 1000d) * 1000);
        maximum = (int)(Math.Floor(maximum / 1000d) * 1000);
        return minimum <= maximum ? (minimum, maximum) : null;
    }
}

public sealed record HomeStateSnapshot(
    CapabilitySnapshot Capabilities,
    HardwareSnapshot? Telemetry,
    HomeControlState Controls);

public sealed record CapabilitySnapshot(CapabilityDescriptor[] Items)
{
    public bool IsAvailable(string key) => Items.Any(item =>
        string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) && item.State == CapabilityState.Available);
    public FieldCapability[] Fields { get; init; } = [];
    public bool? IsOnAcPower { get; init; }
    public HardwareIdentity? Identity { get; init; }
    public DeviceSupportState SupportState { get; init; } = DeviceSupportState.Diagnosing;
    public string? Reason { get; init; }
}
