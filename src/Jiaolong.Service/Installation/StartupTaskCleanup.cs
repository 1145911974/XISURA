using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace Jiaolong.Service.Installation;

internal static class StartupTaskCleanup
{
    private const string TaskPrefix = "XISURA Startup ";

    public static int Run()
    {
        try
        {
            var installedClientPath = Path.Combine(AppContext.BaseDirectory, "Jiaolong.ControlCenter.exe");
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable."))!;
            dynamic? folder = null;
            dynamic? tasks = null;
            try
            {
                scheduler.Connect();
                folder = scheduler.GetFolder(@"\");
                tasks = folder.GetTasks(1); // Include hidden product startup tasks.
                for (var index = (int)tasks.Count; index >= 1; index--)
                {
                    dynamic task = tasks[index];
                    try
                    {
                        string name = task.Name;
                        if (name.StartsWith(TaskPrefix, StringComparison.Ordinal)
                            && IsProductStartupTask(name, (string)task.Xml, installedClientPath))
                            folder.DeleteTask(name, 0);
                    }
                    finally { Marshal.ReleaseComObject(task); }
                }
            }
            finally
            {
                if (tasks is not null) Marshal.ReleaseComObject(tasks);
                if (folder is not null) Marshal.ReleaseComObject(folder);
                Marshal.ReleaseComObject(scheduler);
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Startup task removal failed: {exception.Message}");
            return 1;
        }
    }

    internal static bool IsProductStartupTask(string name, string taskXml, string installedClientPath)
    {
        if (!name.StartsWith(TaskPrefix, StringComparison.Ordinal)) return false;
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var actions = XDocument.Parse(taskXml).Root?.Element(ns + "Actions")?.Elements().ToArray();
            // Leave mixed-purpose tasks alone, even if one action launches this client.
            if (actions is not { Length: 1 } || actions[0].Name != ns + "Exec") return false;
            var command = (string?)actions[0].Element(ns + "Command");
            return !string.IsNullOrWhiteSpace(command)
                && Path.IsPathFullyQualified(command)
                && Path.IsPathFullyQualified(installedClientPath)
                && string.Equals(Path.GetFullPath(command), Path.GetFullPath(installedClientPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
