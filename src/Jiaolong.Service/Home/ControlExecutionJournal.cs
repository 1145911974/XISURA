using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed record UnresolvedControlOperation(Guid OperationId, DateTimeOffset RecordedAtUtc,
    HardwareIdentity Identity, ControlExecutionState State)
{
    public string? OwnerSid { get; init; }
    public string? CommandKind { get; init; }
    public GpuRecoveryBaseline? GpuRecovery { get; init; }
    public GpuRecoveryAttempt? GpuAttempt { get; init; }
    public NativeModeRecoveryAttempt? NativeModeAttempt { get; init; }
}
public sealed record ControlJournalSnapshot(int SchemaVersion, HardwareIdentity? Identity,
    PerformanceMode? LastConfirmedMode, ControlExecutionState? Current,
    UnresolvedControlOperation[] Unresolved)
{
    public string? CurrentOwnerSid { get; init; }
    public ControlJournalRecoveryScope? RecoveryScope { get; init; }
    public FanEcRecoverySnapshot? FanEcRecovery { get; init; }
    public FanFirmwareRecoverySnapshot? FanFirmwareRecovery { get; init; }
}
public sealed record ControlFieldRecoveryScope(string? OwnerSid, long HardwareEpoch, Guid? WindowsPowerSchemeId, long Generation);
public sealed record ControlJournalRecoveryScope(Guid ServiceSessionId, Dictionary<string, ControlFieldRecoveryScope> Fields,
    PerformanceMode? NativeModeTarget, long NativeModeEpoch, Guid? FanOwnerClientSessionId);

// A crash leaves the pre-write marker intact. Old epochs remain evidence, never restoration authority.
public sealed partial class ControlExecutionJournal
{
    private const int MaxJournalBytes = 2 * 1024 * 1024;
    private readonly AtomicJsonStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ControlJournalSnapshot? snapshot;
    public ControlExecutionJournal(string? root = null) =>
        store = new(root ?? MachinePaths.BaseDirectory, "control-execution.json");
    internal string RootDirectory => Path.GetDirectoryName(store.FilePath)!;

    public async Task<ControlJournalSnapshot> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Clone(await LoadAsync(token)); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ControlExecutionState state, HardwareIdentity identity,
        Guid operationId, bool pending, CancellationToken token, string? ownerSid = null, string? commandKind = null,
        bool noMutation = false, ControlJournalRecoveryScope? recoveryScope = null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (recoveryScope is not null && !ValidRecoveryScope(recoveryScope, state))
                throw new InvalidDataException("controlRecoveryScopeInvalid");
            var prior = await LoadAsync(token);
            var unresolved = prior.Unresolved.ToDictionary(item => item.OperationId);
            bool unknown = !noMutation && (state.NativeModeApplyState == ApplyState.Unknown ||
                state.Pages.Any(page => page.State == ApplyState.Unknown) ||
                state.Fields.Values.Any(field => field.OwnerOperationId == operationId && field.WriteState == ApplyState.Unknown));
            if (pending || unknown)
            {
                unresolved.TryGetValue(operationId, out var existing);
                var entry = new UnresolvedControlOperation(operationId, DateTimeOffset.UtcNow, identity, state)
                { OwnerSid = ownerSid, CommandKind = commandKind, GpuRecovery = existing?.GpuRecovery, GpuAttempt = existing?.GpuAttempt,
                    NativeModeAttempt = existing?.NativeModeAttempt };
                if (entry.GpuRecovery is { } gpu && !ValidGpuRecovery(gpu, entry))
                    throw new InvalidDataException("gpuRecoveryIdentityChanged");
                if (entry.GpuAttempt is { } attempt && !ValidGpuAttempt(attempt, entry))
                    throw new InvalidDataException("gpuRecoveryAttemptChanged");
                if (entry.NativeModeAttempt is { } modeAttempt && !ValidNativeModeAttempt(modeAttempt, entry))
                    throw new InvalidDataException("nativeModeRecoveryAttemptChanged");
                unresolved[operationId] = entry;
            }
            else unresolved.Remove(operationId);
            if (unresolved.Count > 128) throw new InvalidDataException("Unresolved control journal is full; verify pending operations before new writes.");
            var next = new ControlJournalSnapshot(1, identity,
                state.NativeModeApplyState == ApplyState.Confirmed ? state.NativeMode :
                    prior.Identity == identity ? prior.LastConfirmedMode : null,
                state, unresolved.Values.ToArray()) { CurrentOwnerSid = ownerSid, RecoveryScope = recoveryScope,
                    FanEcRecovery = prior.FanEcRecovery, FanFirmwareRecovery = prior.FanFirmwareRecovery };
            var frozen = Clone(next);
            await WriteSnapshotAsync(frozen, token);
            snapshot = frozen;
        }
        finally { gate.Release(); }
    }

    internal async Task ResolveNoMutationAsync(Guid operationId, ControlExecutionState? current, CancellationToken token,
        ControlJournalRecoveryScope? recoveryScope = null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (recoveryScope is not null && !ValidRecoveryScope(recoveryScope, current))
                throw new InvalidDataException("controlRecoveryScopeInvalid");
            var prior = await LoadAsync(token);
            var next = Clone(prior with { Current = current,
                RecoveryScope = recoveryScope,
                Unresolved = prior.Unresolved.Where(item => item.OperationId != operationId).ToArray() });
            await WriteSnapshotAsync(next, token);
            snapshot = next;
        }
        finally { gate.Release(); }
    }

    public async Task<Guid[]> ReadUnresolvedOperationsAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return (await LoadAsync(token)).Unresolved.Select(item => item.OperationId).ToArray(); }
        finally { gate.Release(); }
    }

    private async Task<ControlJournalSnapshot> LoadAsync(CancellationToken token)
    {
        if (snapshot is not null) return snapshot;
        if (File.Exists(store.FilePath) && new FileInfo(store.FilePath).Length > MaxJournalBytes)
            throw new InvalidDataException("Control journal exceeds its size limit.");
        var saved = await store.ReadAsync<ControlJournalSnapshot>(token);
        if (saved is not null && (saved.SchemaVersion != 1 || saved.Unresolved is null || saved.Unresolved.Length > 128 ||
            saved.Unresolved.Any(item => item is null || item.OperationId == Guid.Empty || item.State is null ||
                item.State.Pages is null || item.State.Pages.Any(page => page is null) ||
                item.State.Fields is null || item.State.Fields.Values.Any(field => field is null) ||
                item.GpuRecovery is { } gpu && !ValidGpuRecovery(gpu, item) ||
                item.GpuAttempt is { } attempt && !ValidGpuAttempt(attempt, item) ||
                item.NativeModeAttempt is { } modeAttempt && !ValidNativeModeAttempt(modeAttempt, item)) ||
            saved.Unresolved.Select(item => item.OperationId).Distinct().Count() != saved.Unresolved.Length ||
            saved.LastConfirmedMode is { } mode && !Enum.IsDefined(mode) ||
            saved.RecoveryScope is { } scope && !ValidRecoveryScope(scope, saved.Current) ||
            saved.FanEcRecovery is { } fan && !ValidFanEcRecovery(fan) ||
            saved.FanFirmwareRecovery is { } firmware && !ValidFanFirmware(firmware)))
            throw new InvalidDataException("Control journal is invalid; original file retained.");
        return snapshot = saved ?? new(1, null, null, null, []);
    }

    private Task WriteSnapshotAsync(ControlJournalSnapshot value, CancellationToken token)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { WriteIndented = true }).Length > MaxJournalBytes)
            throw new InvalidDataException("Control journal exceeds its size limit; original file retained.");
        return store.WriteAsync(value, token);
    }

    private static ControlJournalSnapshot Clone(ControlJournalSnapshot value) =>
        JsonSerializer.Deserialize<ControlJournalSnapshot>(JsonSerializer.Serialize(value))!;

    private static bool ValidRecoveryScope(ControlJournalRecoveryScope scope, ControlExecutionState? current) =>
        scope.ServiceSessionId != Guid.Empty && scope.Fields is not null && current is { HardwareEpoch: > 0, Fields: not null } &&
        scope.FanOwnerClientSessionId != Guid.Empty && scope.Fields.Count == current.Fields.Count &&
        scope.Fields.All(pair => pair.Value is { HardwareEpoch: > 0, Generation: >= 0 } field &&
            field.HardwareEpoch <= current.HardwareEpoch && field.WindowsPowerSchemeId != Guid.Empty &&
            current.Fields.TryGetValue(pair.Key, out var value) && value is not null && value.ClientSessionId != Guid.Empty &&
            (value.ClientSessionId is null || !string.IsNullOrWhiteSpace(field.OwnerSid))) &&
        (current.NativeModeOwnerClientSessionId is null
            ? scope.NativeModeTarget is null && scope.NativeModeEpoch == 0
            : current.NativeModeOwnerClientSessionId != Guid.Empty && scope.NativeModeTarget is { } mode && Enum.IsDefined(mode) &&
                scope.NativeModeEpoch > 0 && scope.NativeModeEpoch <= current.HardwareEpoch);
}
