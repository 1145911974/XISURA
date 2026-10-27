using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;

namespace Jiaolong.Service.Home;

public sealed class WindowsSystemBootProbe
{
    internal static DateTimeOffset? ParseBootTimeUtc(string? value)
    {
        if (value is not { Length: 25 } || value.Contains('*') || value[21] is not ('+' or '-') ||
            !DateTime.TryParseExact(value[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            !int.TryParse(value[22..], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes > 14 * 60)
            return null;
        try { return new DateTimeOffset(date, TimeSpan.FromMinutes(value[21] == '+' ? minutes : -minutes)).ToUniversalTime(); }
        catch (ArgumentException) { return null; }
    }

    public Task<DateTimeOffset?> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT LastBootUpTime FROM Win32_OperatingSystem");
            searcher.Options.Timeout = TimeSpan.FromSeconds(5);
            using var results = searcher.Get();
            DateTimeOffset? boot = null;
            int count = 0;
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    token.ThrowIfCancellationRequested();
                    boot = ParseBootTimeUtc(item["LastBootUpTime"] as string);
                    count++;
                }
            }
            token.ThrowIfCancellationRequested();
            return count == 1 ? boot : null;
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException or SecurityException)
        { token.ThrowIfCancellationRequested(); return null; }
    }, token);
}
