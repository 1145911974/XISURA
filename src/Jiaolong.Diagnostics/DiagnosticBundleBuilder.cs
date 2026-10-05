using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Jiaolong.Diagnostics;

public interface IDiagnosticBundleBuilder
{
    Task<DiagnosticExport> BuildAsync(DiagnosticExportRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid exportId, CancellationToken cancellationToken);
}

public sealed record DiagnosticBundleContent(
    string VersionsJson,
    string FingerprintJson,
    string CapabilitiesJson,
    string ServiceHealthJson,
    string TelemetryJson,
    string LogsNdjson);

public sealed record DiagnosticExportRequest(string RequestingSid, DiagnosticBundleContent Content);

public sealed record DiagnosticExport(
    string FilePath,
    IReadOnlyList<string> EntryNames,
    string ChecksumsSha256);

public sealed class DiagnosticBundleBuilder : IDiagnosticBundleBuilder
{
    private static readonly string[] EntryNames =
    [
        "versions.json",
        "fingerprint.json",
        "capabilities.json",
        "service-health.json",
        "telemetry-60s.json",
        "logs.ndjson"
    ];

    private readonly string baseDirectory;
    private readonly bool applyAcl;

    public DiagnosticBundleBuilder()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "JiaolongControlCenter",
                "Diagnostics",
                "Exports"),
            applyAcl: true)
    {
    }

    internal DiagnosticBundleBuilder(string baseDirectory)
        : this(baseDirectory, applyAcl: false)
    {
    }

    private DiagnosticBundleBuilder(string baseDirectory, bool applyAcl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        this.baseDirectory = Path.GetFullPath(baseDirectory);
        this.applyAcl = applyAcl;
    }

    public async Task<DiagnosticExport> BuildAsync(
        DiagnosticExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSid(request.RequestingSid);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(baseDirectory);
        RejectReparsePoint(baseDirectory);

        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [EntryNames[0]] = Redact(request.Content.VersionsJson),
            [EntryNames[1]] = Redact(request.Content.FingerprintJson),
            [EntryNames[2]] = Redact(request.Content.CapabilitiesJson),
            [EntryNames[3]] = Redact(request.Content.ServiceHealthJson),
            [EntryNames[4]] = Redact(request.Content.TelemetryJson),
            [EntryNames[5]] = Redact(request.Content.LogsNdjson)
        };
        var checksumText = string.Join(
            Environment.NewLine,
            contents.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()}  {pair.Key}")) +
            Environment.NewLine;
        contents["checksums.sha256"] = Encoding.UTF8.GetBytes(checksumText);

        var filePath = Path.Combine(baseDirectory, $"{Guid.NewGuid():D}.zip");
        try
        {
            await using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var entryName in contents.Keys.OrderBy(name => name, StringComparer.Ordinal))
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(contents[entryName], cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (applyAcl)
            {
                ApplyReadAcl(filePath, request.RequestingSid);
            }

            return new DiagnosticExport(filePath, contents.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(), checksumText);
        }
        catch
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            throw;
        }
    }

    public Task DeleteAsync(Guid exportId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (exportId == Guid.Empty) throw new ArgumentException("Export id is required.", nameof(exportId));
        if (!Directory.Exists(baseDirectory)) return Task.CompletedTask;
        RejectReparsePoint(baseDirectory);

        var filePath = Path.Combine(baseDirectory, $"{exportId:D}.zip");
        if (!File.Exists(filePath)) return Task.CompletedTask;
        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Diagnostic export cannot be a reparse point.");
        File.Delete(filePath);
        return Task.CompletedTask;
    }

    private static byte[] Redact(string text) => Encoding.UTF8.GetBytes(RedactionPolicy.RedactText(text));

    private static void ValidateSid(string sid)
    {
        try
        {
            _ = new SecurityIdentifier(sid);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Diagnostic export requires a valid requesting SID.", nameof(sid), exception);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Diagnostic export directory cannot be a reparse point.");
        }
    }

    private static void ApplyReadAcl(string filePath, string requestingSid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddReadRule(security, "S-1-5-18");
        AddReadRule(security, "S-1-5-32-544");
        AddReadRule(security, requestingSid);
        new FileInfo(filePath).SetAccessControl(security);
    }

    private static void AddReadRule(FileSecurity security, string sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
    }
}
