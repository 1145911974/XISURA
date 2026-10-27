namespace Jiaolong.Service.Hosting;

internal static class CandidateJournalRoot
{
    public static string? Parse(string[] args)
        => Parse(args, out _);

    public static string? Parse(string[] args, out string? stopFile)
    {
        stopFile = null;
        const string option = "--control-journal-root";
        const string stopOption = "--candidate-stop-file";
        if (!args.Any(arg => arg.StartsWith(option, StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith(stopOption, StringComparison.OrdinalIgnoreCase))) return null;
        if ((args.Length != 2 && args.Length != 4) || args[0] != option ||
            (args.Length == 4 && args[2] != stopOption) ||
            string.IsNullOrWhiteSpace(args[1]) || !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("Expected --control-journal-root followed by an existing absolute C: journal directory.");
        string root = Path.GetFullPath(args[1]);
        if (!string.Equals(Path.GetPathRoot(root), @"C:\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Candidate recovery records must remain on C:.");
        string journal = Path.Combine(root, "control-execution.json");
        if (!Directory.Exists(root) || !File.Exists(journal))
            throw new IOException("The original control journal is required; an empty replacement will not be created.");
        RequirePlainDirectories(root);
        if ((File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The original control journal cannot be a reparse point.");
        if (args.Length == 4)
        {
            string requested = args[3];
            if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested) ||
                requested.AsSpan(2).Contains(':') ||
                requested.Split(['\\', '/']).Any(part => part.EndsWith(' ') || part.EndsWith('.')))
                throw new ArgumentException("The candidate stop marker must be an unambiguous absolute C: file path.");
            string marker = Path.GetFullPath(requested);
            if (!string.Equals(Path.GetPathRoot(marker), @"C:\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The candidate stop marker must remain on C:.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(marker), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
                marker.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The candidate stop marker must be outside the recovery journal directory.");
            RequirePlainDirectories(Path.GetDirectoryName(marker)!);
            try
            {
                _ = File.GetAttributes(marker);
            }
            catch (FileNotFoundException)
            {
                stopFile = marker;
                return root;
            }
            throw new IOException("The candidate stop marker must not already exist.");
        }
        return root;
    }

    private static void RequirePlainDirectories(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Candidate directories must exist and cannot contain reparse points.");
    }
}
