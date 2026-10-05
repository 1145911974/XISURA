namespace Jiaolong.Installer.Tests;

[TestClass]
public sealed class OfficialDependencyCoexistenceTests
{
    [TestMethod]
    public void Installer_contains_no_oem_gui_payload_firewall_rule_or_network_dependency()
    {
        var files = File.ReadAllText(MsiFixture.FindSource("Files.wxs"));
        var package = File.ReadAllText(MsiFixture.FindSource("Package.wxs"));

        Assert.IsFalse(files.Contains("ControlCenterX", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(files.Contains("Mechrevo", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(package.Contains("WixFirewallException", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(package.Contains("DownloadUrl", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(package.Contains("RemotePayload", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Program_data_acl_is_protected_and_users_are_read_only()
    {
        var directories = File.ReadAllText(MsiFixture.FindSource("Directories.wxs"));

        StringAssert.Contains(
            directories,
            "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;S-1-5-80-404912088-106822883-2312322702-2665459205-905369233)(A;OICI;0x1200a9;;;BU)");
        Assert.IsFalse(directories.Contains("util:PermissionEx", StringComparison.Ordinal));
    }
}
