using System.Diagnostics;

namespace Jiaolong_ControlCenter.Services;

internal static class AppRuntimeLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
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
