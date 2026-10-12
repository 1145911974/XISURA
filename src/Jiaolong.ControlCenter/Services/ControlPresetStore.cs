using System.Collections.Concurrent;
using System.Text.Json;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public interface IControlPresetStore
{
    Task<PagePresetEnvelope?> LoadAsync(ControlPageId page, PresetKey key, CancellationToken cancellationToken);
    Task SaveAsync(PagePresetEnvelope preset, CancellationToken cancellationToken);
}

public sealed class ControlPresetStore : IControlPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUserPreferencesPathProvider paths;
    private readonly Func<CapabilitySnapshot?>? capabilities;

    public ControlPresetStore(IUserPreferencesPathProvider? paths = null, Func<CapabilitySnapshot?>? capabilities = null)
    { this.paths = paths ?? new DefaultUserPreferencesPathProvider(); this.capabilities = capabilities; }

    public async Task<PagePresetEnvelope?> LoadAsync(ControlPageId page, PresetKey key, CancellationToken cancellationToken)
    {
        ValidateIdentity(page, key);
        cancellationToken.ThrowIfCancellationRequested();
        var path = PresetsPath(page);
        var gate = PathLocks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return ReadEntry(await ReadAsync(path, cancellationToken), page, key, path); }
        finally { gate.Release(); }
    }

    public Task<PagePresetEnvelope> ResetPerformanceAsync(PresetKey key, CancellationToken cancellationToken) =>
        ResetAsync(ControlPageId.Performance, key, cancellationToken);

    public async Task<PagePresetEnvelope> ResetAsync(ControlPageId page, PresetKey key, CancellationToken cancellationToken)
    {
        ValidateIdentity(page, key);
        cancellationToken.ThrowIfCancellationRequested();
        var path = PresetsPath(page);
        var gate = PathLocks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(path, cancellationToken);
            EnsureWritableVersion(stored);
            ArchiveInvalidEntries(stored, page, path);
            var current = ReadEntry(stored, page, key, path);
            var preset = DefaultControlPresets.Create(page, key, capabilities?.Invoke()) with
            { DisplayName = current.DisplayName, SavedAtUtc = DateTimeOffset.UtcNow, Revision = NextRevision(current.Revision) };
            stored[StorageKey(key)] = JsonSerializer.SerializeToElement(preset, JsonOptions);
            await WriteAsync(path, stored, cancellationToken);
            return preset;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(PagePresetEnvelope preset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ValidateIdentity(preset.Page, preset.Key);
        cancellationToken.ThrowIfCancellationRequested();
        var initial = PresetValidation.Validate(preset);
        if (!initial.IsValid) throw new InvalidOperationException(initial.Errors[0]);
        var snapshot = capabilities?.Invoke();
        var completed = DefaultControlPresets.Complete(preset, snapshot);
        var factory = DefaultControlPresets.Create(preset.Page, preset.Key, snapshot);
        bool factoryPayload = !preset.UserModified && preset.FactoryRevision == DefaultPerformancePresets.FactoryRevision
            && JsonElement.DeepEquals(completed.Payload, factory.Payload)
            && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(completed.Settings, JsonOptions),
                JsonSerializer.SerializeToElement(factory.Settings, JsonOptions));
        completed = completed with
        { UserModified = !factoryPayload, Settings = preset.Settings.Count > 0 ? completed.Settings : DefaultControlPresets.InferSettings(completed) };
        var validation = PresetValidation.Validate(completed);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0]);
        var path = PresetsPath(preset.Page);
        var gate = PathLocks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(path, cancellationToken);
            EnsureWritableVersion(stored);
            ArchiveInvalidEntries(stored, preset.Page, path);
            long previous = stored.TryGetValue(StorageKey(preset.Key), out var old) &&
                old.ValueKind == JsonValueKind.Object && old.TryGetProperty("revision", out var revision) &&
                revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var value) ? value : 0;
            completed = completed with { Revision = NextRevision(Math.Max(previous, completed.Revision)), Payload = completed.Payload.Clone(),
                Settings = new(completed.Settings, StringComparer.OrdinalIgnoreCase) };
            stored[StorageKey(preset.Key)] = JsonSerializer.SerializeToElement(completed, JsonOptions);
            await WriteAsync(path, stored, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> TryAdvanceRevisionAsync(PagePresetEnvelope expected, long revision, CancellationToken token)
    {
        ValidateIdentity(expected.Page, expected.Key);
        if (revision <= expected.Revision || revision == long.MaxValue || expected.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion) return false;
        var path = PresetsPath(expected.Page);
        var gate = PathLocks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var stored = await ReadAsync(path, token);
            EnsureWritableVersion(stored);
            var current = ReadEntry(stored, expected.Page, expected.Key, path);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(current, JsonOptions),
                JsonSerializer.SerializeToElement(expected, JsonOptions))) return false;
            // Only revision changes; migration, names, settings and unknown fields retain their saved representation.
            if (stored.TryGetValue(StorageKey(expected.Key), out var raw))
            {
                var fields = raw.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                fields["revision"] = JsonSerializer.SerializeToElement(revision);
                stored[StorageKey(expected.Key)] = JsonSerializer.SerializeToElement(fields, JsonOptions);
            }
            else stored[StorageKey(expected.Key)] = JsonSerializer.SerializeToElement(expected with { Revision = revision }, JsonOptions);
            await WriteAsync(path, stored, token);
            return true;
        }
        finally { gate.Release(); }
    }

    private static long NextRevision(long revision) => revision < long.MaxValue ? revision + 1 :
        throw new InvalidOperationException("预设修订号已达上限，原预设未修改。");

    private PagePresetEnvelope ReadEntry(Dictionary<string, JsonElement> stored, ControlPageId page, PresetKey key, string path)
    {
        if (!stored.TryGetValue(StorageKey(key), out var raw)) return DefaultControlPresets.Create(page, key, capabilities?.Invoke());
        string? name = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("displayName", out var displayName) && displayName.ValueKind == JsonValueKind.String
            ? displayName.GetString() : null;
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("schemaVersion", out var schema) &&
            schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version > PagePresetEnvelope.CurrentSchemaVersion)
            return new(version, page, key, name ?? "请升级应用", raw.TryGetProperty("payload", out var futurePayload) ? futurePayload.Clone() : default, DateTimeOffset.UnixEpoch);
        try
        {
            var preset = raw.Deserialize<PagePresetEnvelope>(JsonOptions) ?? throw new JsonException("Missing preset.");
            if (preset.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion) return preset;
            if (preset.Page != page || preset.Key != key || !PresetValidation.Validate(preset).IsValid) throw new JsonException("Invalid preset entry.");
            return DefaultControlPresets.Complete(preset, capabilities?.Invoke(), resolveFactory: true);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            BackupCorrupt(path, move: false);
            return DefaultControlPresets.Create(page, key, capabilities?.Invoke()) with
            { DisplayName = string.IsNullOrWhiteSpace(name) ? PresetNameCatalog.GetName(new Dictionary<string, string>(), key) : name };
        }
    }

    private static async Task<Dictionary<string, JsonElement>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid preset catalog.");
            return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException)
        { BackupCorrupt(path, move: true); return new(StringComparer.Ordinal); }
    }

    private static void EnsureWritableVersion(Dictionary<string, JsonElement> stored)
    {
        foreach (var entry in stored.Values)
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version > PagePresetEnvelope.CurrentSchemaVersion)
                throw new InvalidOperationException("预设来自更新版本，请升级应用；原文件未修改。");
    }

    private static void ArchiveInvalidEntries(Dictionary<string, JsonElement> stored, ControlPageId page, string path)
    {
        foreach (var (key, raw) in stored)
        {
            try
            {
                var preset = raw.Deserialize<PagePresetEnvelope>(JsonOptions);
                if (preset is not null && preset.Page == page && key == StorageKey(preset.Key) &&
                    PresetValidation.Validate(preset).IsValid) continue;
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException) { }
            // A repaired save may occur without reading this item first; .bak alone rotates away.
            BackupCorrupt(path, move: false);
            return;
        }
    }

    private async Task WriteAsync(string path, Dictionary<string, JsonElement> stored, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
            paths.RecordWrite(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void BackupCorrupt(string path, bool move)
    {
        if (!File.Exists(path)) return;
        try
        {
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var backup = path + ".corrupt-" + digest[..16];
            if (File.Exists(backup)) { if (move) File.Delete(path); return; }
            if (move) File.Move(path, backup); else File.Copy(path, backup);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("无法备份损坏的预设；原文件未覆盖。", error); }
    }

    private string PresetsPath(ControlPageId page) => Path.GetFullPath(Path.Combine(paths.LocalAppDataRoot,
        "Jiaolong Control Center", $"control-presets-{page.ToString().ToLowerInvariant()}.json"));
    private static string StorageKey(PresetKey key) => $"{key.Mode}:{key.Slot}";
    private static void ValidateIdentity(ControlPageId page, PresetKey key)
    { if (!Enum.IsDefined(page)) throw new ArgumentOutOfRangeException(nameof(page)); PresetKey.Create(key.Mode, key.Slot); }
}
