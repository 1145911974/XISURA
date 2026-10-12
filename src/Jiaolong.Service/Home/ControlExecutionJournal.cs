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
}
public sealed record ControlJournalSnapshot(int SchemaVersion, HardwareIdentity? Identity,
    PerformanceMode? LastConfirmedMode, ControlExecutionState? Current,
    UnresolvedControlOperation[] Unresolved)
{
    public string? CurrentOwnerSid { get; init; }
}

// A crash leaves the pre-write marker intact. Old epochs remain evidence, never restoration authority.
public sealed partial class ControlExecutionJournal
{
    private const int MaxJournalBytes = 2 * 1024 * 1024;
    private readonly AtomicJsonStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ControlJournalSnapshot? snapshot;
    public ControlExecutionJournal(string? root = null) =>
        store = new(root ?? MachinePaths.BaseDirectory, "control-execution.json");

    public async Task<ControlJournalSnapshot> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Clone(await LoadAsync(token)); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ControlExecutionState state, HardwareIdentity identity,
        Guid operationId, bool pending, CancellationToken token, string? ownerSid = null, string? commandKind = null,
        bool noMutation = false)
    {
        await gate.WaitAsync(token);
        try
        {
            var prior = await LoadAsync(token);
            var unresolved = prior.Unresolved.ToDictionary(item => item.OperationId);
            bool unknown = !noMutation && (state.NativeModeApplyState == ApplyState.Unknown ||
                state.Pages.Any(page => page.State == ApplyState.Unknown) ||
                state.Fields.Values.Any(field => field.OwnerOperationId == operationId && field.WriteState == ApplyState.Unknown));
            if (pending || unknown)
            {
                unresolved.TryGetValue(operationId, out var existing);
                var entry = new UnresolvedControlOperation(operationId, DateTimeOffset.UtcNow, identity, state)
                { OwnerSid = ownerSid, CommandKind = commandKind, GpuRecovery = existing?.GpuRecovery };
                if (entry.GpuRecovery is { } gpu && !ValidGpuRecovery(gpu, entry))
                    throw new InvalidDataException("gpuRecoveryIdentityChanged");
                unresolved[operationId] = entry;
            }
            else unresolved.Remove(operationId);
            if (unresolved.Count > 128) throw new InvalidDataException("Unresolved control journal is full; verify pending operations before new writes.");
            var next = new ControlJournalSnapshot(1, identity,
                state.NativeModeApplyState == ApplyState.Confirmed ? state.NativeMode :
                    prior.Identity == identity ? prior.LastConfirmedMode : null,
                state, unresolved.Values.ToArray()) { CurrentOwnerSid = ownerSid };
            var frozen = Clone(next);
            await WriteSnapshotAsync(frozen, token);
            snapshot = frozen;
        }
        finally { gate.Release(); }
    }

    internal async Task ResolveNoMutationAsync(Guid operationId, ControlExecutionState? current, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var prior = await LoadAsync(token);
            var next = Clone(prior with { Current = current,
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
                item.GpuRecovery is { } gpu && !ValidGpuRecovery(gpu, item)) ||
            saved.Unresolved.Select(item => item.OperationId).Distinct().Count() != saved.Unresolved.Length ||
            saved.LastConfirmedMode is { } mode && !Enum.IsDefined(mode)))
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
}
