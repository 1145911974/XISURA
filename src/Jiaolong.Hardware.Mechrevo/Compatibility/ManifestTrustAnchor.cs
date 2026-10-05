using System.Reflection;
using System.Security.Cryptography;

namespace Jiaolong.Hardware.Mechrevo.Compatibility;

public static class ManifestTrustAnchor
{
    public const string CertificateSha256 = "4B6CE0846DBD2BD2DF97E507A81CF0ADEAF8E765A37A156E5B9C35814C3386B9";

    public static byte[] LoadCertificateDer()
    {
        var assembly = typeof(ManifestTrustAnchor).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Compatibility.Signing.Jiaolong.ManifestSigning.cer", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded manifest trust anchor is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var certificate = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(certificate));
        if (!string.Equals(hash, CertificateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Embedded manifest trust anchor hash does not match the pinned value.");
        }

        return certificate;
    }
}
