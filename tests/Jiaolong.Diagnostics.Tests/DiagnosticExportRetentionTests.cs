using System.IO.Compression;
using Jiaolong.Diagnostics;

namespace Jiaolong.Diagnostics.Tests;

[TestClass]
public sealed class DiagnosticExportRetentionTests
{
    [TestMethod]
    public async Task Export_remains_readable_by_another_reader_after_builder_and_request_scope_ends()
    {
        using var fixture = new DiagnosticRetentionFixture();
        string archivePath = await BuildAndReleaseAsync(fixture.Root);
        fixture.TrackFile(archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("versions.json");
        Assert.IsNotNull(entry);
        using var reader = new StreamReader(entry.Open());
        Assert.AreEqual("{\"version\":\"retained\"}", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task Cancelling_an_active_write_removes_partial_archive_and_preserves_sibling_file()
    {
        using var fixture = new DiagnosticRetentionFixture();
        string sibling = fixture.WriteFile("other-user-file.txt", "keep this file");
        using var cancellation = new CancellationTokenSource();
        var archiveCreated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(fixture.Root, "*.zip")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size
        };
        watcher.Created += (_, args) =>
        {
            fixture.TrackFile(args.FullPath);
            if (archiveCreated.TrySetResult(args.FullPath))
                cancellation.Cancel();
        };
        watcher.EnableRaisingEvents = true;

        var builder = new DiagnosticBundleBuilder(fixture.Root);
        Task<DiagnosticExport> buildTask = Task.Run(() =>
            builder.BuildAsync(CreateRequest(CreateLargeLog()), cancellation.Token));

        try
        {
            string archivePath = await archiveCreated.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => buildTask.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.IsFalse(File.Exists(archivePath));
            Assert.AreEqual("keep this file", File.ReadAllText(sibling));
            CollectionAssert.AreEqual(new[] { sibling }, Directory.GetFiles(fixture.Root));
        }
        finally
        {
            cancellation.Cancel();
            try { await buildTask.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> BuildAndReleaseAsync(string root)
    {
        var builder = new DiagnosticBundleBuilder(root);
        var request = CreateRequest();
        DiagnosticExport export = await builder.BuildAsync(request, CancellationToken.None);
        return export.FilePath;
    }

    private static DiagnosticExportRequest CreateRequest(string logs = "") =>
        new("S-1-5-21-1-2-3-4",
            new("{\"version\":\"retained\"}", "{}", "{}", "{}", "[]", logs));

    private static string CreateLargeLog()
    {
        const int length = 8 * 1024 * 1024;
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        var characters = new char[length];
        for (int i = 0; i < characters.Length; i++)
            characters[i] = (char)('a' + bytes[i] % 26);
        return new string(characters);
    }

    private sealed class DiagnosticRetentionFixture : IDisposable
    {
        private readonly object gate = new();
        private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);

        public string Root { get; } = Path.Combine(
            @"C:\Users\Administrator\AppData\Local\Temp",
            "XisuraDiagnosticRetention-" + Guid.NewGuid().ToString("N"));

        public DiagnosticRetentionFixture() => Directory.CreateDirectory(Root);

        public string WriteFile(string name, string contents)
        {
            string path = Path.Combine(Root, name);
            TrackFile(path);
            File.WriteAllText(path, contents);
            return path;
        }

        public void TrackFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Diagnostic test files must stay in their unique fixture directory.");

            lock (gate)
                files.Add(fullPath);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;

            // Builder outputs are GUID-named zip files inside this fixture-only directory.
            foreach (string bundle in Directory.GetFiles(Root, "*.zip"))
                TrackFile(bundle);

            string[] ownedFiles;
            lock (gate)
                ownedFiles = files.ToArray();

            foreach (string file in ownedFiles)
                if (File.Exists(file))
                    File.Delete(file);

            Directory.Delete(Root);
        }
    }
}
