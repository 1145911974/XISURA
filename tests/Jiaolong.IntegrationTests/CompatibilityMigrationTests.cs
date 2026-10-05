using Jiaolong.Hardware.Mechrevo.Compatibility;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class CompatibilityMigrationTests
{
    [TestMethod]
    public void Signed_capability_manifest_loads_without_low_level_fallback()
    {
        var loaded = ManifestLoader.LoadEmbedded();
        Assert.IsTrue(loaded.Manifest.Capabilities.Length > 0);
        CollectionAssert.Contains(loaded.Manifest.Capabilities.Select(item => item.Key).ToArray(), "strongCooling");
        CollectionAssert.Contains(loaded.Manifest.Migration.ForbiddenLowLevelKeys, "ecAddress");

        var evidence = File.ReadAllText(Path.Combine(FindRoot(), "docs", "research", "hardware-evidence.md"));
        StringAssert.Contains(evidence, "No migration");
        StringAssert.Contains(evidence, "ReadOnlySafeMode");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
