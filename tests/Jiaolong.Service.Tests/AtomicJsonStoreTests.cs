using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class AtomicJsonStoreTests
{
    [TestMethod]
    public async Task Store_replaces_json_atomically_and_keeps_bounded_backups()
    {
        var root = Path.Combine(Path.GetTempPath(), "JiaolongStoreTests", Guid.NewGuid().ToString("N"));
        var store = new AtomicJsonStore(root, "state.json");
        await store.WriteAsync(new { Value = 1 }, CancellationToken.None);
        await store.WriteAsync(new { Value = 2 }, CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(root, "state.json"));
        Assert.IsTrue(text.Contains("2", StringComparison.Ordinal));
        Assert.IsTrue(Directory.GetFiles(root, "state.json.*.bak").Length <= 3);
    }
}
