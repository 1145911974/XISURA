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
    public string? WindowsPowerSchemeName { get; init; }
    public int? PboScalar { get; init; }
    public IReadOnlyDictionary<int, int>? PerCoreCurveOptimizer { get; init; }
    public Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan? AdvancedLimits { get; init; }
    public bool? OemCustomPowerMode { get; init; }
}

public static class CpuTuningStateExtensions
{
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
}

public sealed record GpuClockLimitState(int MinimumMhz, int MaximumMhz, int? SubmittedMhz, bool HasSubmission, string? Error);
public sealed record GpuPowerLimitState(int MinimumWatts, int MaximumWatts, int DefaultWatts, int? SubmittedWatts, string? Error);
public sealed record GpuPowerPolicyState(uint MinimumMilliPercent, uint MaximumMilliPercent,
    uint? CurrentMilliPercent, uint? SubmittedMilliPercent, bool DriverRangeAvailable, string? Error);
public sealed record FanEcControlState(DateTimeOffset CapturedAtUtc, byte Initialization, byte Control,
    byte CpuTarget, byte GpuTarget, byte? ExpectedInitialization);

public sealed record GpuVfNode(int VoltageMv, int BaseFrequencyMhz, int OffsetKhz);

public sealed record GpuVfState(GpuVfNode[] Nodes, int MinimumOffsetKhz, int MaximumOffsetKhz, string? Error)
{
    public int? MemoryOffsetKhz { get; init; }
    public int? MemoryMinimumOffsetKhz { get; init; }
    public int? MemoryMaximumOffsetKhz { get; init; }
    public int? CoreOffsetKhz { get; init; }
    public int? CoreMinimumOffsetKhz { get; init; }
    public int? CoreMaximumOffsetKhz { get; init; }
    public uint? VoltageBoostPercent { get; init; }
    public string? VoltageBoostError { get; init; }
}

public sealed record HomeStateSnapshot(
    CapabilitySnapshot Capabilities,
    HardwareSnapshot? Telemetry,
    HomeControlState Controls);

public sealed record CapabilitySnapshot(CapabilityDescriptor[] Items)
{
    public HardwareIdentity? Identity { get; init; }
    public DeviceSupportState SupportState { get; init; } = DeviceSupportState.Diagnosing;
    public string? Reason { get; init; }
}
