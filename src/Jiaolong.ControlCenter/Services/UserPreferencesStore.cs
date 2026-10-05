using System.Collections.Concurrent;
using System.Text.Json;
using Jiaolong_ControlCenter.Prototype.QuickMenu;

namespace Jiaolong_ControlCenter.Services;

public sealed record UserPreferences(bool MinimizeToTrayOnClose, string Theme, bool ReduceMotion)
{
    public string LogoStyle { get; init; } = "explore";
    public bool RememberWindowSize { get; init; }
    public int WindowWidthDip { get; init; }
    public int WindowHeightDip { get; init; }
    public string[] QuickMenuEnabled { get; init; } = [];
    public string[] QuickMenuOrder { get; init; } = [];
    public string[]? TrayQuickMenuEnabled { get; init; }
    public string[]? TrayQuickMenuOrder { get; init; }
    public string TrayTheme { get; init; } = "dark";
    public bool LightingRestoreAtStartup { get; init; }
    public bool LightingRestoreAfterWake { get; init; }
    public bool PerformanceFollowPreset { get; init; } = true;
    public bool GpuFollowPreset { get; init; } = true;
    public bool FanFollowPreset { get; init; } = true;
    public bool LightingFollowPreset { get; init; }
    public Jiaolong.Contracts.Models.KeyboardLightingPlan? IndependentLighting { get; init; }
    public Dictionary<string, int> LightingPresetSlots { get; init; } = [];
    public Dictionary<string, int> PerformancePresetSlots { get; init; } = [];
    public bool AdaptiveModeEnabled { get; init; }
    public Dictionary<string, string> PresetNames { get; init; } = [];
    public AdaptiveStrategyId ActiveAdaptiveStrategy { get; init; } = AdaptiveStrategyId.BalancedAdaptive;
    public Dictionary<string, AdaptiveTargetMap> AdaptiveTargetMaps { get; init; } = [];
    public Dictionary<string, AdaptiveTriggerPolicy> AdaptiveTriggerPolicies { get; init; } = [];
    public Dictionary<string, AdaptiveStrategyAppearance> AdaptiveStrategyAppearances { get; init; } = [];

    public QuickMenuLayout ResolveTrayQuickMenuLayout() =>
        TrayQuickMenuEnabled is null && TrayQuickMenuOrder is null
            ? QuickMenuLayout.FromPersisted(QuickMenuEnabled ?? [], QuickMenuOrder ?? [], maxItems: 6)
            : QuickMenuLayout.FromPersisted(TrayQuickMenuEnabled ?? [], TrayQuickMenuOrder ?? [],
                maxItems: 6, useDefaultWhenEmpty: false);
}

public interface IUserPreferencesPathProvider
{
    string LocalAppDataRoot { get; }
    string ProgramDataRoot { get; }
    void RecordWrite(string path);
}

public sealed class DefaultUserPreferencesPathProvider : IUserPreferencesPathProvider
{
    public string LocalAppDataRoot => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string ProgramDataRoot => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public void RecordWrite(string path) { }
}

public sealed class UserPreferencesStore
{
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUserPreferencesPathProvider paths;
    private readonly string preferencesPath;
    private readonly object saveGate;

    public UserPreferencesStore(IUserPreferencesPathProvider? paths = null)
    {
        this.paths = paths ?? new DefaultUserPreferencesPathProvider();
        preferencesPath = Path.GetFullPath(Path.Combine(this.paths.LocalAppDataRoot, "Jiaolong Control Center", "preferences.json"));
        saveGate = PathLocks.GetOrAdd(preferencesPath, static _ => new object());
    }

    public bool MinimizeToTrayOnClose
    {
        get => Load().MinimizeToTrayOnClose;
        set => Update(current => current with { MinimizeToTrayOnClose = value });
    }

    public UserPreferences Load()
    {
        lock (saveGate) return LoadCore();
    }

    private UserPreferences LoadCore(bool allowFallback = true)
    {
        var path = preferencesPath;
        if (!File.Exists(path)) return Normalize(new UserPreferences(false, "system", false));

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Normalize(JsonSerializer.Deserialize<UserPreferences>(stream)
                ?? new UserPreferences(false, "system", false));
        }
        catch when (allowFallback)
        {
            return Normalize(new UserPreferences(false, "system", false));
        }
    }

    public void Save(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (saveGate) SaveCore(Normalize(preferences));
    }

    public UserPreferences Update(Func<UserPreferences, UserPreferences> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (saveGate)
        {
            var updated = mutation(LoadCore(allowFallback: false));
            ArgumentNullException.ThrowIfNull(updated);
            updated = Normalize(updated);
            SaveCore(updated);
            return updated;
        }
    }

    private void SaveCore(UserPreferences preferences)
    {
        var path = preferencesPath;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Preferences directory is unavailable.");
        Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            paths.RecordWrite(path);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public Task<UserPreferences> LoadAsync(CancellationToken cancellationToken) =>
        Task.Run(Load, cancellationToken);

    public Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken) =>
        Task.Run(() => Save(preferences), cancellationToken);

    private static UserPreferences Normalize(UserPreferences value)
    {
        var tray = value.ResolveTrayQuickMenuLayout();
        return value with
        {
            LogoStyle = value.LogoStyle == "classic" ? "classic" : "explore",
            TrayTheme = value.TrayTheme == "light" ? "light" : "dark",
            TrayQuickMenuEnabled = tray.ToPersistedEnabled(),
            TrayQuickMenuOrder = tray.ToPersistedOrder()
        };
    }
}
