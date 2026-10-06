using System.Xml.Linq;
using Jiaolong.Service.Installation;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class StartupTaskCleanupTests
{
    [TestMethod]
    public void Full_uninstall_removes_only_startup_tasks_for_this_installation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "installer", "Jiaolong.Installer", "Package.wxs")))
            root = root.Parent;
        Assert.IsNotNull(root);
        var package = XDocument.Load(Path.Combine(root.FullName, "installer", "Jiaolong.Installer", "Package.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var action = package.Descendants(wix + "CustomAction")
            .SingleOrDefault(element => (string?)element.Attribute("ExeCommand") == "--remove-startup-tasks");
        Assert.IsNotNull(action, "Full uninstall must remove product startup tasks.");
        Assert.AreEqual("ServiceExecutable", (string?)action.Attribute("FileRef"));
        Assert.AreEqual("deferred", (string?)action.Attribute("Execute"));
        Assert.AreEqual("no", (string?)action.Attribute("Impersonate"));
        Assert.AreEqual("check", (string?)action.Attribute("Return"));
        var scheduled = package.Descendants(wix + "Custom")
            .Single(element => (string?)element.Attribute("Action") == (string?)action.Attribute("Id"));
        Assert.AreEqual("RemoveFiles", (string?)scheduled.Attribute("Before"));
        Assert.AreEqual("REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE", (string?)scheduled.Attribute("Condition"));

        const string installed = @"C:\Program Files\Jiaolong Control Center\Jiaolong.ControlCenter.exe";
        const string name = "XISURA Startup S-1-5-21-42";
        XNamespace taskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string TaskXml(params string[] commands) => new XElement(taskNs + "Task",
            new XElement(taskNs + "Actions", commands.Select(command =>
                new XElement(taskNs + "Exec", new XElement(taskNs + "Command", command))))).ToString();
        bool Matches(string taskName, string xml) => StartupTaskCleanup.IsProductStartupTask(taskName, xml, installed);

        Assert.IsTrue(Matches(name, TaskXml(installed.ToUpperInvariant())));
        Assert.IsTrue(Matches(name, TaskXml(@"C:\Program Files\Jiaolong Control Center\sub\..\Jiaolong.ControlCenter.exe")));
        Assert.IsFalse(Matches("Other Startup S-1-5-21-42", TaskXml(installed)));
        Assert.IsFalse(Matches(name, TaskXml(@"C:\Other\Jiaolong.ControlCenter.exe")));
        Assert.IsFalse(Matches(name, TaskXml(@"Jiaolong.ControlCenter.exe")));
        Assert.IsFalse(Matches(name, TaskXml(installed, @"C:\Other\program.exe")));
        Assert.IsFalse(Matches(name, TaskXml()));
        Assert.IsFalse(Matches(name, "invalid XML"));
    }
}
