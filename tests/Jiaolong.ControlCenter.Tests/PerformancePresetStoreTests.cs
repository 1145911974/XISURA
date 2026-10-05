using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformancePresetStoreTests
{
    [TestMethod]
    public async Task Save_then_load_round_trips_all_supported_fields()
    {
        var paths = new RecordingPathProvider();
        var store = new PerformancePresetStore(paths);
        var expected = new Dictionary<string, IReadOnlyList<PerformanceDraft>>
        {
            ["office"] = [new()
            {
                TemperatureLimitC = 76,
                SplWatts = 36,
                SpptWatts = 46,
                MaxFrequencyMhz = 3800,
                IsBoostEnabled = false,
                WindowsPowerSchemeId = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e")
            }]
        };

        await store.SaveAsync(expected, CancellationToken.None);
        var actual = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(expected["office"][0], actual["office"][0]);
    }

    [TestMethod]
    public async Task Save_then_load_round_trips_three_independent_slots()
    {
        var paths = new RecordingPathProvider();
        var store = new PerformancePresetStore(paths);
        var expected = new Dictionary<string, IReadOnlyList<PerformanceDraft>>
        {
            ["office"] =
            [
                new() { SplWatts = 30 },
                new() { SplWatts = 45 },
                new() { SplWatts = 60 }
            ]
        };

        await store.SaveAsync(expected, CancellationToken.None);
        var actual = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(3, actual["office"].Count);
        Assert.AreEqual(30, actual["office"][0].SplWatts);
        Assert.AreEqual(45, actual["office"][1].SplWatts);
        Assert.AreEqual(60, actual["office"][2].SplWatts);
    }

    [TestMethod]
    public async Task Missing_store_returns_empty_presets_without_creating_a_file()
    {
        var paths = new RecordingPathProvider();
        var actual = await new PerformancePresetStore(paths).LoadAsync(CancellationToken.None);

        Assert.AreEqual(0, actual.Count);
        Assert.IsFalse(File.Exists(Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "performance-presets.json")));
    }

    [TestMethod]
    public async Task Legacy_single_frequency_preset_loads_into_both_power_states()
    {
        var paths = new RecordingPathProvider();
        var directory = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "performance-presets.json"),
            "{\"office\":{\"TemperatureLimitC\":75,\"SplWatts\":35,\"SpptWatts\":45,\"MaxFrequencyMhz\":3800,\"IsBoostEnabled\":false,\"WindowsPowerSchemeId\":\"381b4222-f694-41f0-9685-ff5bb260df2e\"}}");

        var actual = await new PerformancePresetStore(paths).LoadAsync(CancellationToken.None);

        Assert.AreEqual(1, actual["office"].Count);
        Assert.AreEqual(3_800, actual["office"][0].AcMaxFrequencyMhz);
        Assert.AreEqual(3_800, actual["office"][0].DcMaxFrequencyMhz);
    }
}
