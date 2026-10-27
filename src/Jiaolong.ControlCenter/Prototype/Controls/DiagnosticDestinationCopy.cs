namespace Jiaolong_ControlCenter.Prototype.Controls;

internal static class DiagnosticDestinationCopy
{
    internal static async Task CopyAsync(Stream source, string destination, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(path)!;
        string staging = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }
            // Keep an existing export intact until the new archive is fully written.
            if (File.Exists(path)) File.Replace(staging, path, null);
            else File.Move(staging, path);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
