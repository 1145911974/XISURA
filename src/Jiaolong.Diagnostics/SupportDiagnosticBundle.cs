using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Jiaolong.Diagnostics;

public static class SupportDiagnosticBundle
{
    private static readonly string[] ServiceEntries =
        ["versions.json", "fingerprint.json", "capabilities.json", "service-health.json", "telemetry-60s.json", "logs.ndjson"];

    public static async Task WriteAsync(Stream target, IReadOnlyDictionary<string, string> entries, string? serviceArchive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, text) in entries)
        {
            if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name == "checksums.sha256")
                throw new ArgumentException("Diagnostic entry must be a plain file name.", nameof(entries));
            contents.Add(name, text);
        }
        if (serviceArchive is not null)
        {
            try
            {
                if ((File.GetAttributes(serviceArchive) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Service export cannot be a reparse point.");
                using var service = ZipFile.OpenRead(serviceArchive);
                foreach (var name in ServiceEntries)
                {
                    var entry = service.GetEntry(name);
                    if (entry is null) continue;
                    if (entry.Length > 1024 * 1024) throw new IOException("Service diagnostic entry exceeds the size limit.");
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    contents.Add("service/" + name, await reader.ReadToEndAsync(cancellationToken));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                contents["service-export-error.txt"] = error.ToString();
            }
        }
        var payloads = contents.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(RedactionPolicy.RedactText(pair.Value)), StringComparer.Ordinal);
        var checksums = string.Join("\n", payloads.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()}  {pair.Key}")) + "\n";
        payloads.Add("checksums.sha256", Encoding.UTF8.GetBytes(checksums));
        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (name, bytes) in payloads)
        {
            await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            await entry.WriteAsync(bytes, cancellationToken);
        }
    }

    public static string ReadLogTail(string path, int maxBytes = 512 * 1024)
    {
        if (maxBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Diagnostic log cannot be a reparse point.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long offset = Math.Max(0, file.Length - maxBytes);
            file.Position = offset;
            var bytes = new byte[(int)Math.Min(maxBytes, file.Length)];
            int count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            var text = Encoding.UTF8.GetString(bytes, 0, count);
            return offset == 0 ? text : "[truncated to recent log tail]\n" + text[(text.IndexOf('\n') + 1)..];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"[unavailable: {error.Message}]";
        }
    }
}
