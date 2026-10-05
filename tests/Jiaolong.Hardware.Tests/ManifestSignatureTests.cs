using Jiaolong.Hardware.Abstractions.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class ManifestSignatureTests
{
    [TestMethod]
    public async Task One_byte_manifest_change_fails_signature_and_forces_read_only()
    {
        var fixture = new ManifestResolverFixture();
        var decision = await fixture.ResolveTamperedManifestAsync(byteOffset: 32, xorMask: 0x01);
        Assert.AreEqual(CompatibilityMode.ReadOnlySafeMode, decision.Mode);
        CollectionAssert.Contains(decision.Reasons.Select(reason => reason.Code).ToArray(), "manifestSignatureInvalid");
    }

    [TestMethod]
    public async Task Valid_signature_from_unpinned_certificate_forces_read_only()
    {
        var fixture = new ManifestResolverFixture();
        var decision = await fixture.ResolveManifestSignedByAnotherCertificateAsync();
        Assert.AreEqual(CompatibilityMode.ReadOnlySafeMode, decision.Mode);
        CollectionAssert.Contains(decision.Reasons.Select(reason => reason.Code).ToArray(), "manifestSignerUntrusted");
    }
}
