namespace Jiaolong.Hardware.Abstractions.Models;

public sealed record HardwareFingerprint
{
    public HardwareFingerprint(
        string boardProduct,
        string biosVersion,
        string cpuModel,
        string gpuName,
        bool hasVerifiedWritableEvidence)
    {
        BoardProduct = boardProduct;
        BiosVersion = biosVersion;
        CpuModel = cpuModel;
        GpuName = gpuName;
        HasVerifiedWritableEvidence = hasVerifiedWritableEvidence;
    }

    public string BoardProduct { get; init; }

    public string BiosVersion { get; init; }

    public string CpuModel { get; init; }

    public string GpuName { get; init; }

    public bool HasVerifiedWritableEvidence { get; init; }

    public string CpuVendor { get; init; } = string.Empty;

    public string GpuVendorId { get; init; } = string.Empty;

    public IReadOnlyList<string> GpuPnpDeviceIdsExact { get; init; } = Array.Empty<string>();

    public VerifiedOemProvider? OemProvider { get; init; }

    public IReadOnlyList<VerifiedDependency> Dependencies { get; init; } = Array.Empty<VerifiedDependency>();
}

public sealed record VerifiedOemProvider(
    string Namespace,
    string Class,
    string InstanceName,
    int ReadType,
    int WriteType);

public sealed record VerifiedDependency(
    string Name,
    string Version,
    string Publisher,
    string Sha256);
