using System.Text.Json;
using System.Text.Json.Serialization;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Contracts.Protocol;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    MaxDepth = 32)]
[JsonSerializable(typeof(ProtocolVersion))]
[JsonSerializable(typeof(MessageEnvelope))]
[JsonSerializable(typeof(HelloEnvelope))]
[JsonSerializable(typeof(HelloAckEnvelope))]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(ResponseEnvelope))]
[JsonSerializable(typeof(EventEnvelope))]
[JsonSerializable(typeof(CancelEnvelope))]
[JsonSerializable(typeof(PingEnvelope))]
[JsonSerializable(typeof(PongEnvelope))]
[JsonSerializable(typeof(HardwareCommand))]
[JsonSerializable(typeof(SetPerformanceModeCommand))]
[JsonSerializable(typeof(SetMuxModeCommand))]
[JsonSerializable(typeof(SetFanControlCommand))]
[JsonSerializable(typeof(ReleaseFanControlCommand))]
[JsonSerializable(typeof(SetKeyboardLightingCommand))]
[JsonSerializable(typeof(RestoreKeyboardLightingPreviewCommand))]
[JsonSerializable(typeof(SetLidLogoCommand))]
[JsonSerializable(typeof(SetQuickSettingCommand))]
[JsonSerializable(typeof(SetStrongCoolingCommand))]
[JsonSerializable(typeof(SetCpuTuningCommand))]
[JsonSerializable(typeof(SetGpuFrequencyLimitCommand))]
[JsonSerializable(typeof(SetGpuVfCurveCommand))]
[JsonSerializable(typeof(SetGpuMemoryOffsetCommand))]
[JsonSerializable(typeof(SetGpuCoreOffsetCommand))]
[JsonSerializable(typeof(SetGpuVoltageBoostCommand))]
[JsonSerializable(typeof(SetGpuPowerLimitCommand))]
[JsonSerializable(typeof(SetGpuPowerPolicyCommand))]
[JsonSerializable(typeof(SetAutomationProfileCommand))]
[JsonSerializable(typeof(SetAdaptiveAutomationConfigurationCommand))]
[JsonSerializable(typeof(UpdateAdaptiveAutomationContextCommand))]
[JsonSerializable(typeof(AdaptiveAutomationConfiguration))]
[JsonSerializable(typeof(AdaptiveAutomationPolicy))]
[JsonSerializable(typeof(AdaptiveAutomationTargetMap))]
[JsonSerializable(typeof(AdaptiveAutomationPreset[]))]
[JsonSerializable(typeof(AdaptiveAutomationApplicationRule[]))]
[JsonSerializable(typeof(AdaptiveAutomationClientContext))]
[JsonSerializable(typeof(AdaptiveAutomationStatus))]
[JsonSerializable(typeof(AdaptiveAutomationStrategyId))]
[JsonSerializable(typeof(AdaptiveAutomationStageId))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(CpuTuningPlan))]
[JsonSerializable(typeof(GpuLimitPlan))]
[JsonSerializable(typeof(FanControlPlan))]
[JsonSerializable(typeof(KeyboardLightingPlan))]
[JsonSerializable(typeof(AutomationProfile))]
[JsonSerializable(typeof(ControlModeId))]
[JsonSerializable(typeof(ControlPageId))]
[JsonSerializable(typeof(PresetKey))]
[JsonSerializable(typeof(PagePresetEnvelope))]
[JsonSerializable(typeof(CapabilitySnapshot))]
[JsonSerializable(typeof(HardwareIdentity))]
[JsonSerializable(typeof(QuickSettingStatus))]
[JsonSerializable(typeof(HomeControlState))]
[JsonSerializable(typeof(HomeStateSnapshot))]
[JsonSerializable(typeof(HardwareSnapshot))]
[JsonSerializable(typeof(CpuTuningState))]
[JsonSerializable(typeof(GpuVfState))]
[JsonSerializable(typeof(GpuVfNode))]
[JsonSerializable(typeof(ServiceError))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class ProtocolJsonContext : JsonSerializerContext;
