using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong.Diagnostics;

namespace Jiaolong_ControlCenter.Services;

internal static class SupportDiagnosticCollector
{
    internal static async Task<Dictionary<string, string>> CollectAsync(HomeStateSnapshot? state, string sessionStatus, bool ipcConnected, CancellationToken token)
    {
        var install = AppContext.BaseDirectory;
        var files = new[] { "Jiaolong.ControlCenter.exe", "Jiaolong.ControlCenter.dll", "Jiaolong.Service.exe", "Jiaolong.Service.dll",
            "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "Microsoft.UI.Xaml.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };
        var entries = new Dictionary<string, string>
        {
            ["support.json"] = JsonSerializer.Serialize(new
            {
                exportedAt = DateTimeOffset.Now, windows = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(), sessionStatus, ipcConnected,
                deviceSupport = state?.Capabilities.SupportState.ToString(), reason = state?.Capabilities.Reason,
                modules = files.ToDictionary(name => name, name => ReadFileVersion(Path.Combine(install, name)))
            }, new JsonSerializerOptions { WriteIndented = true }),
            ["capabilities.json"] = JsonSerializer.Serialize(state?.Capabilities),
            ["README.txt"] = "XISURA 诊断日志。support.json 记录版本、连接与设备状态；service-status.txt 记录 Windows 服务注册/启动状态；service-events.xml 记录最近两天的相关服务事件；client.log 和 service-*.ndjson 为最近日志。服务已连接时附加 service/ 诊断。读取失败也保留错误原因。导出自动脱敏个人路径、SID 和凭据；不包含预设文件、个人文档或完整进程列表。"
        };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        entries["client.log"] = SupportDiagnosticBundle.ReadLogTail(Path.Combine(local, "Jiaolong Control Center", "logs", "application.log"));
        foreach (var name in new[] { "events.ndjson", "command-journal.ndjson" })
            entries["service-" + name] = ipcConnected
                ? "[included in service/logs.ndjson when IPC export succeeds]"
                : ReadServiceLogFallback(name);
        entries["device.json"] = await Task.Run(ReadDevice, token);
        var status = new List<string>();
        foreach (var operation in new[] { "queryex", "qc", "qfailure" })
            status.Add(await ReadToolAsync("sc.exe", [operation, "JiaolongControlService"], token));
        entries["service-status.txt"] = string.Join("\n\n", status);
        const string query = "*[System[Provider[@Name='Service Control Manager'] and TimeCreated[timediff(@SystemTime) <= 172800000]] and EventData[Data='JiaolongControlService' or Data='XISURA Control Service' or Data='Jiaolong Control Service']]";
        entries["service-events.xml"] = await ReadToolAsync("wevtutil.exe", ["qe", "System", "/q:" + query, "/rd:true", "/c:30", "/f:xml"], token);
        return entries;
    }

    internal static void AddServiceLogFallback(IDictionary<string, string> entries)
    {
        foreach (var name in new[] { "events.ndjson", "command-journal.ndjson" })
            entries["service-" + name] = ReadServiceLogFallback(name);
    }

    private static string ReadServiceLogFallback(string name)
    {
        var current = Path.Combine(DiagnosticPaths.ServiceLogsDirectory, name);
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "config", "systemprofile", "AppData", "Local", "JiaolongControlCenter", "Diagnostics", "Logs", name);
        foreach (var path in new[] { current, legacy }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return "[unsafe reparse point]";
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var offset = Math.Max(0, file.Length - 512 * 1024);
                file.Position = offset;
                var bytes = new byte[(int)Math.Min(512 * 1024, file.Length)];
                var count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                var text = Encoding.UTF8.GetString(bytes, 0, count);
                return text.Length == 0 ? "[empty]" : offset == 0 ? text : "[truncated to recent log tail]\n" + text;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return "[access denied]"; }
            catch (IOException error) { return $"[read failed: {error.HResult:X8}]"; }
        }
        return "[missing: current and legacy service log paths]";
    }

    private static string ReadFileVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "present" : "missing"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return error.Message; }
    }

    private static string ReadDevice()
    {
        var result = new Dictionary<string, object>();
        foreach (var (type, fields) in new[] { ("Win32_ComputerSystem", new[] { "Manufacturer", "Model" }),
            ("Win32_BaseBoard", new[] { "Manufacturer", "Product" }), ("Win32_BIOS", new[] { "SMBIOSBIOSVersion" }),
            ("Win32_Processor", new[] { "Name", "Manufacturer", "NumberOfCores" }),
            ("Win32_VideoController", new[] { "Name", "DriverVersion", "PNPDeviceID" }) })
        {
            try
            {
                using var search = new ManagementObjectSearcher("SELECT " + string.Join(",", fields) + " FROM " + type);
                search.Options.Timeout = TimeSpan.FromSeconds(3);
                using var items = search.Get();
                result[type] = items.Cast<ManagementBaseObject>().Select(item =>
                {
                    using (item) return fields.ToDictionary(field => field, field => item[field]?.ToString());
                }).ToArray();
            }
            catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
            { result[type] = new { error = error.Message }; }
        }
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<string> ReadToolAsync(string name, string[] arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return $"{name} {arguments[0]} (exit={process.ExitCode})\n{await output}\n{await error}";
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            token.ThrowIfCancellationRequested();
            return $"{name}: read timed out";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return $"{name}: {error.Message}"; }
    }
}
