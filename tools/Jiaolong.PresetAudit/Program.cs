using System.Security.Cryptography;
using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

bool simulateSave = args.Length == 2 && args[1] == "--simulate-save";
if (args.Length is < 1 or > 2 || (args.Length == 2 && !simulateSave) ||
    !Path.IsPathFullyQualified(args[0]) || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Jiaolong.PresetAudit <absolute existing local config directory> [--simulate-save]");
    return 2;
}

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var files = new Dictionary<string, string>();
var errors = new List<string>();
var migrations = new List<object>();
int storedCount = 0, defaultCount = 0, legacyCount = 0, modifiedCount = 0, preservedModifiedCount = 0;
int savedCopyCount = 0, preservedCopyCount = 0;
bool scratchCleaned = false;

foreach (var page in Enum.GetValues<ControlPageId>())
{
    var path = Path.Combine(args[0], $"control-presets-{page.ToString().ToLowerInvariant()}.json");
    if (!File.Exists(path)) { errors.Add($"Missing installed catalog: {page}"); continue; }
    var bytes = await File.ReadAllBytesAsync(path);
    files[path] = Convert.ToHexString(SHA256.HashData(bytes));
    using var document = JsonDocument.Parse(bytes);
    if (document.RootElement.ValueKind != JsonValueKind.Object)
    { errors.Add($"Invalid catalog root: {page}"); continue; }
    var stored = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    foreach (var key in PresetKey.All)
    {
        var storageKey = $"{key.Mode}:{key.Slot}";
        PagePresetEnvelope resolved;
        if (stored.TryGetValue(storageKey, out var raw))
        {
            storedCount++;
            try
            {
                var original = raw.Deserialize<PagePresetEnvelope>(options) ?? throw new JsonException("Missing preset");
                var initial = PresetValidation.Validate(original);
                if (original.Page != page || original.Key != key || !initial.IsValid)
                    throw new InvalidDataException(string.Join("; ", initial.Errors.Prepend("Identity or payload invalid")));
                resolved = DefaultControlPresets.Complete(original, resolveFactory: true);
                if (original.SchemaVersion < PagePresetEnvelope.CurrentSchemaVersion) legacyCount++;
                if (original.UserModified)
                {
                    modifiedCount++;
                    bool preserved = resolved.UserModified && resolved.DisplayName == original.DisplayName &&
                        resolved.SavedAtUtc == original.SavedAtUtc && resolved.Revision >= original.Revision &&
                        original.Payload.EnumerateObject().All(property =>
                            resolved.Payload.TryGetProperty(property.Name, out var value) && JsonElement.DeepEquals(property.Value, value)) &&
                        original.Settings.All(setting => resolved.Settings.TryGetValue(setting.Key, out var value) &&
                            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(setting.Value, options),
                                JsonSerializer.SerializeToElement(value, options)));
                    if (preserved) preservedModifiedCount++;
                    else errors.Add($"Modified preset changed during migration: {page}/{storageKey}");
                }
                if (original.SchemaVersion != resolved.SchemaVersion || original.UserModified != resolved.UserModified)
                    migrations.Add(new { page, key = storageKey, fromSchema = original.SchemaVersion,
                        toSchema = resolved.SchemaVersion, fromModified = original.UserModified, toModified = resolved.UserModified });
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            { errors.Add($"{page}/{storageKey}: {error.Message}"); continue; }
        }
        else
        {
            defaultCount++;
            resolved = DefaultControlPresets.Create(page, key);
        }
        var validation = PresetValidation.Validate(resolved);
        if (!validation.IsValid) errors.Add($"{page}/{storageKey} after load: {string.Join("; ", validation.Errors)}");
    }
    foreach (var extra in stored.Keys.Except(PresetKey.All.Select(key => $"{key.Mode}:{key.Slot}")))
        errors.Add($"Unknown stored key: {page}/{extra}");
}

if (simulateSave && errors.Count == 0)
{
    var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
    var scratchPath = Path.Combine(tempRoot, "JiaolongPresetAudit-" + Guid.NewGuid().ToString("N"));
    if (!tempRoot.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(tempRoot) ||
        Directory.Exists(scratchPath))
        throw new InvalidOperationException("Exclusive C: scratch directory unavailable.");
    var scratch = Directory.CreateDirectory(scratchPath);
    try
    {
        if (!scratch.FullName.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scratch directory must be on C:.");
        var copyDirectory = Path.Combine(scratch.FullName, "Jiaolong Control Center");
        Directory.CreateDirectory(copyDirectory);
        foreach (var path in files.Keys)
            File.Copy(path, Path.Combine(copyDirectory, Path.GetFileName(path)));
        var copyStore = new ControlPresetStore(new AuditCopyPaths(scratch.FullName));
        foreach (var page in Enum.GetValues<ControlPageId>())
            foreach (var key in PresetKey.All)
            {
                var before = await copyStore.LoadAsync(page, key, CancellationToken.None)
                    ?? throw new InvalidDataException($"Copy load missing: {page}/{key}");
                await copyStore.SaveAsync(before, CancellationToken.None);
                savedCopyCount++;
                var after = await copyStore.LoadAsync(page, key, CancellationToken.None)
                    ?? throw new InvalidDataException($"Copy save missing: {page}/{key}");
                bool preserved = after.Page == before.Page && after.Key == before.Key &&
                    after.SchemaVersion == before.SchemaVersion && after.FactoryRevision == before.FactoryRevision &&
                    after.UserModified == before.UserModified && after.DisplayName == before.DisplayName &&
                    after.SavedAtUtc == before.SavedAtUtc && after.Revision > before.Revision &&
                    JsonElement.DeepEquals(after.Payload, before.Payload) &&
                    JsonElement.DeepEquals(JsonSerializer.SerializeToElement(after.Settings, options),
                        JsonSerializer.SerializeToElement(before.Settings, options)) &&
                    PresetValidation.Validate(after).IsValid;
                if (preserved) preservedCopyCount++;
                else errors.Add($"Copy save changed preset content: {page}/{key.Mode}:{key.Slot}");
            }
    }
    catch (Exception error)
    { errors.Add($"Copy save failed: {error.Message}"); }
    finally
    {
        // Only this run's exclusively created C: scratch directory contains generated copies.
        if (scratch.FullName.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
            try { scratch.Delete(recursive: true); scratchCleaned = true; }
            catch (Exception error) { errors.Add($"Scratch cleanup failed: {error.Message}"); }
    }
}

foreach (var (path, originalHash) in files)
    if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) != originalHash)
        errors.Add($"Source changed during audit: {Path.GetFileName(path)}");

Console.WriteLine(JsonSerializer.Serialize(new
{
    capturedAtUtc = DateTimeOffset.UtcNow,
    sourceFiles = files.Select(item => new { name = Path.GetFileName(item.Key), sha256 = item.Value }),
    expected = 72, storedCount, defaultCount, legacyCount, modifiedCount, preservedModifiedCount,
    simulateSave, savedCopyCount, preservedCopyCount, scratchCleaned, migrationChanges = migrations, errors
}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
return errors.Count == 0 && storedCount + defaultCount == 72 &&
    (!simulateSave || savedCopyCount == 72 && preservedCopyCount == 72 && scratchCleaned) ? 0 : 1;

sealed class AuditCopyPaths(string root) : IUserPreferencesPathProvider
{
    public string LocalAppDataRoot => root;
    public string ProgramDataRoot => root;
    public void RecordWrite(string path) { }
}
