using System.Text.Json;
using System.Text.Json.Serialization;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Contracts.Commands;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetPerformanceModeCommand), "setPerformanceMode")]
[JsonDerivedType(typeof(SetMuxModeCommand), "setMuxMode")]
[JsonDerivedType(typeof(SetFanControlCommand), "setFanControl")]
[JsonDerivedType(typeof(ReleaseFanControlCommand), "releaseFanControl")]
[JsonDerivedType(typeof(SetKeyboardLightingCommand), "setKeyboardLighting")]
[JsonDerivedType(typeof(RestoreKeyboardLightingPreviewCommand), "restoreKeyboardLightingPreview")]
[JsonDerivedType(typeof(SetLidLogoCommand), "setLidLogo")]
[JsonDerivedType(typeof(SetQuickSettingCommand), "setQuickSetting")]
[JsonDerivedType(typeof(SetStrongCoolingCommand), "setStrongCooling")]
[JsonDerivedType(typeof(SetCpuTuningCommand), "setCpuTuning")]
[JsonDerivedType(typeof(SetCpuTuningBatchCommand), "setCpuTuningBatch")]
[JsonDerivedType(typeof(SetGpuFrequencyLimitCommand), "setGpuFrequencyLimit")]
[JsonDerivedType(typeof(SetGpuVfCurveCommand), "setGpuVfCurve")]
[JsonDerivedType(typeof(SetGpuMemoryOffsetCommand), "setGpuMemoryOffset")]
[JsonDerivedType(typeof(SetGpuCoreOffsetCommand), "setGpuCoreOffset")]
[JsonDerivedType(typeof(SetGpuVoltageBoostCommand), "setGpuVoltageBoost")]
[JsonDerivedType(typeof(SetGpuPowerLimitCommand), "setGpuPowerLimit")]
[JsonDerivedType(typeof(SetGpuPowerPolicyCommand), "setGpuPowerPolicy")]
[JsonDerivedType(typeof(SetAutomationProfileCommand), "setAutomationProfile")]
[JsonDerivedType(typeof(SetAdaptiveAutomationConfigurationCommand), "setAdaptiveAutomationConfiguration")]
[JsonDerivedType(typeof(UpdateAdaptiveAutomationContextCommand), "updateAdaptiveAutomationContext")]
public abstract record HardwareCommand(Guid OperationId);

public sealed record SetPerformanceModeCommand(Guid OperationId, PerformanceMode Mode) : HardwareCommand(OperationId);

public sealed record SetMuxModeCommand(Guid OperationId, MuxMode Mode, bool UserConfirmedRestartImpact) : HardwareCommand(OperationId);

public sealed record SetFanControlCommand(Guid OperationId, FanControlPlan Plan, bool RiskConfirmed) : HardwareCommand(OperationId);

public sealed record ReleaseFanControlCommand(Guid OperationId, ReleaseReason Reason) : HardwareCommand(OperationId);

public sealed record SetKeyboardLightingCommand(Guid OperationId, KeyboardLightingPlan Plan) : HardwareCommand(OperationId)
{
    public bool Preview { get; init; }
}

public sealed record RestoreKeyboardLightingPreviewCommand(Guid OperationId) : HardwareCommand(OperationId);

public sealed record SetLidLogoCommand(Guid OperationId, bool Enabled) : HardwareCommand(OperationId);

public sealed record SetQuickSettingCommand(Guid OperationId, QuickSettingKind Setting, bool Enabled) : HardwareCommand(OperationId);

public sealed record SetStrongCoolingCommand(Guid OperationId, bool Enabled) : HardwareCommand(OperationId);

public sealed record SetCpuTuningCommand(Guid OperationId, CpuTuningPlan Plan, bool RiskConfirmed) : HardwareCommand(OperationId);
public sealed record SetCpuTuningBatchCommand(Guid OperationId, CpuTuningPlan[] Plans, bool RiskConfirmed) : HardwareCommand(OperationId)
{
    public PerformanceMode? NativeMode { get; init; }
}

public sealed record SetGpuFrequencyLimitCommand(Guid OperationId, int? Megahertz, bool RiskConfirmed) : HardwareCommand(OperationId);
public sealed record SetGpuVoltageBoostCommand(Guid OperationId, uint ExpectedPercent, uint Percent, bool RiskConfirmed) : HardwareCommand(OperationId);
public sealed record SetGpuPowerLimitCommand(Guid OperationId, int Watts, bool RiskConfirmed) : HardwareCommand(OperationId);
public sealed record SetGpuPowerPolicyCommand(Guid OperationId, uint? ExpectedMilliPercent, uint MilliPercent, bool RiskConfirmed) : HardwareCommand(OperationId);

public sealed record SetGpuVfCurveCommand(Guid OperationId, int[] ExpectedOffsetsKhz, int[] OffsetsKhz, bool RiskConfirmed) : HardwareCommand(OperationId);

public sealed record SetGpuMemoryOffsetCommand(Guid OperationId, int ExpectedOffsetKhz, int OffsetKhz, bool RiskConfirmed) : HardwareCommand(OperationId);

public sealed record SetGpuCoreOffsetCommand(Guid OperationId, int ExpectedOffsetKhz, int OffsetKhz, bool RiskConfirmed) : HardwareCommand(OperationId)
{
    public int[]? ExpectedVfOffsetsKhz { get; init; }
}

public sealed record SetAutomationProfileCommand(Guid OperationId, AutomationProfile Profile) : HardwareCommand(OperationId);

public sealed record CpuTuningPlan(
    int? TemperatureLimitC,
    int? SplWatts,
    int? SpptWatts,
    int? MaxFrequencyMhz,
    bool? BoostEnabled,
    int? EnabledCoreCount,
    Guid? WindowsPowerSchemeId,
    int? NegativeCurveOptimizer)
{
    public int? AcMaxFrequencyMhz { get; init; }
    public int? DcMaxFrequencyMhz { get; init; }
    public int? AcMinActiveCoresPercent { get; init; }
    public int? DcMinActiveCoresPercent { get; init; }
    public AdvancedCpuTuningPlan? Advanced { get; init; }
}

public sealed record AdvancedCpuTuningPlan(
    double? StapmWatts = null,
    double? FastPptWatts = null,
    double? SlowPptWatts = null,
    double? PptWatts = null,
    int? VrmCurrentMilliamps = null,
    int? TdcCurrentMilliamps = null,
    int? EdcCurrentMilliamps = null,
    int? Mp1TemperatureC = null,
    int? RsmuTemperatureC = null,
    int? PboScalar = null,
    bool? OverclockEnabled = null,
    int? OcClockMhz = null,
    int? OcVoltageMillivolts = null,
    IReadOnlyDictionary<int, int>? PerCoreOcClockMhz = null,
    int? CurveOptimizerAll = null,
    IReadOnlyDictionary<int, int>? PerCoreCurveOptimizer = null);

public sealed record GpuLimitPlan(int? CoreFrequencyLimitMhz);

public sealed class LowerCamelEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is null)
        {
            throw new JsonException("Enum value must be a string.");
        }

        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(ToWireValue(value), text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new JsonException($"Unknown {typeof(TEnum).Name} value.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWireValue(value));

    private static string ToWireValue(TEnum value)
    {
        var name = value.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }
}
