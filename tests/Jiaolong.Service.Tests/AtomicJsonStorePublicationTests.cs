using System.Text.Json;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class AtomicJsonStorePublicationTests
{
    private const string TempRoot = @"C:\Users\Administrator\AppData\Local\Temp";
    private const string FileName = "state.json";

    [TestMethod]
    public async Task Failed_publication_keeps_old_current_visible_and_removes_temp_file()
    {
        string root = CreateRoot();
        try
        {
            var seed = new AtomicJsonStore(root, FileName);
            await seed.WriteAsync(new Snapshot(17), CancellationToken.None);
            string current = Path.Combine(root, FileName);
            string oldBytes = await File.ReadAllTextAsync(current);
            int hookCalls = 0;

            var interrupted = new AtomicJsonStore(root, FileName, () =>
            {
                hookCalls++;
                var visible = new AtomicJsonStore(root, FileName)
                    .ReadAsync<Snapshot>(CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsNotNull(visible);
                Assert.AreEqual(17, visible.Value);
                throw new IOException("publication failure injected");
            });

            await Assert.ThrowsExactlyAsync<IOException>(() =>
                interrupted.WriteAsync(new Snapshot(29), CancellationToken.None));

            Assert.AreEqual(1, hookCalls);
            Assert.AreEqual(oldBytes, await File.ReadAllTextAsync(current));
            var saved = await new AtomicJsonStore(root, FileName).ReadAsync<Snapshot>(CancellationToken.None);
            Assert.IsNotNull(saved);
            Assert.AreEqual(17, saved.Value);
            Assert.IsFalse(Directory.GetFiles(root, ".state.json.*.tmp").Any());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Successful_publications_rotate_three_backups_and_open_delete_shared_reader_keeps_old_snapshot()
    {
        string root = CreateRoot();
        try
        {
            var store = new AtomicJsonStore(root, FileName);
            string current = Path.Combine(root, FileName);
            await store.WriteAsync(new Snapshot(1), CancellationToken.None);

            await using var oldReader = new FileStream(current, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            await store.WriteAsync(new Snapshot(2), CancellationToken.None);
            await store.WriteAsync(new Snapshot(3), CancellationToken.None);
            await store.WriteAsync(new Snapshot(4), CancellationToken.None);

            var latest = await new AtomicJsonStore(root, FileName).ReadAsync<Snapshot>(CancellationToken.None);
            Assert.IsNotNull(latest);
            Assert.AreEqual(4, latest.Value);
            Assert.AreEqual(3, await ReadValueAsync(Path.Combine(root, FileName + ".1.bak")));
            Assert.AreEqual(2, await ReadValueAsync(Path.Combine(root, FileName + ".2.bak")));
            Assert.AreEqual(1, await ReadValueAsync(Path.Combine(root, FileName + ".3.bak")));

            oldReader.Position = 0;
            var retained = await JsonSerializer.DeserializeAsync<Snapshot>(oldReader,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.IsNotNull(retained);
            Assert.AreEqual(1, retained.Value);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Missing_current_with_backup_refuses_new_and_existing_stores_without_replacing_evidence()
    {
        string root = CreateRoot();
        try
        {
            var store = new AtomicJsonStore(root, FileName);
            await store.WriteAsync(new Snapshot(1), CancellationToken.None);
            await store.WriteAsync(new Snapshot(2), CancellationToken.None);
            string backup = Path.Combine(root, FileName + ".1.bak");
            string evidence = await File.ReadAllTextAsync(backup);
            File.Delete(store.FilePath);

            Assert.ThrowsExactly<IOException>(() => new AtomicJsonStore(root, FileName));
            await Assert.ThrowsExactlyAsync<IOException>(() => store.ReadAsync<Snapshot>(CancellationToken.None));
            await Assert.ThrowsExactlyAsync<IOException>(() => store.WriteAsync(new Snapshot(3), CancellationToken.None));

            Assert.IsFalse(File.Exists(store.FilePath));
            Assert.AreEqual(evidence, await File.ReadAllTextAsync(backup));
            CollectionAssert.AreEqual(new[] { backup }, Directory.GetFiles(root));
        }
        finally { Cleanup(root); }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(TempRoot, $"XisuraJournalPublication-{Guid.NewGuid():N}");
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Temporary test path already exists.");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<int> ReadValueAsync(string path)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return json.RootElement.GetProperty("value").GetInt32();
    }

    private static void Cleanup(string root)
    {
        foreach (string path in new[]
        {
            Path.Combine(root, FileName), Path.Combine(root, FileName + ".1.bak"),
            Path.Combine(root, FileName + ".2.bak"), Path.Combine(root, FileName + ".3.bak")
        })
            if (File.Exists(path)) File.Delete(path);
        foreach (string path in Directory.GetFiles(root, ".state.json.*.tmp")) File.Delete(path);
        Directory.Delete(root);
    }

    public sealed record Snapshot(int Value);
}
