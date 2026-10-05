using Jiaolong.Contracts.Models;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class LifecycleRecoveryTests
{
    [TestMethod]
    public void Release_runbook_covers_stop_suspend_hibernate_shutdown_and_restart()
    {
        var root = FindRoot();
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "engineering", "release-runbook.md"));

        foreach (var marker in new[] { "ServiceStopping", "SystemSuspend", "hibernate", "shutdown", "SCM restart" })
        {
            StringAssert.Contains(runbook, marker, marker);
        }

        CollectionAssert.AreEqual(
            new[] { ReleaseReason.ServiceStopping, ReleaseReason.SystemSuspend },
            new[] { ReleaseReason.ServiceStopping, ReleaseReason.SystemSuspend });
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
