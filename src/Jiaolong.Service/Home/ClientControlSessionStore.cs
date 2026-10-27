using System.Text.Json;
using System.Text.Json.Serialization;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed record ClientControlBaseline(PerformanceMode? PerformanceMode, CpuTuningState? CpuTuning,
    GpuVfState? GpuVf, KeyboardLightingPlan? KeyboardLighting, bool? StrongCooling, FanControlPlan? ActiveFanControlPlan)
{
    public QuickSettingStatus[] QuickSettings { get; init; } = [];
    public OemCpuConfigurationSnapshot? OemCpuConfiguration { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClientControlSessionPhase>))]
public enum ClientControlSessionPhase { Active, Closing, Preparing, Completed }

public sealed record ClientControlSessionSnapshot(int SchemaVersion, Guid ServiceSessionId,
    ClientControlSessionHandle Handle, string OwnerSid, int OwnerSessionId, int ProcessId,
    long ProcessCreationFileTimeUtc, HardwareIdentity Identity, ClientControlBaseline Baseline)
{
    public ClientControlSessionPhase Phase { get; init; }
    public ExitDeviceDefaults? ExitDefaults { get; init; }
    public DateTimeOffset? BootStartedAtUtc { get; init; }
    public PowerContinuityEvidence? PowerContinuity { get; init; }
    public string? PreviousCompletedArchiveSha256 { get; init; }
}

internal sealed class ClientControlSessionStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AtomicJsonStore store = new(root, "client-control-session.json");

    public async Task<ClientControlSessionSnapshot?> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(store.FilePath)) return null;
        if (new FileInfo(store.FilePath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Client session snapshot exceeds its size limit.");
        var saved = await store.ReadAsync<ClientControlSessionSnapshot>(token);
        Validate(saved);
        return saved;
    }

    internal static bool SameCapture(ClientControlSessionSnapshot left, ClientControlSessionSnapshot right) =>
        JsonSerializer.Serialize(left with { Phase = right.Phase }, Json) == JsonSerializer.Serialize(right, Json);

    internal static bool ValidDefaults(ExitDeviceDefaults? defaults, string deviceKey) => defaults is null ||
        defaults.SchemaVersion == 1 && defaults.DeviceKey == deviceKey && defaults.CurveOptimizerAll is null or >= -50 and <= 0;

    public async Task<ClientControlSessionSnapshot> WriteAsync(ClientControlSessionSnapshot value, CancellationToken token,
        bool requireExisting = false, bool configureDefaults = false, Guid? completedSessionId = null)
    {
        Validate(value);
        // The provider may reuse mutable dictionaries/arrays on its next read.
        var frozen = JsonSerializer.Deserialize<ClientControlSessionSnapshot>(JsonSerializer.Serialize(value, Json), Json)!;
        var prior = await ReadAsync(token);
        bool replacing = completedSessionId is not null && prior?.Phase == ClientControlSessionPhase.Completed &&
            prior.Handle.SessionId == completedSessionId && frozen.Handle.SessionId != completedSessionId &&
            frozen.Phase is ClientControlSessionPhase.Preparing or ClientControlSessionPhase.Active;
        bool configuring = configureDefaults && prior?.Phase == ClientControlSessionPhase.Preparing && frozen.Phase == ClientControlSessionPhase.Active;
        var comparable = configuring ? prior! with { ExitDefaults = frozen.ExitDefaults } : prior;
        if (prior is null && (requireExisting || completedSessionId is not null) || !replacing && comparable is not null && !SameCapture(comparable, frozen))
            throw new InvalidDataException("Client session capture changed or disappeared; original retained.");
        if (prior?.Phase == frozen.Phase) return prior;
        bool transition = replacing || prior is null && (frozen.Phase is ClientControlSessionPhase.Preparing or ClientControlSessionPhase.Active) ||
            configuring || (prior?.Phase is ClientControlSessionPhase.Preparing or ClientControlSessionPhase.Active) && frozen.Phase == ClientControlSessionPhase.Closing ||
            prior?.Phase == ClientControlSessionPhase.Closing && frozen.Phase == ClientControlSessionPhase.Completed;
        if (!transition) throw new InvalidDataException("Client session transition is invalid.");
        await store.WriteAsync(frozen, token);
        return frozen;
    }

    private static void Validate(ClientControlSessionSnapshot? value)
    {
        if (value is not { SchemaVersion: 1, Handle: not null, Identity: not null, Baseline: not null } ||
            !Enum.IsDefined(value.Phase) || value.ServiceSessionId == Guid.Empty || value.Handle.SessionId == Guid.Empty || value.Handle.HardwareEpoch <= 0 ||
            !DeviceRecoveryKey.IsValid(value.Handle.DeviceKey) || !ValidDefaults(value.ExitDefaults, value.Handle.DeviceKey) ||
            value.PreviousCompletedArchiveSha256 is { } hash && (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) ||
            value.Phase == ClientControlSessionPhase.Preparing && value.ExitDefaults is not null || value.Handle.CapturedAtUtc <= DateTimeOffset.UnixEpoch ||
            value.Handle.CapturedAtUtc > DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(value.OwnerSid) ||
            value.OwnerSessionId < 0 || value.ProcessId <= 0 || value.ProcessCreationFileTimeUtc <= 0 ||
            value.ProcessCreationFileTimeUtc > DateTime.UtcNow.ToFileTimeUtc() ||
            value.Baseline.OemCpuConfiguration is { } oem &&
                (oem.CapturedAtUtc <= DateTimeOffset.UnixEpoch || oem.CapturedAtUtc > value.Handle.CapturedAtUtc) ||
            value.BootStartedAtUtc is { } boot && (boot <= DateTimeOffset.UnixEpoch || boot > value.Handle.CapturedAtUtc ||
                boot > DateTimeOffset.FromFileTime(value.ProcessCreationFileTimeUtc)) ||
            value.PowerContinuity is not null && !PowerContinuityEvidence.Valid(value.PowerContinuity))
            throw new InvalidDataException("Client session snapshot is invalid; original retained.");
    }
}
