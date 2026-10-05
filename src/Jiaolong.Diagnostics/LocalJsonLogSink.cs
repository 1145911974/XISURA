using System.Text;
using System.Text.Json;

namespace Jiaolong.Diagnostics;

public sealed class LocalJsonLogSink : IDiagnosticEventWriter, IDiagnosticEventReader, IAsyncDisposable
{
    private readonly string directory;
    private readonly string logPath;
    private readonly string commandJournalPath;
    private readonly long maxBytes;
    private readonly TimeSpan retention;
    private readonly SemaphoreSlim gate = new(1, 1);

    public LocalJsonLogSink(
        string directory,
        long maxBytes = 200L * 1024 * 1024,
        TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        this.maxBytes = maxBytes;
        this.retention = retention ?? TimeSpan.FromDays(14);
        logPath = Path.Combine(this.directory, "events.ndjson");
        commandJournalPath = Path.Combine(this.directory, "command-journal.ndjson");
    }

    public async ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (!DiagnosticEventNames.All.Contains(diagnosticEvent.EventName))
        {
            throw new ArgumentException($"Unknown diagnostic event '{diagnosticEvent.EventName}'.", nameof(diagnosticEvent));
        }

        var redacted = RedactionPolicy.RedactEvent(diagnosticEvent);
        var line = JsonSerializer.Serialize(redacted) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            RejectReparsePoint(directory);
            await AppendAsync(logPath, bytes, maxBytes, retention, "events-*.ndjson", cancellationToken);
            if (diagnosticEvent.EventName.StartsWith("command.", StringComparison.Ordinal))
            {
                await AppendAsync(commandJournalPath, bytes, maxBytes, TimeSpan.FromDays(7), "command-journal-*.ndjson", cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> ReadRecentAsync(int maximumEntries, CancellationToken cancellationToken)
    {
        if (maximumEntries is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(logPath)) return string.Empty;
            var entries = new Queue<string>(maximumEntries);
            await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0) continue;
                if (entries.Count == maximumEntries) entries.Dequeue();
                entries.Enqueue(line);
            }

            return string.Join(Environment.NewLine, entries);
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task AppendAsync(
        string path,
        byte[] bytes,
        long fileBudget,
        TimeSpan fileRetention,
        string rotatedPattern,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > fileBudget)
        {
            var rotatedPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.ndjson");
            File.Move(path, rotatedPath, overwrite: false);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.WriteThrough | FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var cutoff = DateTime.UtcNow - fileRetention;
        foreach (var rotatedPath in Directory.EnumerateFiles(directory, rotatedPattern))
        {
            if (File.GetLastWriteTimeUtc(rotatedPath) < cutoff)
            {
                File.Delete(rotatedPath);
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Diagnostic log directory cannot be a reparse point.");
        }
    }
}
