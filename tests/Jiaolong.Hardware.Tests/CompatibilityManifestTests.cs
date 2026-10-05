using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Hardware.Mechrevo.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CompatibilityManifestTests
{
    [TestMethod]
    public Task Exact_board_and_bios_are_writable() => AssertWritable("MRID6-23", "MRID6_23_P_V39", expectedWritable: true);

    [TestMethod]
    public Task Rollback_bios_is_read_only() => AssertWritable("MRID6-23", "MRID6_23_P_V40", expectedWritable: false);

    [TestMethod]
    public Task Other_board_is_read_only() => AssertWritable("OTHER", "MRID6_23_P_V39", expectedWritable: false);

    private async Task AssertWritable(string board, string bios, bool expectedWritable)
    {
        var fixture = new ManifestResolverFixture();
        var decision = await fixture.ResolveAsync(board, bios);
        Assert.AreEqual(expectedWritable, decision.Mode == CompatibilityMode.Writable);
    }
}

internal sealed class ManifestResolverFixture
{
    private const string ProviderNamespace = "root\\WMI";
    private const string ProviderClass = "MICommonInterface";
    private const string ProviderInstance = "ACPI\\PNP0C14\\MIFS_0";
    private const string DependencyHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly byte[] manifestContent;
    private readonly byte[] manifestSignature;
    private readonly X509Certificate2 trustedCertificate;
    private readonly ManifestVerifier verifier;

    public ManifestResolverFixture()
    {
        trustedCertificate = CreateCertificate("CN=Jiaolong test signer");
        verifier = new ManifestVerifier(trustedCertificate.RawData);
        manifestContent = ManifestLoader.Serialize(CreateManifest());
        manifestSignature = Sign(manifestContent, trustedCertificate);
    }

    public Task<CompatibilityDecision> ResolveAsync(string board, string bios) =>
        CreateResolver(manifestContent, manifestSignature).ResolveAsync(CreateFingerprint(board, bios), CancellationToken.None);

    public Task<CompatibilityDecision> ResolveTamperedManifestAsync(int byteOffset, byte xorMask)
    {
        var tampered = manifestContent.ToArray();
        tampered[byteOffset] ^= xorMask;
        return CreateResolver(tampered, manifestSignature)
            .ResolveAsync(CreateFingerprint("MRID6-23", "MRID6_23_P_V39"), CancellationToken.None);
    }

    public Task<CompatibilityDecision> ResolveManifestSignedByAnotherCertificateAsync()
    {
        using var other = CreateCertificate("CN=Jiaolong other signer");
        return CreateResolver(manifestContent, Sign(manifestContent, other))
            .ResolveAsync(CreateFingerprint("MRID6-23", "MRID6_23_P_V39"), CancellationToken.None);
    }

    private ManifestResolver CreateResolver(byte[] content, byte[] signature)
    {
        try
        {
            return new ManifestResolver(new ManifestLoader().Load(content, signature), verifier);
        }
        catch (ManifestLoadException)
        {
            return new ManifestResolver("manifestSignatureInvalid");
        }
    }

    private static HardwareFingerprint CreateFingerprint(string board, string bios) =>
        new(board, bios, "AMD Ryzen 7 7745HX", "NVIDIA RTX 4070 Laptop GPU", true)
        {
            CpuVendor = "AuthenticAMD",
            GpuVendorId = "10DE",
            GpuPnpDeviceIdsExact = ["PCI\\VEN_10DE&DEV_2820"],
            OemProvider = new VerifiedOemProvider(ProviderNamespace, ProviderClass, ProviderInstance, 250, 251),
            Dependencies = [new VerifiedDependency("fixture-oem-provider", "1.0.0", "Fixture Publisher", DependencyHash)]
        };

    private static CompatibilityManifest CreateManifest() => new()
    {
        SchemaVersion = 1,
        ManifestId = "fixture-manifest-v1",
        DeviceFamily = "FixtureDevice",
        BoardProductsExact = ["MRID6-23"],
        BiosVersionsExact = ["MRID6_23_P_V39"],
        Cpu = new CpuCompatibility { Vendor = "AuthenticAMD", ModelContains = "AMD Ryzen 7 7745HX" },
        Gpus =
        [
            new GpuCompatibility
            {
                VendorId = "10DE",
                PnpDeviceIdsExact = ["PCI\\VEN_10DE&DEV_2820"],
                NameContains = "RTX 4070 Laptop GPU"
            }
        ],
        WmiProvider = new WmiProviderEvidence
        {
            Namespace = ProviderNamespace,
            Class = ProviderClass,
            InstanceName = ProviderInstance,
            ReadType = 250,
            WriteType = 251
        },
        Dependencies =
        [
            new DependencyRequirement
            {
                Name = "fixture-oem-provider",
                Version = "1.0.0",
                Publisher = "Fixture Publisher",
                Sha256 = DependencyHash
            }
        ],
        Safety = new SafetyPolicy { CpuCriticalC = 95, GpuCriticalC = 87, CircuitFailures = 3, CircuitBreakSeconds = 60 },
        Capabilities = [new CapabilityBinding { Key = "fixture.readback", State = Jiaolong.Contracts.Models.CapabilityState.Available }],
        Migration = new MigrationRules
        {
            SemanticKeys = ["fixture.readback"],
            ForbiddenLowLevelKeys = ["methodName", "ecAddress", "rawPayload"]
        }
    };

    private static X509Certificate2 CreateCertificate(string subject)
    {
        var rsa = RSA.Create(3072);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static byte[] Sign(byte[] content, X509Certificate2 certificate)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1", "SHA256")
        };
        cms.ComputeSignature(signer);
        return cms.Encode();
    }
}
