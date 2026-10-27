using System.Text.Json;

namespace Jiaolong.Service.Storage;

public sealed class AtomicJsonStore
{
    private const int MaxBackups = 3;
    private readonly string root;
    private readonly string fileName;
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Action? beforePublish;

    public AtomicJsonStore(string root, string fileName) : this(root, fileName, null) { }

    internal AtomicJsonStore(string root, string fileName, Action? beforePublish)
    {
        MachinePaths.EnsureSafeDirectory(root);
        if (Path.GetFileName(fileName) != fileName || string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("The store file must be a leaf name.", nameof(fileName));
        }

        this.root = root;
        this.fileName = fileName;
        this.beforePublish = beforePublish;
        EnsureCurrentNotLost();
    }

    public string FilePath => Path.Combine(root, fileName);

    public async Task WriteAsync<T>(T value, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            MachinePaths.EnsureSafeDirectory(root);
            MachinePaths.EnsureSafeFile(FilePath);
            EnsureCurrentNotLost();
            bool replacing = File.Exists(FilePath);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
            var tempPath = Path.Combine(root, $".{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough | FileOptions.SequentialScan))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(true);
                }

                RotateBackups();
                beforePublish?.Invoke();
                if (replacing) File.Replace(tempPath, FilePath, BackupPath(1));
                else File.Move(tempPath, FilePath);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T?> ReadAsync<T>(CancellationToken cancellationToken)
    {
        MachinePaths.EnsureSafeDirectory(root);
        MachinePaths.EnsureSafeFile(FilePath);
        EnsureCurrentNotLost();
        if (!File.Exists(FilePath)) return default;
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken);
    }

    private void RotateBackups()
    {
        for (var index = MaxBackups; index >= 1; index--)
        {
            var current = BackupPath(index);
            MachinePaths.EnsureSafeFile(current);
            if (!File.Exists(current)) continue;

            if (index == MaxBackups)
            {
                File.Delete(current);
            }
            else
            {
                File.Move(current, BackupPath(index + 1), true);
            }
        }
    }

    private void EnsureCurrentNotLost()
    {
        if (!File.Exists(FilePath) && Enumerable.Range(1, MaxBackups).Any(index => File.Exists(BackupPath(index))))
            throw new IOException("Current store file is missing but recovery backups remain; original evidence retained.");
    }

    private string BackupPath(int index) => Path.Combine(root, $"{fileName}.{index}.bak");
}
