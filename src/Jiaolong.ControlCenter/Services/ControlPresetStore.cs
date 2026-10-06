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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IUserPreferencesPathProvider paths;

    public ControlPresetStore(IUserPreferencesPathProvider? paths = null) =>
        this.paths = paths ?? new DefaultUserPreferencesPathProvider();

    public async Task<PagePresetEnvelope?> LoadAsync(
        ControlPageId page,
        PresetKey key,
        CancellationToken cancellationToken)
    {
        ValidatePage(page);
        PresetKey.Create(key.Mode, key.Slot);
        cancellationToken.ThrowIfCancellationRequested();
        var presets = await LoadAllAsync(page, cancellationToken);
        return presets.TryGetValue(StorageKey(key), out var preset) && preset.Key == key
            ? DefaultControlPresets.Complete(preset) : DefaultControlPresets.Create(page, key);
    }

    public async Task<PagePresetEnvelope> ResetPerformanceAsync(PresetKey key, CancellationToken cancellationToken)
    {
        var existing = await LoadAsync(ControlPageId.Performance, key, cancellationToken);
        var preset = DefaultPerformancePresets.Create(key) with
        {
            DisplayName = existing!.DisplayName,
            SavedAtUtc = DateTimeOffset.UtcNow
        };
        await SaveAsync(preset, cancellationToken);
        return preset;
    }

    public async Task SaveAsync(PagePresetEnvelope preset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ValidatePage(preset.Page);
        PresetKey.Create(preset.Key.Mode, preset.Key.Slot);
        cancellationToken.ThrowIfCancellationRequested();

        var path = PresetsPath(preset.Page);
        var presets = await LoadAllAsync(preset.Page, cancellationToken);
        presets[StorageKey(preset.Key)] = preset with { Payload = preset.Payload.Clone() };

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Preset directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, presets, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
            paths.RecordWrite(path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task<Dictionary<string, PagePresetEnvelope>> LoadAllAsync(
        ControlPageId page,
        CancellationToken cancellationToken)
    {
        var path = PresetsPath(page);
        if (!File.Exists(path))
            return new Dictionary<string, PagePresetEnvelope>(StringComparer.Ordinal);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);
            var stored = await JsonSerializer.DeserializeAsync<Dictionary<string, PagePresetEnvelope>>(
                stream,
                JsonOptions,
                cancellationToken);
            return stored is null
                ? new Dictionary<string, PagePresetEnvelope>(StringComparer.Ordinal)
                : FilterValidPresets(page, stored);
        }
        catch (JsonException)
        {
            QuarantineCorruptFile(path);
            return new Dictionary<string, PagePresetEnvelope>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, PagePresetEnvelope> FilterValidPresets(
        ControlPageId page,
        Dictionary<string, PagePresetEnvelope> stored)
    {
        var valid = new Dictionary<string, PagePresetEnvelope>(StringComparer.Ordinal);
        foreach (var (storageKey, preset) in stored)
        {
            if (preset is null || preset.Page != page)
                continue;

            try
            {
                PresetKey.Create(preset.Key.Mode, preset.Key.Slot);
                valid[storageKey] = preset with { Payload = preset.Payload.Clone() };
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore an invalid future entry without losing valid slots.
            }
        }

        return valid;
    }

    private static string StorageKey(PresetKey key) => $"{key.Mode}:{key.Slot}";

    private void QuarantineCorruptFile(string path)
    {
        try
        {
            var quarantinePath = path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Move(path, quarantinePath, overwrite: false);
        }
        catch (IOException)
        {
            // A read-only or concurrently replaced preference file is treated as empty.
        }
        catch (UnauthorizedAccessException)
        {
            // The next save reports its own write error; startup remains available.
        }
    }

    private string PresetsPath(ControlPageId page) =>
        Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", $"control-presets-{page.ToString().ToLowerInvariant()}.json");

    private static void ValidatePage(ControlPageId page)
    {
        if (!Enum.IsDefined(page))
            throw new ArgumentOutOfRangeException(nameof(page), page, "未知的控制页面。");
    }
}
