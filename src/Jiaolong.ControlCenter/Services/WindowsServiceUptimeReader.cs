using System.Management;

namespace Jiaolong_ControlCenter.Services;

internal static class WindowsServiceUptimeReader
{
    internal static TimeSpan? Read()
    {
        try
        {
            using var services = new ManagementObjectSearcher(
                "SELECT State, ProcessId FROM Win32_Service WHERE Name='JiaolongControlService'");
            using var serviceResults = services.Get();
            var service = serviceResults.Cast<ManagementObject>().FirstOrDefault();
            if (service is null || !string.Equals(service["State"] as string, "Running", StringComparison.Ordinal))
                return null;
            var processId = Convert.ToUInt32(service["ProcessId"]);
            if (processId == 0) return null;

            using var processes = new ManagementObjectSearcher(
                $"SELECT CreationDate FROM Win32_Process WHERE ProcessId={processId}");
            using var processResults = processes.Get();
            var process = processResults.Cast<ManagementObject>().FirstOrDefault();
            if (process?["CreationDate"] is not string creationDate) return null;
            var uptime = DateTime.Now - ManagementDateTimeConverter.ToDateTime(creationDate);
            return uptime >= TimeSpan.Zero ? uptime : null;
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
