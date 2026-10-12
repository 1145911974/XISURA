using System.Text.Json;
using System.Text.Json.Serialization;
using Jiaolong.Contracts.Commands;

namespace Jiaolong.Contracts.Models;

[JsonConverter(typeof(LowerCamelEnumConverter<ControlOrigin>))]
public enum ControlOrigin { ManualMode, ManualLive, PresetUse, Adaptive, StartupRestore, TurboBranch }
[JsonConverter(typeof(LowerCamelEnumConverter<ApplyState>))]
public enum ApplyState { Pending, Confirmed, SubmittedUnverified, Partial, Failed, Superseded, Unknown }
[JsonConverter(typeof(LowerCamelEnumConverter<SettingIntent>))]
public enum SettingIntent { Keep, Official, Set }
[JsonConverter(typeof(LowerCamelEnumConverter<AutomationConfigurationIntent>))]
public enum AutomationConfigurationIntent { Enable, Disable, Update }

public sealed record SettingRequest(SettingIntent Intent, JsonElement? Value)
{
    public static SettingRequest Keep { get; } = new(SettingIntent.Keep, null);
    public static SettingRequest Official { get; } = new(SettingIntent.Official, null);
    public static SettingRequest Set<T>(T value) => new(SettingIntent.Set, JsonSerializer.SerializeToElement(value));
}

public sealed record PageSelection(ControlPageId Page, bool Follow, PresetKey Key, long Revision);
public sealed record PageApplyResult(ControlPageId Page, PresetKey? Key, long Revision,
    ApplyState State, string[] ConfirmedFields, string[] UnverifiedFields, string[] SkippedFields, string? ErrorCode);
public sealed record ControlRequest(Guid OperationId, long Generation, ControlOrigin Origin,
    ControlModeId LogicalMode, PerformanceMode NativeMode, PageSelection[] Pages);
public sealed record FieldExecutionState(Guid OwnerOperationId, ControlOrigin Origin,
    SettingRequest? Baseline, SettingRequest? LastWritten, ApplyState WriteState)
{
    public CpuFieldRestorationValue? BaselineDetails { get; init; }
    public CpuFieldRestorationValue? LastWrittenDetails { get; init; }
}
public sealed record CpuFieldRestorationValue(int? AcBoostMode, int? DcBoostMode, Dictionary<int, int>? PerCoreCurveOptimizer);
public sealed record ControlExecutionState(long Generation, long HardwareEpoch, long ConfigurationRevision,
    ControlModeId LogicalMode, PerformanceMode NativeMode, PageApplyResult[] Pages,
    Dictionary<string, FieldExecutionState> Fields)
{
    public ApplyState NativeModeApplyState { get; init; } = ApplyState.Pending;
}
public sealed record PublishPresetCatalogResult(long CatalogRevision);
