using System.Text.Json.Serialization;
using Jiaolong.Contracts.Commands;

namespace Jiaolong.Contracts.Models;

[JsonConverter(typeof(LowerCamelEnumConverter<AdaptiveAutomationStrategyId>))]
public enum AdaptiveAutomationStrategyId { QuietFirst, BalancedAdaptive, ResponseFirst }

[JsonConverter(typeof(LowerCamelEnumConverter<AdaptiveAutomationStageId>))]
public enum AdaptiveAutomationStageId { Office, Game, Turbo }

public sealed record AdaptiveAutomationApplicationRule(string Executable, bool ForegroundOnly, AdaptiveAutomationStageId Target)
{
    public bool RequireActivity { get; init; } = true;
}

public sealed record AdaptiveAutomationPolicy(
    int GameCpuPercent, int GameGpuPercent, int GameSeconds,
    bool TurboEnabled, int TurboCpuPercent, int TurboGpuPercent, int TurboSeconds,
    int OfficeCpuPercent, int OfficeGpuPercent, int OfficeSeconds,
    int LowBatteryPercent, int BatteryRecoveryPercent, bool RespectBatterySaver,
    int CpuTemperatureCeiling, int GpuTemperatureCeiling, int CooldownSeconds,
    int MinimumDwellSeconds, int ApplicationSeconds, bool IdleReturnEnabled,
    int IdleSeconds, AdaptiveAutomationApplicationRule[] ApplicationRules)
{
    public int TurboExitCpuPercent { get; init; } = 70;
    public int TurboExitGpuPercent { get; init; } = 75;
    public int TurboExitSeconds { get; init; } = 12;
    public int TurboMinimumDwellSeconds { get; init; } = 12;
}

public sealed record AdaptiveAutomationTargetMap(PresetKey AcOffice, PresetKey AcGame, PresetKey AcTurbo, PresetKey DcOffice, PresetKey DcGame);

// Preserve the serialized CPU property name; it also carries verified PBO Scalar and CO.
// Legacy CPU snapshots remain readable; Pages carries independently followed page plans.
public sealed record AdaptiveAutomationPreset(PresetKey Key, PerformanceMode PerformanceMode, CpuTuningPlan? CpuWindowsTuning)
{
    public PagePresetEnvelope[] Pages { get; init; } = [];
    public PageSelection[] Selections { get; init; } = [];
}

public sealed record AdaptiveAutomationConfiguration(
    bool Enabled,
    AdaptiveAutomationStrategyId Strategy,
    AdaptiveAutomationPolicy Policy,
    AdaptiveAutomationTargetMap TargetMap,
    AdaptiveAutomationPreset[] Presets)
{
    public long Revision { get; init; }
    public long CatalogRevision { get; init; }
}

public sealed record AdaptiveAutomationClientContext(
    DateTimeOffset CapturedAtUtc,
    string? ForegroundExecutable,
    int? IdleSeconds,
    bool? BatterySaver)
{
    public string[]? RunningExecutables { get; init; }
}

public sealed record AdaptiveAutomationStatus(
    bool Enabled,
    bool Running,
    PresetKey? CurrentTarget,
    string? Reason,
    DateTimeOffset? LastEvaluationUtc,
    DateTimeOffset? LastApplyUtc,
    string? LastError,
    string? OwnerConfigurationIdentity)
{
    public PresetKey? CandidateTarget { get; init; }
    public long Revision { get; init; }
    public PresetKey? SubmittedTarget { get; init; }
    public ApplyState? TargetApplyState { get; init; }
}
