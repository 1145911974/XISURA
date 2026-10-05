using System.Management;
using System.Security.Cryptography;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Service.Home;

internal static class PawnIoDependencyProbe
{
    public static VerifiedDependency? ReadInstalled()
    {
        try
        {
            var driverPath = ReadDriverPath();
            var metadata = ReadSignedDriverMetadata();
            if (driverPath is null || metadata is null || !File.Exists(driverPath)) return null;

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(driverPath)));
            return CreateVerifiedDependency(driverPath, metadata.Value.Version, metadata.Value.Publisher, hash);
        }
        catch
        {
            return null;
        }
    }

    public static VerifiedDependency? CreateVerifiedDependency(
        string? driverPath,
        string? version,
        string? publisher,
        string? sha256)
    {
        if (string.IsNullOrWhiteSpace(driverPath) ||
            string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(publisher) ||
            string.IsNullOrWhiteSpace(sha256) ||
            sha256.Length != 64 ||
            !sha256.All(Uri.IsHexDigit))
        {
            return null;
        }

        return new VerifiedDependency("PawnIO", version.Trim(), publisher.Trim(), sha256.Trim().ToUpperInvariant());
    }

    private static string? ReadDriverPath()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT PathName FROM Win32_SystemDriver WHERE Name='PawnIO'");
        var rawPath = searcher.Get()
            .Cast<ManagementObject>()
            .Select(item => item["PathName"]?.ToString())
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        return NormalizeDriverPath(rawPath);
    }

    private static (string Version, string Publisher)? ReadSignedDriverMetadata()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, DeviceName, DriverProviderName, DriverVersion FROM Win32_PnPSignedDriver");
        var item = searcher.Get()
            .Cast<ManagementObject>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate["DeviceID"]?.ToString(), @"ROOT\PAWNIO\0000", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate["DeviceName"]?.ToString(), "PawnIO", StringComparison.OrdinalIgnoreCase));
        var version = item?["DriverVersion"]?.ToString();
        var publisher = item?["DriverProviderName"]?.ToString();
        return string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(publisher)
            ? null
            : (version.Trim(), publisher.Trim());
    }

    private static string? NormalizeDriverPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;

        var path = rawPath.Trim().Trim('"');
        const string systemRoot = @"\SystemRoot\";
        if (path.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[systemRoot.Length..]);
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);

        return path;
    }
}
