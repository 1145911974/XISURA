using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace Jiaolong_ControlCenter.Services;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void Enable(string? executablePath = null);
    void Disable();
}

public sealed class StartupRegistrationService : IStartupRegistration
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "JiaolongControlCenter";
    private static string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("当前用户身份不可用。");
        }
    }
    private static string TaskName => $"XISURA Startup {UserSid}";

    public bool IsEnabled
    {
        get
        {
            return WithTaskFolder(folder => ReadTaskEnabled(folder)) || LegacyEnabled;
        }
    }

    public void Enable(string? executablePath = null)
    {
        var path = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Executable path is unavailable.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("开机启动程序不存在。", path);
        // Run entries cannot elevate the administrator UI during sign-in. A user-owned
        // interactive logon task preserves elevation without credentials or a UAC prompt.
        WithTaskFolder(folder =>
        {
            dynamic task = folder.RegisterTask(TaskName, CreateTaskXml(path, UserSid), 6, UserSid, null, 3, null);
            try
            {
                if (!(bool)task.Enabled) throw new InvalidOperationException("开机启动任务未启用。");
            }
            finally { Marshal.ReleaseComObject(task); }
            return true;
        });
        DeleteLegacyEntry();
    }

    public void Disable()
    {
        WithTaskFolder(folder =>
        {
            try { folder.DeleteTask(TaskName, 0); }
            catch (Exception exception) when (exception.HResult == unchecked((int)0x80070002)) { }
            return true;
        });
        DeleteLegacyEntry();
    }

    public void RepairEnabledRegistration()
    {
        try
        {
            if (IsEnabled) Enable();
        }
        catch (Exception exception)
        {
            // Startup repair must not prevent the window from opening or hide the cause.
            AppRuntimeLog.Write($"[{DateTime.Now:O}] 开机启动登记修复失败：{exception}\n");
        }
    }

    private static bool LegacyEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string value || string.IsNullOrWhiteSpace(value)) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return approved?.GetValue(ValueName) is not byte[] state || state.Length == 0 || state[0] is not (3 or 7);
        }
    }

    private static void DeleteLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool ReadTaskEnabled(dynamic folder)
    {
        dynamic task;
        try { task = folder.GetTask(TaskName); }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070002)) { return false; }
        try { return (bool)task.Enabled; }
        finally { Marshal.ReleaseComObject(task); }
    }

    private static T WithTaskFolder<T>(Func<dynamic, T> operation)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows 任务计划服务不可用。"))!;
        dynamic? folder = null;
        try
        {
            scheduler.Connect();
            folder = scheduler.GetFolder(@"\");
            return operation(folder);
        }
        finally
        {
            if (folder is not null) Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(scheduler);
        }
    }

    private static string CreateTaskXml(string path, string userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, object value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", "XISURA 当前用户登录后立即启动")),
            Element("Triggers", new XElement(ns + "LogonTrigger", Element("Enabled", "true"), Element("UserId", userSid))),
            Element("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                Element("UserId", userSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
            Element("Settings", new object[] {
                Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"),
                Element("StopIfGoingOnBatteries", "false"), Element("StartWhenAvailable", "true"),
                Element("Enabled", "true"), Element("ExecutionTimeLimit", "PT0S") }),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"),
                new XElement(ns + "Exec", Element("Command", path), Element("WorkingDirectory", Path.GetDirectoryName(path)!)))))
            .ToString(SaveOptions.DisableFormatting);
    }
}
