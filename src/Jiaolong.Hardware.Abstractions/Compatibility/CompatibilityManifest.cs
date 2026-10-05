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
    {
        if (!fingerprint.HasVerifiedWritableEvidence ||
            !BoardProductsExact.Contains(fingerprint.BoardProduct, StringComparer.Ordinal) ||
            !BiosVersionsExact.Contains(fingerprint.BiosVersion, StringComparer.Ordinal) ||
            !string.Equals(Cpu.Vendor, fingerprint.CpuVendor, StringComparison.Ordinal) ||
            !fingerprint.CpuModel.Contains(Cpu.ModelContains, StringComparison.Ordinal) ||
            Dependencies.Length == 0 ||
            fingerprint.OemProvider is null ||
            !WmiProvider.Matches(fingerprint.OemProvider) ||
            fingerprint.Dependencies.Count != Dependencies.Length)
        {
            return false;
        }

        var gpu = Gpus.FirstOrDefault(candidate =>
            string.Equals(candidate.VendorId, fingerprint.GpuVendorId, StringComparison.OrdinalIgnoreCase) &&
            fingerprint.GpuName.Contains(candidate.NameContains, StringComparison.Ordinal));

        if (gpu is null ||
            gpu.PnpDeviceIdsExact.Length == 0 ||
            !fingerprint.GpuPnpDeviceIdsExact.Any(id => gpu.PnpDeviceIdsExact.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return Dependencies.All(required => fingerprint.Dependencies.Any(observed => required.Matches(observed)));
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
