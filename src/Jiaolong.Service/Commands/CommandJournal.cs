using System.Text.Json;
using Jiaolong.Contracts.Commands;

namespace Jiaolong.Service.Commands;

public interface ICommandJournal
{
    Task<JournalClaim> ClaimAsync(Guid operationId, string requestHash, CancellationToken cancellationToken);
    Task CompleteAsync(Guid operationId, CommandResult result, CancellationToken cancellationToken);
}

public sealed record JournalClaim(
    Guid OperationId,
    string RequestHash,
    bool IsReplay,
    bool IsInProgress,
    bool IsConflict,
    CommandResult? Result);

public sealed class CommandJournal : ICommandJournal
{
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    public CommandJournal(string? root = null)
    {
        this.root = root ?? Path.Combine(Jiaolong.Service.Storage.MachinePaths.BaseDirectory, "Commands");
        Jiaolong.Service.Storage.MachinePaths.EnsureSafeDirectory(this.root);
    }

    public async Task<JournalClaim> ClaimAsync(Guid operationId, string requestHash, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("OperationId must not be empty.", nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = EntryPath(operationId);
            Jiaolong.Service.Storage.MachinePaths.EnsureSafeFile(path);
            if (File.Exists(path))
            {
                var existing = await ReadEntryAsync(path, cancellationToken);
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new JournalClaim(operationId, requestHash, false, false, true, null);
                }

                return existing.Result is null
                    ? new JournalClaim(operationId, requestHash, false, true, false, null)
                    : new JournalClaim(operationId, requestHash, true, false, false, existing.Result);
            }

            var entry = new JournalEntry(operationId, requestHash, null);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, options);
            try
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
                return new JournalClaim(operationId, requestHash, false, false, false, null);
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = await ReadEntryAsync(path, cancellationToken);
                return !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? new JournalClaim(operationId, requestHash, false, false, true, null)
                    : existing.Result is null
                        ? new JournalClaim(operationId, requestHash, false, true, false, null)
                        : new JournalClaim(operationId, requestHash, true, false, false, existing.Result);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CompleteAsync(Guid operationId, CommandResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = EntryPath(operationId);
            if (!File.Exists(path)) throw new FileNotFoundException("Command journal claim was not found.", path);
            var existing = await ReadEntryAsync(path, cancellationToken);
            var store = new Jiaolong.Service.Storage.AtomicJsonStore(root, Path.GetFileName(path));
            await store.WriteAsync(existing with { Result = result }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private string EntryPath(Guid operationId) => Path.Combine(root, $"{operationId:D}.json");

    private async Task<JournalEntry> ReadEntryAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<JournalEntry>(stream, options, cancellationToken)
            ?? throw new InvalidDataException("Command journal entry is empty.");
    }

    private sealed record JournalEntry(Guid OperationId, string RequestHash, CommandResult? Result);
}
