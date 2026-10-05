using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Jiaolong.Hardware.Mechrevo.Compatibility;

public sealed record ManifestVerificationResult(bool IsValid, string? ErrorCode)
{
    public static ManifestVerificationResult Valid() => new(true, null);

    public static ManifestVerificationResult Invalid(string code) => new(false, code);
}

public sealed class ManifestVerifier
{
    private static readonly HashSet<string> Sha256OrStronger =
    [
        "2.16.840.1.101.3.4.2.1",
        "2.16.840.1.101.3.4.2.2",
        "2.16.840.1.101.3.4.2.3"
    ];

    private readonly byte[] trustedCertificateDer;

    public ManifestVerifier()
        : this(ManifestTrustAnchor.LoadCertificateDer(), ManifestTrustAnchor.CertificateSha256)
    {
    }

    internal ManifestVerifier(byte[] trustedCertificateDer)
        : this(trustedCertificateDer, Convert.ToHexString(SHA256.HashData(trustedCertificateDer)))
    {
    }

    private ManifestVerifier(byte[] trustedCertificateDer, string expectedHash)
    {
        ArgumentNullException.ThrowIfNull(trustedCertificateDer);
        this.trustedCertificateDer = trustedCertificateDer.ToArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(this.trustedCertificateDer));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Manifest trust anchor hash mismatch.");
        }
    }

    public ManifestVerificationResult Verify(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature)
    {
        try
        {
            var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
            cms.Decode(signature.ToArray());

            if (!cms.Detached)
            {
                return ManifestVerificationResult.Invalid("manifestSignatureNotDetached");
            }

            if (cms.SignerInfos.Count != 1)
            {
                return ManifestVerificationResult.Invalid("manifestSignerCountInvalid");
            }

            var signer = cms.SignerInfos[0];
            if (signer.Certificate is null)
            {
                return ManifestVerificationResult.Invalid("manifestSignerCertificateMissing");
            }

            if (!Sha256OrStronger.Contains(signer.DigestAlgorithm.Value ?? string.Empty))
            {
                return ManifestVerificationResult.Invalid("manifestDigestAlgorithmWeak");
            }

            using var signerCertificate = X509CertificateLoader.LoadCertificate(signer.Certificate.RawData);
            if (DateTime.UtcNow < signerCertificate.NotBefore.ToUniversalTime() ||
                DateTime.UtcNow > signerCertificate.NotAfter.ToUniversalTime())
            {
                return ManifestVerificationResult.Invalid("manifestSignerExpired");
            }

            if (!CryptographicOperations.FixedTimeEquals(signerCertificate.RawData, trustedCertificateDer))
            {
                return ManifestVerificationResult.Invalid("manifestSignerUntrusted");
            }

            cms.CheckSignature(verifySignatureOnly: true);
            return ManifestVerificationResult.Valid();
        }
        catch (CryptographicException)
        {
            return ManifestVerificationResult.Invalid("manifestSignatureInvalid");
        }
        catch (InvalidOperationException)
        {
            return ManifestVerificationResult.Invalid("manifestSignatureInvalid");
        }
    }
}
