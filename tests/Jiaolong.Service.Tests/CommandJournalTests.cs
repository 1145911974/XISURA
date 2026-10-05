using Jiaolong.Service.Commands;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CommandJournalTests
{
    [TestMethod]
    public async Task Journal_claim_is_atomic_for_same_operation()
    {
        var root = Path.Combine(Path.GetTempPath(), "JiaolongJournalTests", Guid.NewGuid().ToString("N"));
        var journal = new CommandJournal(root);
        var first = await journal.ClaimAsync(Guid.NewGuid(), "hash-a", CancellationToken.None);
        var second = await journal.ClaimAsync(first.OperationId, "hash-a", CancellationToken.None);

        Assert.IsFalse(first.IsReplay);
        Assert.IsTrue(second.IsInProgress);
    }
}
