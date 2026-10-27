using System.Text.Json;
using Jiaolong.Service.Home;
using Jiaolong.Service.Hosting;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CandidateJournalRootTests
{
    [TestMethod]
    public void Normal_host_arguments_keep_the_installed_journal_location()
    {
        Assert.IsNull(CandidateJournalRoot.Parse([]));
        Assert.IsNull(CandidateJournalRoot.Parse(["--environment", "Development"]));
    }

    [TestMethod]
    public async Task Explicit_candidate_root_reads_the_existing_journal_without_replacing_authority()
    {
        string root = NewRoot();
        try
        {
            string original = JsonSerializer.Serialize(new ControlJournalSnapshot(1, null,
                Jiaolong.Contracts.Models.PerformanceMode.Balanced, null, []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            string journal = Path.Combine(root, "control-execution.json");
            await File.WriteAllTextAsync(journal, original);
            await File.WriteAllTextAsync(Path.Combine(root, "client-control-session.json"), "retained capture");
            await File.WriteAllTextAsync(Path.Combine(root, "prior-exit-plan.json"), "retained plan");
            string[] before = Directory.GetFiles(root).Order().ToArray();

            string? selected = CandidateJournalRoot.Parse(["--control-journal-root", root]);
            Assert.AreEqual(root, selected);
            var snapshot = await new ControlExecutionJournal(selected).ReadAsync(CancellationToken.None);
            Assert.AreEqual(Jiaolong.Contracts.Models.PerformanceMode.Balanced, snapshot.LastConfirmedMode);
            Assert.AreEqual(original, await File.ReadAllTextAsync(journal));
            Assert.AreEqual("retained capture", await File.ReadAllTextAsync(Path.Combine(root, "client-control-session.json")));
            Assert.AreEqual("retained plan", await File.ReadAllTextAsync(Path.Combine(root, "prior-exit-plan.json")));
            CollectionAssert.AreEqual(before, Directory.GetFiles(root).Order().ToArray());
        }
        finally { CleanRoot(root); }
    }

    [TestMethod]
    public void Candidate_root_requires_an_existing_original_journal_and_creates_nothing()
    {
        string root = NewRoot();
        try
        {
            Assert.ThrowsExactly<IOException>(() => CandidateJournalRoot.Parse(["--control-journal-root", root]));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
            string absent = Path.Combine(root, "absent");
            Assert.ThrowsExactly<IOException>(() => CandidateJournalRoot.Parse(["--control-journal-root", absent]));
            Assert.IsFalse(Directory.Exists(absent));
        }
        finally { CleanRoot(root); }
    }

    [TestMethod]
    public void Candidate_arguments_reject_ambiguous_relative_and_other_drive_paths_before_access()
    {
        string[][] invalid =
        [
            ["--control-journal-root"],
            ["--control-journal-root=C:\\"],
            ["--CONTROL-JOURNAL-ROOT", "C:\\"],
            ["--control-journal-root", "C:\\", "--environment", "Development"],
            ["--environment", "Development", "--control-journal-root", "C:\\"],
            ["--control-journal-root", ""],
            ["--control-journal-root", "."],
            ["--control-journal-root", @"C:relative"],
            ["--control-journal-root", @"D:\not-created"],
            ["--control-journal-root", @"\\server\share\not-created"]
        ];
        foreach (var args in invalid)
            Assert.ThrowsExactly<ArgumentException>(() => CandidateJournalRoot.Parse(args));
    }

    private static string NewRoot()
    {
        string root = Path.Combine(@"C:\Users\Administrator\AppData\Local\Temp", "XisuraCandidateRootTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanRoot(string root)
    {
        foreach (string file in Directory.GetFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
}
