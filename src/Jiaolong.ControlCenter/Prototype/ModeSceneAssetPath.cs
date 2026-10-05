namespace Jiaolong_ControlCenter.Prototype;

public static class ModeSceneAssetPath
{
    private const string AppxPrefix = "ms-appx:///";

    public static string Resolve(string assetUri, string baseDirectory)
    {
        if (!assetUri.StartsWith(AppxPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Mode scene assets must use an ms-appx URI.", nameof(assetUri));

        var relative = Uri.UnescapeDataString(assetUri[AppxPrefix.Length..])
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(baseDirectory, relative));
    }
}
