namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class UserPreferencesStoreTests
{
    [TestMethod]
    public async Task User_preferences_never_write_program_data()
    {
        var paths = new RecordingPathProvider();
        var store = new Jiaolong_ControlCenter.Services.UserPreferencesStore(paths);
        var preferencesPath = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "preferences.json");
        var defaultJson = System.Text.Json.JsonSerializer.SerializeToElement(store.Load());
        Assert.IsTrue(defaultJson.TryGetProperty("LogoStyle", out var defaultStyle), "Missing LogoStyle default.");
        Assert.AreEqual("explore", defaultStyle.GetString());
        Assert.IsTrue(defaultJson.TryGetProperty("PerformancePresetSlots", out var performanceSlots), "Missing confirmed-slot memory.");
        Assert.AreEqual(0, performanceSlots.EnumerateObject().Count());
        Assert.AreEqual("dark", store.Load().TrayTheme);
        Assert.AreEqual(6, store.Load().ResolveTrayQuickMenuLayout().EnabledOrder.Count);
        Assert.IsFalse(File.Exists(preferencesPath), "Initialization must not save preferences.");
        Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
        foreach (var legacyStyle in new[] { "null", "\"\"", "\"  \"", "\"unknown\"", "\"wave\"", "\"classic\"" })
        {
            var json = "{\"MinimizeToTrayOnClose\":true,\"Theme\":\"dark\",\"ReduceMotion\":true,\"LogoStyle\":" + legacyStyle + "}";
            File.WriteAllText(preferencesPath, json);
            var loaded = System.Text.Json.JsonSerializer.SerializeToElement(store.Load());
            Assert.AreEqual(legacyStyle == "\"classic\"" ? "classic" : "explore", loaded.GetProperty("LogoStyle").GetString());
            Assert.AreEqual(json, File.ReadAllText(preferencesPath), "Migration must not write during loading.");
        }
        var oldMain = "{\"Theme\":\"light\",\"QuickMenuEnabled\":[\"wifi\",\"bluetooth\",\"touchpad\",\"fnLock\",\"lidLogo\",\"numLock\",\"capsLock\",\"winKey\",\"displayOff\"],\"QuickMenuOrder\":[\"displayOff\",\"wifi\",\"bluetooth\",\"touchpad\",\"fnLock\",\"lidLogo\",\"numLock\",\"capsLock\",\"winKey\"]}";
        File.WriteAllText(preferencesPath, oldMain);
        var migrated = store.Load();
        CollectionAssert.AreEqual(new[] { "DisplayOff", "Wifi", "Bluetooth", "Touchpad", "FnLock", "LidLogo" }, migrated.TrayQuickMenuOrder!);
        Assert.AreEqual("dark", migrated.TrayTheme, "Tray theme must not inherit the main theme.");
        Assert.AreEqual(9, migrated.QuickMenuEnabled.Length);
        Assert.AreEqual(oldMain, File.ReadAllText(preferencesPath));
        store.Update(current => current with { Theme = "dark", TrayTheme = "light" });
        store.Update(current => current with { QuickMenuEnabled = ["wifi"], QuickMenuOrder = ["wifi"] });
        CollectionAssert.AreEqual(migrated.TrayQuickMenuOrder!, store.Load().TrayQuickMenuOrder!);
        Assert.AreEqual("light", store.Load().TrayTheme);
        store.Update(current => current with
        {
            TrayQuickMenuEnabled = ["wifi", "wifi", "unknown", "bluetooth", "touchpad", "fnLock", "lidLogo", "numLock", "capsLock"],
            TrayQuickMenuOrder = ["capsLock", "wifi", "bluetooth", "touchpad", "fnLock", "lidLogo", "numLock"], TrayTheme = "system"
        });
        Assert.AreEqual(6, store.Load().ResolveTrayQuickMenuLayout().EnabledOrder.Count);
        Assert.AreEqual("dark", store.Load().TrayTheme);
        CollectionAssert.AreEqual(new[] { "wifi" }, store.Load().QuickMenuEnabled);
        store.Update(current => current with { TrayQuickMenuEnabled = [], TrayQuickMenuOrder = [] });
        Assert.AreEqual(0, store.Load().ResolveTrayQuickMenuLayout().EnabledOrder.Count);
        foreach (var invalidTheme in new string?[] { null, "", "LIGHT", "unexpected" })
        {
            store.Update(current => current with { TrayTheme = invalidTheme! });
            Assert.AreEqual("dark", store.Load().TrayTheme);
        }

        await store.SaveAsync(
            new Jiaolong_ControlCenter.Services.UserPreferences(true, "dark", true)
            {
                LightingFollowPreset = true,
                IndependentLighting = new("Cycle", 67) { BrightnessLevel = 2, Red = 10, Green = 20, Blue = 30 },
                LightingPresetSlots = new() { ["Gaming"] = 3 }
            },
            CancellationToken.None);

        var reopened = new Jiaolong_ControlCenter.Services.UserPreferencesStore(paths).Load();
        Assert.IsTrue(reopened.LightingFollowPreset);
        Assert.AreEqual("Cycle", reopened.IndependentLighting!.Effect);
        Assert.AreEqual(3, reopened.LightingPresetSlots["Gaming"]);
        store.Save(reopened with { LightingFollowPreset = false });
        Assert.IsFalse(store.Load().LightingFollowPreset);
        Assert.AreEqual(reopened.IndependentLighting, store.Load().IndependentLighting);
        Assert.AreEqual("dark", store.Load().Theme);
        Assert.IsTrue(store.Load().ReduceMotion);
        var otherStore = new Jiaolong_ControlCenter.Services.UserPreferencesStore(paths);
        store.Update(current => current with
        {
            LogoStyle = "classic",
            QuickMenuEnabled = ["strong-cooling", "logo"]
        });
        otherStore.Update(current => current with { RememberWindowSize = true });
        var afterUpdates = store.Load();
        Assert.AreEqual("classic", System.Text.Json.JsonSerializer.SerializeToElement(afterUpdates).GetProperty("LogoStyle").GetString());
        CollectionAssert.AreEqual(new[] { "strong-cooling", "logo" }, afterUpdates.QuickMenuEnabled);
        Assert.IsTrue(afterUpdates.RememberWindowSize);
        Assert.AreEqual(reopened.IndependentLighting, afterUpdates.IndependentLighting);
        await Task.WhenAll(
            Task.Run(() => store.Update(current => current with { Theme = "light" })),
            Task.Run(() => otherStore.Update(current => current with { QuickMenuOrder = ["logo", "strong-cooling"] })));
        Assert.AreEqual("light", store.Load().Theme);
        CollectionAssert.AreEqual(new[] { "logo", "strong-cooling" }, store.Load().QuickMenuOrder);
        Assert.AreEqual("classic", store.Load().LogoStyle);
        store.Update(current => current with { PerformancePresetSlots = new() { ["Custom1"] = 3 } });
        var settings = new Jiaolong_ControlCenter.ViewModels.SettingsViewModel(
            new RecordingDiagnosticClient(), store, new RecordingStartupRegistration());
        await settings.SavePreferencesAsync(new(false, "dark", false), CancellationToken.None);
        Assert.AreEqual("classic", settings.Preferences.LogoStyle);
        Assert.AreEqual(3, settings.Preferences.PerformancePresetSlots["Custom1"]);
        Assert.IsTrue(settings.Preferences.RememberWindowSize);
        CollectionAssert.AreEqual(new[] { "logo", "strong-cooling" }, settings.Preferences.QuickMenuOrder);
        Assert.AreEqual(reopened.IndependentLighting, settings.Preferences.IndependentLighting);
        var beforeFailure = File.ReadAllBytes(preferencesPath);
        using (new FileStream(preferencesPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Assert.Throws<Exception>(() => store.Update(current => current with { LogoStyle = "explore" }));
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException);
        }
        CollectionAssert.AreEqual(beforeFailure, File.ReadAllBytes(preferencesPath));
        Assert.AreEqual("classic", store.Load().LogoStyle);
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(preferencesPath)!, "preferences.json.tmp-*").Length);
        File.WriteAllText(preferencesPath, "{");
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => store.Update(current => current with { LogoStyle = "classic" }));
        Assert.AreEqual("{", File.ReadAllText(preferencesPath), "A failed read must not replace preferences with defaults.");
        File.WriteAllBytes(preferencesPath, beforeFailure);
        Assert.IsTrue(paths.WrittenPaths.All(x => x.StartsWith(paths.LocalAppDataRoot, StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(paths.WrittenPaths.Any(x => x.StartsWith(paths.ProgramDataRoot, StringComparison.OrdinalIgnoreCase)));
    }
}

internal sealed class RecordingPathProvider : Jiaolong_ControlCenter.Services.IUserPreferencesPathProvider
{
    public string LocalAppDataRoot { get; } = Path.Combine(Path.GetTempPath(), "JiaolongTests", Guid.NewGuid().ToString("N"), "LocalAppData");
    public string ProgramDataRoot { get; } = Path.Combine(Path.GetTempPath(), "JiaolongTests", Guid.NewGuid().ToString("N"), "ProgramData");
    public List<string> WrittenPaths { get; } = [];

    public void RecordWrite(string path) => WrittenPaths.Add(path);
}
