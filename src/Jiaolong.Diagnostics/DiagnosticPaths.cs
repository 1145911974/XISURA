namespace Jiaolong.Diagnostics;

public static class DiagnosticPaths
{
    public static string ServiceLogsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "JiaolongControlCenter", "Diagnostics", "Logs");
}
