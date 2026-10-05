using System.Buffers.Binary;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformanceVisualEvidenceTests
{
    private static readonly string EvidenceDirectory = Path.Combine(
        FindRepositoryRoot(), "docs", "design", "visual-reconstruction", "performance");

    [TestMethod]
    public void Evidence_manifest_uses_the_approved_canvas_and_regions()
    {
        using var manifest = LoadJson("layout.manifest.json");
        var root = manifest.RootElement;
        Assert.AreEqual(1307, root.GetProperty("canvas").GetProperty("width").GetInt32());
        Assert.AreEqual(992, root.GetProperty("canvas").GetProperty("height").GetInt32());

        var regions = root.GetProperty("regions").EnumerateArray().ToArray();
        var ids = regions.Select(region => region.GetProperty("id").GetString()!).ToArray();
        string[] required =
        [
            "header", "preset-selector", "unsaved-state", "save-preset", "use-preset",
            "basic-tuning", "cpu-boundary", "advanced-summary", "preset-panel",
            "advanced-smu", "advanced-pbo-core", "advanced-status"
        ];

        CollectionAssert.IsSubsetOf(required, ids);
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        foreach (var region in regions)
        {
            var bounds = region.GetProperty("bounds");
            var x = bounds.GetProperty("x").GetInt32();
            var y = bounds.GetProperty("y").GetInt32();
            var width = bounds.GetProperty("width").GetInt32();
            var height = bounds.GetProperty("height").GetInt32();
            Assert.IsTrue(x >= 0 && y >= 0 && width > 0 && height > 0);
            Assert.IsTrue(x + width <= 1307 && y + height <= 992, region.GetProperty("id").GetString());
            Assert.IsTrue(region.GetProperty("renderKind").GetString() is "native" or "asset");
        }
    }

    [TestMethod]
    public void Evidence_states_reference_the_three_approved_mockups()
    {
        using var states = LoadJson("states.json");
        var root = states.RootElement;
        Assert.AreEqual("01-performance-base-content.png", root.GetProperty("default").GetProperty("reference").GetString());
        Assert.AreEqual("02-performance-preset-expanded-content.png", root.GetProperty("preset-expanded").GetProperty("reference").GetString());
        Assert.AreEqual("03-performance-advanced-content.png", root.GetProperty("advanced-expanded").GetProperty("reference").GetString());
        Assert.AreEqual("PerformanceV2", root.GetProperty("default").GetProperty("route").GetString());
        Assert.AreEqual("PerformanceV2Preset", root.GetProperty("preset-expanded").GetProperty("route").GetString());
        Assert.AreEqual("PerformanceV2Advanced", root.GetProperty("advanced-expanded").GetProperty("route").GetString());
    }

    [TestMethod]
    public void Evidence_tokens_include_the_required_visual_contract()
    {
        using var tokens = LoadJson("design-tokens.json");
        var root = tokens.RootElement;
        Assert.AreEqual(14, root.GetProperty("controls").GetProperty("trackHeight").GetInt32());
        Assert.AreEqual(28, root.GetProperty("controls").GetProperty("thumbSize").GetInt32());
        Assert.AreEqual(16, root.GetProperty("spacing").GetProperty("sectionGap").GetInt32());
        Assert.AreEqual(16, root.GetProperty("geometry").GetProperty("cardCornerRadius").GetInt32());
    }

    [TestMethod]
    public void Performance_assets_are_explicit_and_auditable()
    {
        var repositoryRoot = FindRepositoryRoot();
        Assert.IsTrue(File.Exists(Path.Combine(repositoryRoot, "tools", "Visual-Reconstruction", "Audit-TransparentAsset.ps1")));
        Assert.IsTrue(File.Exists(Path.Combine(repositoryRoot, "src", "Jiaolong.ControlCenter", "Assets", "PerformanceV2", "README.md")));

        using var manifest = LoadJson("layout.manifest.json");
        var cpuDecoration = manifest.RootElement.GetProperty("regions").EnumerateArray()
            .Single(region => region.GetProperty("id").GetString() == "cpu-boundary-decoration");
        Assert.AreEqual("native", cpuDecoration.GetProperty("renderKind").GetString(),
            "CPU 装饰包含动态象限，必须由 WinUI/Win2D 绘制，不能从整页截图烘焙。\n");
    }

    [TestMethod]
    public void Visual_gate_pins_dependencies_and_uses_canonical_masks()
    {
        var repositoryRoot = FindRepositoryRoot();
        string toolRoot = Path.Combine(repositoryRoot, "tools", "Visual-Reconstruction");
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(toolRoot, "package.json")));
        var dependencies = package.RootElement.GetProperty("dependencies");
        Assert.AreEqual("7.2.0", dependencies.GetProperty("pixelmatch").GetString());
        Assert.AreEqual("7.0.0", dependencies.GetProperty("pngjs").GetString());
        Assert.IsTrue(File.Exists(Path.Combine(toolRoot, "compare.mjs")));

        foreach (string state in new[] { "default", "preset-expanded", "advanced-expanded" })
        {
            string mask = Path.Combine(EvidenceDirectory, "qa", "reference-masks", $"{state}.png");
            Assert.IsTrue(File.Exists(mask), mask);
            byte[] header = File.ReadAllBytes(mask)[..24];
            Assert.AreEqual(1307, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
            Assert.AreEqual(992, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
        }
    }

    private static JsonDocument LoadJson(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(EvidenceDirectory, fileName)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。");
    }
}
