using Jiaolong.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Jiaolong.Diagnostics.Tests;

[TestClass]
public sealed class DiagnosticBundleBuilderTests
{
    [TestMethod]
    public async Task Bundle_contains_only_allowlisted_entries()
    {
        var fixture = new DiagnosticBundleFixture();
        var names = await fixture.BuildAndListEntriesAsync();

        CollectionAssert.AreEquivalent(DiagnosticBundleFixture.AllowedEntryNames, names.ToArray());
    }

    [TestMethod]
    public async Task Bundle_checksums_can_be_recomputed_for_all_payload_entries()
    {
        var fixture = new DiagnosticBundleFixture();
        var export = await fixture.BuildAsync();

        using var archive = ZipFile.OpenRead(export.FilePath);
        var checksums = archive.GetEntry("checksums.sha256")!;
        using var reader = new StreamReader(checksums.Open(), Encoding.UTF8);
        var lines = (await reader.ReadToEndAsync()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            var entry = archive.GetEntry(parts[1])!;
            using var memory = new MemoryStream();
            await entry.Open().CopyToAsync(memory);
            Assert.AreEqual(parts[0], Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant());
        }
    }

    [TestMethod]
    public async Task Delete_removes_only_the_export_named_by_its_guid()
    {
        var root = Path.Combine(Path.GetTempPath(), "JiaolongDiagnosticsDeleteTests", Guid.NewGuid().ToString("N"));
        var builder = new DiagnosticBundleBuilder(root);
        try
        {
            var export = await builder.BuildAsync(
                new DiagnosticExportRequest("S-1-5-21-1-2-3-4", new("{}", "{}", "{}", "{}", "[]", "")),
                CancellationToken.None);
            var exportId = Guid.Parse(Path.GetFileNameWithoutExtension(export.FilePath));
            var unrelatedPath = Path.Combine(root, $"{Guid.NewGuid():D}.zip");
            await File.WriteAllTextAsync(unrelatedPath, "keep");

            await builder.DeleteAsync(exportId, CancellationToken.None);

            Assert.IsFalse(File.Exists(export.FilePath));
            Assert.IsTrue(File.Exists(unrelatedPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class DiagnosticBundleFixture
{
    public static readonly string[] AllowedEntryNames =
    [
        "versions.json",
        "fingerprint.json",
        "capabilities.json",
        "service-health.json",
        "telemetry-60s.json",
        "logs.ndjson",
        "checksums.sha256"
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), "JiaolongDiagnosticsTests", Guid.NewGuid().ToString("N"));

    public async Task<IReadOnlyList<string>> BuildAndListEntriesAsync()
    {
        var export = await BuildAsync();
        using var archive = ZipFile.OpenRead(export.FilePath);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    public async Task<DiagnosticExport> BuildAsync()
    {
        var content = new DiagnosticBundleContent(
            "{\"appVersion\":\"test\"}",
            "{\"board\":\"unknown\"}",
            "{\"monitoring\":\"available\"}",
            "{\"healthy\":true}",
            "[]",
            "");
        var builder = new DiagnosticBundleBuilder(root);
        return await builder.BuildAsync(
            new DiagnosticExportRequest("S-1-5-21-1-2-3-4", content),
            CancellationToken.None);
    }
}
