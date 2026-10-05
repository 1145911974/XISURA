namespace Jiaolong.Service.Storage;

public static class MachinePaths
{
    public static string BaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "JiaolongControlCenter");

    public static string CommandsDirectory => Path.Combine(BaseDirectory, "Commands");
    public static string LastKnownGoodFile => Path.Combine(BaseDirectory, "last-known-good.json");

    public static void EnsureSafeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Reparse points are not allowed: {path}");
        }
    }

    public static void EnsureSafeFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Reparse points are not allowed: {path}");
        }
    }
}
