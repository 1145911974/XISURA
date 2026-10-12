using System.Diagnostics;

namespace Jiaolong_ControlCenter.Services;

internal static class AppRuntimeLog
{
    private static readonly object Gate = new();
    private static bool applicationLoggingEnabled;

    public static void EnableForApplication() => Volatile.Write(ref applicationLoggingEnabled, true);

    public static void Write(string message)
    {
        // Sessions also run in test and support-tool hosts; only the WinUI app owns client.log.
        if (!Volatile.Read(ref applicationLoggingEnabled))
        {
            Debug.WriteLine(message);
            return;
        }
        try
        {
            lock (Gate)
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Jiaolong Control Center", "logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "application.log"), message);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
        }
    }
}
