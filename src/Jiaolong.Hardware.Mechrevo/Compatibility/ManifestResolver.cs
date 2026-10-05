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

        if (!loadedManifest.Manifest.MatchesWritableFingerprint(fingerprint))
        {
            var manifestEvidenceIncomplete =
                loadedManifest.Manifest.Dependencies.Length == 0 ||
                loadedManifest.Manifest.Gpus.Any(gpu => gpu.PnpDeviceIdsExact.Length == 0);
            return Task.FromResult(ReadOnly(
                loadedManifest.Manifest,
                !fingerprint.HasVerifiedWritableEvidence || manifestEvidenceIncomplete
                    ? "verifiedWritableEvidenceMissing"
                    : "fingerprintMismatch"));
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

    private static CompatibilityDecision ReadOnly(CompatibilityManifest? manifest, string code) =>
        new(
            CompatibilityMode.ReadOnlySafeMode,
            new CapabilitySnapshot(
                manifest?.Capabilities
                    .Select(capability => new CapabilityDescriptor(capability.Key, CapabilityState.Unavailable, code))
                    .ToArray() ?? Array.Empty<CapabilityDescriptor>()),
            manifest,
            [new CompatibilityReason(code, "Hardware writes remain unavailable until all evidence and signature checks pass.")]);
}
