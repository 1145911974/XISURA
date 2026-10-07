using System.Text.Json.Serialization;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Abstractions.Compatibility;

public enum CompatibilityMode
{
    ReadOnlySafeMode,
    Writable
}

public sealed record CompatibilityReason(string Code, string Message);

public sealed record CompatibilityDecision(
    CompatibilityMode Mode,
    CapabilitySnapshot Capabilities,
    CompatibilityManifest? Manifest,
    IReadOnlyList<CompatibilityReason> Reasons);

public sealed record CompatibilityManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("manifestId")]
    public string ManifestId { get; init; } = string.Empty;

    [JsonPropertyName("deviceFamily")]
    public string DeviceFamily { get; init; } = string.Empty;

    [JsonPropertyName("boardProductsExact")]
    public string[] BoardProductsExact { get; init; } = Array.Empty<string>();

    [JsonPropertyName("biosVersionsExact")]
    public string[] BiosVersionsExact { get; init; } = Array.Empty<string>();

    [JsonPropertyName("cpu")]
    public CpuCompatibility Cpu { get; init; } = new();

    [JsonPropertyName("gpus")]
    public GpuCompatibility[] Gpus { get; init; } = Array.Empty<GpuCompatibility>();

    [JsonPropertyName("wmiProvider")]
    public WmiProviderEvidence WmiProvider { get; init; } = new();

    [JsonPropertyName("dependencies")]
    public DependencyRequirement[] Dependencies { get; init; } = Array.Empty<DependencyRequirement>();

    [JsonPropertyName("safety")]
    public SafetyPolicy Safety { get; init; } = new();

    [JsonPropertyName("capabilities")]
    public CapabilityBinding[] Capabilities { get; init; } = Array.Empty<CapabilityBinding>();

    [JsonPropertyName("migration")]
    public MigrationRules Migration { get; init; } = new();

    public bool MatchesWritableFingerprint(HardwareFingerprint fingerprint)
        => WritableMismatchReason(fingerprint) is null;

    public string? WritableMismatchReason(HardwareFingerprint fingerprint)
    {
        if (!BoardProductsExact.Contains(fingerprint.BoardProduct, StringComparer.Ordinal)) return "boardUnsupported";
        if (!BiosVersionsExact.Contains(fingerprint.BiosVersion, StringComparer.Ordinal)) return "biosUnsupported";
        if (!string.Equals(Cpu.Vendor, fingerprint.CpuVendor, StringComparison.Ordinal) ||
            !fingerprint.CpuModel.Contains(Cpu.ModelContains, StringComparison.Ordinal)) return "cpuUnsupported";
        if (Dependencies.Length == 0) return "manifestEvidenceIncomplete";
        if (fingerprint.OemProvider is null) return "oemInterfaceUnavailable";
        if (!WmiProvider.Matches(fingerprint.OemProvider)) return "oemInterfaceMismatch";

        var gpu = Gpus.FirstOrDefault(candidate =>
            string.Equals(candidate.VendorId, fingerprint.GpuVendorId, StringComparison.OrdinalIgnoreCase) &&
            fingerprint.GpuName.Contains(candidate.NameContains, StringComparison.Ordinal));

        if (gpu is null) return "gpuUnsupported";
        if (gpu.PnpDeviceIdsExact.Length == 0) return "manifestEvidenceIncomplete";
        if (fingerprint.GpuPnpDeviceIdsExact.Count == 0) return "gpuEvidenceMissing";
        if (!fingerprint.GpuPnpDeviceIdsExact.Any(observed => PciHardwareId(observed) is { } hardwareId &&
            gpu.PnpDeviceIdsExact.Any(expected => string.Equals(hardwareId, PciHardwareId(expected), StringComparison.OrdinalIgnoreCase))))
            return "gpuHardwareMismatch";
        if (Dependencies.Any(required => !fingerprint.Dependencies.Any(observed => string.Equals(required.Name, observed.Name, StringComparison.Ordinal))))
            return "hardwareDependencyMissing";
        if (fingerprint.Dependencies.Count != Dependencies.Length ||
            !Dependencies.All(required => fingerprint.Dependencies.Any(observed => required.Matches(observed))))
            return "hardwareDependencyMismatch";
        return fingerprint.HasVerifiedWritableEvidence ? null : "verifiedWritableEvidenceMissing";
    }

    private static string? PciHardwareId(string instanceId)
    {
        // The instance suffix identifies one computer's PCI location; VEN/DEV/SUBSYS/REV identify the verified hardware.
        var parts = instanceId.Split('\\');
        if (parts.Length is < 2 or > 3 || !string.Equals(parts[0], "PCI", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Contains("&DEV_", StringComparison.OrdinalIgnoreCase) ||
            parts.Length == 3 && string.IsNullOrWhiteSpace(parts[2])) return null;
        return $"{parts[0]}\\{parts[1]}";
    }
}

public sealed record CpuCompatibility
{
    [JsonPropertyName("vendor")]
    public string Vendor { get; init; } = string.Empty;

    [JsonPropertyName("modelContains")]
    public string ModelContains { get; init; } = string.Empty;
}

public sealed record GpuCompatibility
{
    [JsonPropertyName("vendorId")]
    public string VendorId { get; init; } = string.Empty;

    [JsonPropertyName("pnpDeviceIdsExact")]
    public string[] PnpDeviceIdsExact { get; init; } = Array.Empty<string>();

    [JsonPropertyName("nameContains")]
    public string NameContains { get; init; } = string.Empty;
}

public sealed record WmiProviderEvidence
{
    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = string.Empty;

    [JsonPropertyName("class")]
    public string Class { get; init; } = string.Empty;

    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = string.Empty;

    [JsonPropertyName("readType")]
    public int ReadType { get; init; }

    [JsonPropertyName("writeType")]
    public int WriteType { get; init; }

    public bool Matches(VerifiedOemProvider observed) =>
        string.Equals(Namespace, observed.Namespace, StringComparison.Ordinal) &&
        string.Equals(Class, observed.Class, StringComparison.Ordinal) &&
        string.Equals(InstanceName, observed.InstanceName, StringComparison.Ordinal) &&
        ReadType == observed.ReadType &&
        WriteType == observed.WriteType;
}

public sealed record DependencyRequirement
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    public bool Matches(VerifiedDependency observed) =>
        string.Equals(Name, observed.Name, StringComparison.Ordinal) &&
        string.Equals(Version, observed.Version, StringComparison.Ordinal) &&
        string.Equals(Publisher, observed.Publisher, StringComparison.Ordinal) &&
        string.Equals(Sha256, observed.Sha256, StringComparison.OrdinalIgnoreCase);
}

public sealed record SafetyPolicy
{
    [JsonPropertyName("cpuCriticalC")]
    public int CpuCriticalC { get; init; }

    [JsonPropertyName("gpuCriticalC")]
    public int GpuCriticalC { get; init; }

    [JsonPropertyName("circuitFailures")]
    public int CircuitFailures { get; init; }

    [JsonPropertyName("circuitBreakSeconds")]
    public int CircuitBreakSeconds { get; init; }
}

public sealed record CapabilityBinding
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public CapabilityState State { get; init; } = CapabilityState.Unavailable;
}

public sealed record MigrationRules
{
    [JsonPropertyName("semanticKeys")]
    public string[] SemanticKeys { get; init; } = Array.Empty<string>();

    [JsonPropertyName("forbiddenLowLevelKeys")]
    public string[] ForbiddenLowLevelKeys { get; init; } = Array.Empty<string>();
}
