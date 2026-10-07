using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Jiaolong.Diagnostics;

namespace Jiaolong.Diagnostics.Tests;

[TestClass]
public sealed class SupportDiagnosticBundleTests
{
    [TestMethod]
    public async Task Unsafe_local_entry_name_is_rejected_before_writing()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => SupportDiagnosticBundle.WriteAsync(output,
            new Dictionary<string, string> { ["../file.txt"] = "text" }, null, CancellationToken.None));
        Assert.AreEqual(0, output.Length);
    }

    [TestMethod]
    public async Task Offline_export_redacts_logs_and_checksums_every_entry()
    {
        using var output = new MemoryStream();
        await SupportDiagnosticBundle.WriteAsync(output, new Dictionary<string, string>
        {
            ["support.json"] = "{\"ipcConnected\":false}",
            ["client.log"] = "C:\\Users\\Alice\\Documents\\app.log\nS-1-5-21-111-222-333-1001 token=private-value\n" +
                "{\"password\":\"private spaced value\",\"fullPath\":\"C:\\\\Users\\\\Jane Doe\\\\Desktop\\\\app.log\"}"
        }, null, CancellationToken.None);
        output.Position = 0;
        using var zip = new ZipArchive(output);
        var text = await new StreamReader(zip.GetEntry("client.log")!.Open()).ReadToEndAsync();
        Assert.IsFalse(text.Contains("Alice") || text.Contains("private-value") || text.Contains("S-1-5-21") || text.Contains("Jane Doe") || text.Contains("private spaced value"));
        var lines = (await new StreamReader(zip.GetEntry("checksums.sha256")!.Open()).ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(zip.Entries.Count - 1, lines.Length);
        foreach (var line in lines)
        {
            var parts = line.Split("  ", 2);
            using var bytes = new MemoryStream();
            await zip.GetEntry(parts[1])!.Open().CopyToAsync(bytes);
            Assert.AreEqual(parts[0], Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        }
    }

    [TestMethod]
    public async Task Unreadable_service_bundle_keeps_local_export_and_records_failure()
    {
        using var output = new MemoryStream();
        await SupportDiagnosticBundle.WriteAsync(output, new Dictionary<string, string> { ["client.log"] = "offline connection error" },
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip"), CancellationToken.None);
        output.Position = 0;
        using var zip = new ZipArchive(output);
        Assert.IsNotNull(zip.GetEntry("client.log"));
        Assert.IsNotNull(zip.GetEntry("service-export-error.txt"));
    }

    [TestMethod]
    public async Task Service_entries_are_allowlisted_redacted_and_rehashed()
    {
        var file = Path.Combine(Path.GetTempPath(), "xisura-diag-test-" + Guid.NewGuid() + ".zip");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("versions.json").Open())) writer.Write(@"C:\Users\Bob\version");
                using (var writer = new StreamWriter(zip.CreateEntry("../personal.txt").Open())) writer.Write("private");
                using (var writer = new StreamWriter(zip.CreateEntry("checksums.sha256").Open())) writer.Write("old checksum");
            }
            using var output = new MemoryStream();
            await SupportDiagnosticBundle.WriteAsync(output, new Dictionary<string, string>(), file, CancellationToken.None);
            output.Position = 0;
            using var result = new ZipArchive(output);
            Assert.IsNotNull(result.GetEntry("service/versions.json"));
            Assert.IsNull(result.GetEntry("service/../personal.txt"));
            Assert.IsFalse((await new StreamReader(result.GetEntry("service/versions.json")!.Open()).ReadToEndAsync()).Contains("Bob"));
            Assert.IsFalse((await new StreamReader(result.GetEntry("checksums.sha256")!.Open()).ReadToEndAsync()).Contains("old checksum"));
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public void Log_reads_are_bounded_shared_and_missing_files_are_reported()
    {
        var file = Path.Combine(Path.GetTempPath(), "xisura-log-test-" + Guid.NewGuid());
        try
        {
            File.WriteAllText(file, new string('a', 2048) + "\nlast line", Encoding.UTF8);
            using var active = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tail = SupportDiagnosticBundle.ReadLogTail(file, 64);
            StringAssert.Contains(tail, "last line");
            Assert.IsTrue(tail.Length < 128);
            StringAssert.Contains(SupportDiagnosticBundle.ReadLogTail(file + ".missing"), "unavailable");
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task Cancelled_export_does_not_write_an_archive()
    {
        using var output = new MemoryStream();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => SupportDiagnosticBundle.WriteAsync(output,
            new Dictionary<string, string> { ["client.log"] = "log" }, null, cancelled.Token));
        Assert.AreEqual(0, output.Length);
    }
}
