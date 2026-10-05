using System.Xml.Linq;

namespace Jiaolong.Installer.Tests;

[TestClass]
public sealed class UpgradeRepairRollbackTests
{
    [TestMethod]
    public void Installer_source_supports_major_upgrade_repair_and_rollback_boundary()
    {
        var document = XDocument.Load(MsiFixture.FindSource("Package.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var package = document.Descendants(wix + "Package").Single();
        var majorUpgrade = document.Descendants(wix + "MajorUpgrade").Single();

        Assert.AreEqual("perMachine", (string?)package.Attribute("Scope"));
        Assert.AreEqual("6D5E0FE5-7C27-4BB5-AE48-CA1A5E7E9B39", (string?)package.Attribute("UpgradeCode"));
        Assert.IsTrue(Version.Parse((string)package.Attribute("Version")!) > new Version(1, 0, 13, 0));
        Assert.AreEqual("afterInstallInitialize", (string?)majorUpgrade.Attribute("Schedule"));
        Assert.IsNotNull(majorUpgrade.Attribute("DowngradeErrorMessage"));

        var icon = package.Elements(wix + "Icon").Single();
        Assert.AreEqual("JiaolongWaveApp.exe", (string?)icon.Attribute("Id"));
        StringAssert.EndsWith((string)icon.Attribute("SourceFile")!, @"\Assets\Brand\JiaolongWaveApp.ico");
        var files = XDocument.Load(MsiFixture.FindSource("Files.wxs"));
        var executable = files.Descendants(wix + "File").Single(file => (string?)file.Attribute("Id") == "ControlCenterExecutable");
        Assert.AreEqual("yes", (string?)executable.Attribute("KeyPath"));
        Assert.IsTrue(files.Descendants(wix + "Exclude").Any(exclude => ((string?)exclude.Attribute("Files"))?.EndsWith(@"\Jiaolong.ControlCenter.exe", StringComparison.Ordinal) == true));
        var shortcuts = executable.Elements(wix + "Shortcut").ToArray();
        CollectionAssert.AreEquivalent(new[] { "DesktopFolder", "ProgramMenuFolder" }, shortcuts.Select(shortcut => (string)shortcut.Attribute("Directory")!).ToArray());
        foreach (var shortcut in shortcuts)
        {
            Assert.AreEqual("yes", (string?)shortcut.Attribute("Advertise"));
            Assert.AreEqual("JiaolongWaveApp.exe", (string?)shortcut.Attribute("Icon"));
            Assert.AreEqual("INSTALLFOLDER", (string?)shortcut.Attribute("WorkingDirectory"));
            Assert.IsNull(shortcut.Attribute("Arguments"));
            Assert.IsFalse(shortcut.Elements(wix + "ShortcutProperty").Any());
        }
    }

    [TestMethod]
    public void Installer_payload_publish_must_build_self_contained_apphosts()
    {
        var source = File.ReadAllText(MsiFixture.FindSource("Jiaolong.Installer.wixproj"));

        StringAssert.Contains(source, "--self-contained true");
        Assert.IsFalse(source.Contains("--no-build", StringComparison.Ordinal));
    }
}
