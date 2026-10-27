namespace Jiaolong.Installer.Tests;

[TestClass]
public sealed class InstallLifecycleTests
{
    [TestMethod]
    public void Candidate_has_no_firewall_table_and_preserves_machine_scope()
    {
        using var msi = MsiFixture.OpenCandidate();

        Assert.IsTrue(msi.IsPerMachine);
        Assert.IsFalse(msi.TableExists("WixFirewallException"));
    }

    [TestMethod]
    public void Repair_does_not_run_uninstall_cleanup_actions()
    {
        using var msi = MsiFixture.OpenCandidate();

        StringAssert.Contains(msi.SequenceCondition("JiaolongRemoveStartupTasks"), "NOT REINSTALL");
        StringAssert.Contains(msi.SequenceCondition("JiaolongRemoveProgramData"), "NOT REINSTALL");
    }
}
