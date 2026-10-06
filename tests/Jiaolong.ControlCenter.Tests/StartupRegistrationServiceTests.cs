using System.Reflection;
using System.Xml.Linq;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class StartupRegistrationServiceTests
{
    [TestMethod]
    public void Login_task_runs_only_its_user_interactively_with_elevation_and_without_delay()
    {
        var xml = Definition();
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.AreEqual("S-1-5-21-123", xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")?.Value);
        Assert.IsFalse(xml.Descendants(ns + "Delay").Any());
        Assert.AreEqual("S-1-5-21-123", xml.Descendants(ns + "Principal").Single().Element(ns + "UserId")?.Value);
        Assert.AreEqual("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.AreEqual("HighestAvailable", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.AreEqual("IgnoreNew", xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.AreEqual("false", xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.AreEqual("false", xml.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.AreEqual("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.AreEqual("4", xml.Descendants(ns + "Priority").Single().Value);
        Assert.AreEqual("PT1M", xml.Descendants(ns + "RestartOnFailure").Single().Element(ns + "Interval")?.Value);
        Assert.AreEqual("3", xml.Descendants(ns + "RestartOnFailure").Single().Element(ns + "Count")?.Value);
    }

    [TestMethod]
    public void Task_action_preserves_the_literal_executable_path_and_working_directory()
    {
        var xml = Definition();
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.AreEqual(@"C:\Program Files\XISURA & Co\Jiaolong.ControlCenter.exe", xml.Descendants(ns + "Command").Single().Value);
        Assert.AreEqual(@"C:\Program Files\XISURA & Co", xml.Descendants(ns + "WorkingDirectory").Single().Value);
        Assert.IsFalse(xml.Descendants(ns + "Arguments").Any());
    }

    private static XDocument Definition()
    {
        var factory = typeof(StartupRegistrationService).GetMethod("CreateTaskXml", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(factory, "Login registration must provide a task definition, rather than the non-elevating Run entry.");
        return XDocument.Parse((string)factory.Invoke(null, [@"C:\Program Files\XISURA & Co\Jiaolong.ControlCenter.exe", "S-1-5-21-123"])!);
    }
}
