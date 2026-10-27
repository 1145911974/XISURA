using System.Diagnostics;
using Jiaolong.Service.Hosting;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CandidateJournalRootSafetyTests
{
    [TestMethod]
    public void Rejects_reparse_journal_file_without_replacing_target_record()
    {
        using var fixture = new SafetyFixture();
        string root = fixture.CreateDirectory("candidate");
        const string original = "original control journal bytes";
        string target = fixture.WriteFile(Path.Combine(fixture.Root, "original.json"), original);
        string journal = Path.Combine(root, "control-execution.json");

        try
        {
            fixture.CreateFileSymbolicLink(journal, target);
            Assert.ThrowsExactly<IOException>(() => CandidateJournalRoot.Parse(["--control-journal-root", root]));
        }
        catch (Exception error) when (IsSymbolicLinkUnavailable(error))
        {
            Assert.Inconclusive("File symbolic links need Developer Mode or SeCreateSymbolicLinkPrivilege; directory junction rejection is covered separately.");
            return;
        }

        Assert.IsTrue((File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0);
        Assert.AreEqual(original, File.ReadAllText(target));
    }

    [TestMethod]
    public void Rejects_root_and_ancestor_junctions_without_replacing_original_records()
    {
        using var fixture = new SafetyFixture();
        string targetRoot = fixture.CreateDirectory("target");
        string nestedRoot = fixture.CreateDirectory(Path.Combine("target", "child"));
        const string rootRecord = "original root journal bytes";
        const string nestedRecord = "original nested journal bytes";
        string rootJournal = fixture.WriteFile(Path.Combine(targetRoot, "control-execution.json"), rootRecord);
        string nestedJournal = fixture.WriteFile(Path.Combine(nestedRoot, "control-execution.json"), nestedRecord);
        string rootJunction = fixture.CreateDirectoryJunction("root-junction", targetRoot);
        string ancestorJunction = fixture.CreateDirectoryJunction("ancestor-junction", targetRoot);

        Assert.ThrowsExactly<IOException>(() => CandidateJournalRoot.Parse(["--control-journal-root", rootJunction]));
        Assert.ThrowsExactly<IOException>(() => CandidateJournalRoot.Parse(["--control-journal-root", Path.Combine(ancestorJunction, "child")]));

        Assert.IsTrue((File.GetAttributes(rootJunction) & FileAttributes.ReparsePoint) != 0);
        Assert.IsTrue((File.GetAttributes(ancestorJunction) & FileAttributes.ReparsePoint) != 0);
        Assert.AreEqual(rootRecord, File.ReadAllText(rootJournal));
        Assert.AreEqual(nestedRecord, File.ReadAllText(nestedJournal));
    }

    private static bool IsSymbolicLinkUnavailable(Exception error) =>
        error is UnauthorizedAccessException or PlatformNotSupportedException ||
        (error is IOException &&
         (error.HResult == unchecked((int)0x80070522) || error.HResult == unchecked((int)0x80070005)));

    private sealed class SafetyFixture : IDisposable
    {
        private readonly List<string> directories = [];
        private readonly List<string> files = [];
        private readonly List<string> junctions = [];

        public string Root { get; } = Path.Combine(
            @"C:\Users\Administrator\AppData\Local\Temp",
            "XisuraCandidateSafety-" + Guid.NewGuid().ToString("N"));

        public SafetyFixture() => Directory.CreateDirectory(Root);

        public string CreateDirectory(string relativePath)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(path);
            directories.Add(path);
            return path;
        }

        public string WriteFile(string path, string contents)
        {
            files.Add(path);
            File.WriteAllText(path, contents);
            return path;
        }

        public string CreateFileSymbolicLink(string path, string target)
        {
            files.Add(path);
            File.CreateSymbolicLink(path, target);
            return path;
        }

        public string CreateDirectoryJunction(string name, string target)
        {
            string path = Path.Combine(Root, name);
            junctions.Add(path);

            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:XISURA_TEST_LINK -Target $env:XISURA_TEST_TARGET -ErrorAction Stop | Out-Null");
            start.Environment["XISURA_TEST_LINK"] = path;
            start.Environment["XISURA_TEST_TARGET"] = target;

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell to create a test junction.");
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new IOException($"Could not create the test junction: {error.Trim()}");

            return path;
        }

        public void Dispose()
        {
            for (int i = junctions.Count - 1; i >= 0; i--)
            {
                string junction = junctions[i];
                if (Directory.Exists(junction) && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(junction);
            }

            for (int i = files.Count - 1; i >= 0; i--)
                if (File.Exists(files[i]))
                    File.Delete(files[i]);

            for (int i = directories.Count - 1; i >= 0; i--)
                if (Directory.Exists(directories[i]))
                    Directory.Delete(directories[i]);

            if (Directory.Exists(Root))
                Directory.Delete(Root);
        }
    }
}
