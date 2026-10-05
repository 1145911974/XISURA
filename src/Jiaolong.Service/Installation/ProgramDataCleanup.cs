namespace Jiaolong.Service.Installation;

internal static class ProgramDataCleanup
{
    private static readonly string ProductDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "JiaolongControlCenter");

    public static int Run()
    {
        try
        {
            DeleteProductData();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Program data removal failed: {exception.Message}");
            return 1;
        }
    }

    private static void DeleteProductData()
    {
        if (!Directory.Exists(ProductDataPath)) return;
        if (IsReparsePoint(ProductDataPath))
        {
            throw new IOException("Product data root cannot be a reparse point.");
        }

        DeleteDirectoryContents(ProductDataPath);
        Directory.Delete(ProductDataPath);
    }

    private static void DeleteDirectoryContents(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (IsReparsePoint(entry))
            {
                DeleteReparsePoint(entry);
                continue;
            }

            if (Directory.Exists(entry))
            {
                DeleteDirectoryContents(entry);
                Directory.Delete(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void DeleteReparsePoint(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }
}
