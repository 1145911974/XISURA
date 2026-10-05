using System.Text.Json;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public sealed class PerformancePresetStore
{
    private readonly IUserPreferencesPathProvider paths;

    public PerformancePresetStore(IUserPreferencesPathProvider? paths = null) =>
        this.paths = paths ?? new DefaultUserPreferencesPathProvider();

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<PerformanceDraft>>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PresetsPath))
            return new Dictionary<string, IReadOnlyList<PerformanceDraft>>(StringComparer.Ordinal);

        await using var stream = new FileStream(PresetsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<PerformanceDraft>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var stored = property.Value.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<StoredPreset>>(property.Value.GetRawText()) ?? []
                : property.Value.Deserialize<StoredPreset>() is { } legacy ? [legacy] : [];
            result[property.Name] = stored.Select(item => item.ToDraft()).ToArray();
        }

        return result;
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, IReadOnlyList<PerformanceDraft>> presets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presets);
        cancellationToken.ThrowIfCancellationRequested();
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
            }

            File.Move(tempPath, PresetsPath, overwrite: true);
            paths.RecordWrite(PresetsPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
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
        AdvancedCpuTuningDraft? AdvancedCpuTuning = null)
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
            AdvancedCpuTuning = AdvancedCpuTuning
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
            draft.AdvancedCpuTuning);
    }
}
