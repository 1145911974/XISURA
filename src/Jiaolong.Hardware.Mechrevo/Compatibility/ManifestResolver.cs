using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Mechrevo.Compatibility;

public sealed class ManifestResolver : ICompatibilityResolver
{
    private readonly LoadedCompatibilityManifest? loadedManifest;
    private readonly ManifestVerifier? verifier;
    private readonly string? loadError;

    public ManifestResolver()
        : this(ManifestLoader.LoadEmbedded(), new ManifestVerifier())
    {
    }

    internal ManifestResolver(LoadedCompatibilityManifest loadedManifest, ManifestVerifier verifier)
    {
        this.loadedManifest = loadedManifest;
        this.verifier = verifier;
    }

    internal ManifestResolver(string loadError)
    {
        this.loadError = loadError;
    }

    public Task<CompatibilityDecision> ResolveAsync(
        HardwareFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (loadError is not null || loadedManifest is null || verifier is null)
        {
            return Task.FromResult(ReadOnly(null, loadError ?? "manifestInvalid"));
        }

        var verification = verifier.Verify(loadedManifest.Content, loadedManifest.Signature);
        if (!verification.IsValid)
        {
            return Task.FromResult(ReadOnly(null, verification.ErrorCode ?? "manifestSignatureInvalid"));
        }

        if (loadedManifest.Manifest.DeviceFamily == "Jiaolong16Pro2023")
            return Task.FromResult(ResolveInterfaces(loadedManifest.Manifest, fingerprint));

        if (!loadedManifest.Manifest.MatchesWritableFingerprint(fingerprint))
        {
            var manifestEvidenceIncomplete =
                loadedManifest.Manifest.Dependencies.Length == 0 ||
                loadedManifest.Manifest.Gpus.Any(gpu => gpu.PnpDeviceIdsExact.Length == 0);
            return Task.FromResult(ReadOnly(
                loadedManifest.Manifest,
                !fingerprint.HasVerifiedWritableEvidence || manifestEvidenceIncomplete
                    ? "verifiedWritableEvidenceMissing"
                    : "fingerprintMismatch",
                loadedManifest.Manifest.WritableMismatchReason(fingerprint)));
        }

        var capabilities = new CapabilitySnapshot(
            loadedManifest.Manifest.Capabilities
                .Select(capability => new CapabilityDescriptor(capability.Key, CapabilityState.Available, null))
                .ToArray());
        return Task.FromResult(new CompatibilityDecision(
            CompatibilityMode.Writable,
            capabilities,
            loadedManifest.Manifest,
            [new CompatibilityReason("manifestVerified", "Pinned manifest and exact hardware evidence matched.")]));
    }

    private static CompatibilityDecision ResolveInterfaces(CompatibilityManifest manifest, HardwareFingerprint fingerprint)
    {
        // Hardware identity describes the machine; each transport owns its protocol/readback validation.
        bool mi = fingerprint.OemProvider is not null && manifest.WmiProvider.Matches(fingerprint.OemProvider);
        bool nvidia = fingerprint.GpuVendorId.Equals("10DE", StringComparison.OrdinalIgnoreCase) &&
            fingerprint.GpuPnpDeviceIdsExact.Any(id => id.StartsWith(@"PCI\VEN_10DE&DEV_", StringComparison.OrdinalIgnoreCase));
        bool dependency = manifest.Dependencies.Length > 0 && manifest.Dependencies.All(required =>
            fingerprint.Dependencies.Any(observed => required.Matches(observed)));
        bool amd = fingerprint.CpuVendor == "AuthenticAMD";
        bool knownEc = manifest.BoardProductsExact.Contains(fingerprint.BoardProduct, StringComparer.OrdinalIgnoreCase);
        var capabilities = new CapabilitySnapshot(manifest.Capabilities.Select(item =>
        {
            string? reason = item.Key switch
            {
                "cpuTuning" or "quickSetting:wifi" or "quickSetting:bluetooth" => null,
                "cpuTuning:smu" or "cpuTuning:pbo" when !amd => "advancedCpuProtocolUnavailable",
                "cpuTuning:curveOptimizer" when !amd => "curveOptimizerUnavailable",
                "cpuTuning:smu" or "cpuTuning:pbo" or "cpuTuning:curveOptimizer" when !dependency => "hardwareDependencyMissing",
                "fanControl" when !knownEc => "fanEcProtocolUnavailable",
                "fanControl" => null,
                "gpuVfCurve" or "gpuMemoryOffset" or "gpuCoreOffset" or "gpuFrequencyLimit" when !nvidia => "gpuInterfaceUnavailable",
                "gpuVfCurve" or "gpuMemoryOffset" or "gpuCoreOffset" or "gpuFrequencyLimit" => null,
                "gpuVoltageBoost" or "gpuPowerLimit" => "gpuOperationUnsupported",
                "cpuTuning:smu" or "cpuTuning:pbo" or "cpuTuning:curveOptimizer" => null,
                _ => mi ? null : "oemInterfaceUnavailable"
            };
            return new CapabilityDescriptor(item.Key, reason is null ? CapabilityState.Available : CapabilityState.Unavailable, reason);
        }).ToArray());
        return new CompatibilityDecision(mi ? CompatibilityMode.Writable : CompatibilityMode.ReadOnlySafeMode,
            capabilities, manifest,
            [new CompatibilityReason(mi ? "interfacesDetected" : "oemInterfaceUnavailable", "Capabilities are resolved independently; Windows and GPU interfaces do not depend on OEM model or BIOS."),
                .. mi ? Array.Empty<CompatibilityReason>() : [new CompatibilityReason("fingerprintMismatch", "OEM protocol evidence is unavailable.")]]);
    }

    private static CompatibilityDecision ReadOnly(CompatibilityManifest? manifest, string code, string? detail = null) =>
        new(
            CompatibilityMode.ReadOnlySafeMode,
            new CapabilitySnapshot(
                manifest?.Capabilities
                    .Select(capability => new CapabilityDescriptor(capability.Key, CapabilityState.Unavailable, detail ?? code))
                    .ToArray() ?? Array.Empty<CapabilityDescriptor>()),
            manifest,
            [new CompatibilityReason(detail ?? code, "Hardware writes remain unavailable until all evidence and signature checks pass."),
                .. detail is not null && detail != code ? new[] { new CompatibilityReason(code, "Compatibility evidence did not match.") } : Array.Empty<CompatibilityReason>()]);
}
