using System.Text.Json;
using System.Collections.Concurrent;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public sealed class PerformancePresetStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUserPreferencesPathProvider paths;

    public PerformancePresetStore(IUserPreferencesPathProvider? paths = null) =>
        this.paths = paths ?? new DefaultUserPreferencesPathProvider();

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<PerformanceDraft>>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(PresetsPath);
        var gate = PathLocks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
        if (!File.Exists(PresetsPath))
            return new Dictionary<string, IReadOnlyList<PerformanceDraft>>(StringComparer.Ordinal);

        await using var stream = new FileStream(PresetsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<PerformanceDraft>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var items = property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray().ToArray() : [property.Value];
            var drafts = new List<PerformanceDraft>();
            foreach (var item in items)
            {
                try { drafts.Add((item.Deserialize<StoredPreset>() ?? throw new JsonException("Missing preset.")).ToDraft()); }
                catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                {
                    var backup = path + ".corrupt-legacy";
                    if (!File.Exists(backup)) File.Copy(path, backup);
                    drafts.Add(new PerformanceDraft { UseOfficialCpuPolicy = true, NegativeCurveOptimizer = null });
                }
            }
            result[property.Name] = drafts;
        }

        return result;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, IReadOnlyList<PerformanceDraft>> presets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presets);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var draft in presets.Values.SelectMany(items => items))
        {
            var validation = PerformanceDraftValidator.Validate(draft);
            if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0]);
        }
        var path = Path.GetFullPath(PresetsPath);
        var gate = PathLocks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
        var directory = Path.GetDirectoryName(PresetsPath) ?? throw new InvalidOperationException("Preset directory is unavailable.");
        Directory.CreateDirectory(directory);
        var tempPath = PresetsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var stored = presets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(StoredPreset.FromDraft).ToArray(),
            StringComparer.Ordinal);
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(PresetsPath)) File.Replace(tempPath, PresetsPath, PresetsPath + ".bak");
            else File.Move(tempPath, PresetsPath);
            paths.RecordWrite(PresetsPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        }
        finally { gate.Release(); }
    }

    private string PresetsPath => Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "performance-presets.json");

    private sealed record StoredPreset(
        int TemperatureLimitC,
        int SplWatts,
        int SpptWatts,
        int MaxFrequencyMhz,
        bool IsBoostEnabled,
        Guid WindowsPowerSchemeId,
        int? NegativeCurveOptimizer = -15,
        int? AcMaxFrequencyMhz = null,
        int? DcMaxFrequencyMhz = null,
        AdvancedCpuTuningDraft? AdvancedCpuTuning = null,
        bool UseOfficialCpuPolicy = false,
        int? AcMinActiveCoresPercent = null,
        int? DcMinActiveCoresPercent = null)
    {
        public PerformanceDraft ToDraft() => new()
        {
            TemperatureLimitC = TemperatureLimitC,
            SplWatts = SplWatts,
            SpptWatts = SpptWatts,
            AcMaxFrequencyMhz = AcMaxFrequencyMhz ?? MaxFrequencyMhz,
            DcMaxFrequencyMhz = DcMaxFrequencyMhz ?? MaxFrequencyMhz,
            IsBoostEnabled = IsBoostEnabled,
            WindowsPowerSchemeId = WindowsPowerSchemeId,
            NegativeCurveOptimizer = NegativeCurveOptimizer,
            AdvancedCpuTuning = AdvancedCpuTuning,
            UseOfficialCpuPolicy = UseOfficialCpuPolicy,
            AcMinActiveCoresPercent = AcMinActiveCoresPercent,
            DcMinActiveCoresPercent = DcMinActiveCoresPercent
        };

        public static StoredPreset FromDraft(PerformanceDraft draft) => new(
            draft.TemperatureLimitC,
            draft.SplWatts,
            draft.SpptWatts,
            draft.MaxFrequencyMhz,
            draft.IsBoostEnabled,
            draft.WindowsPowerSchemeId,
            draft.NegativeCurveOptimizer,
            draft.AcMaxFrequencyMhz,
            draft.DcMaxFrequencyMhz,
            draft.AdvancedCpuTuning,
            draft.UseOfficialCpuPolicy,
            draft.AcMinActiveCoresPercent,
            draft.DcMinActiveCoresPercent);
    }
}
