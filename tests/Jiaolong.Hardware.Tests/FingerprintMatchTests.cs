using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class FingerprintMatchTests
{
    [TestMethod]
    public void Missing_exact_gpu_or_provider_evidence_is_not_writable()
    {
        var fingerprint = new HardwareFingerprint("MRID6-23", "MRID6_23_P_V39", "AMD Ryzen 7 7745HX", "RTX 4070 Laptop GPU", false);
        Assert.IsFalse(fingerprint.HasVerifiedWritableEvidence);
    }

    [TestMethod]
    public async Task Production_manifest_with_nonmatching_exact_gpu_evidence_is_read_only()
    {
        var resolver = new ManifestResolver();
        var fingerprint = new HardwareFingerprint("MRID6-23", "MRID6_23_P_V39", "AMD Ryzen 7 7745HX", "RTX 4070 Laptop GPU", true)
        {
            CpuVendor = "AuthenticAMD",
            GpuVendorId = "10DE",
            GpuPnpDeviceIdsExact = ["PCI\\VEN_10DE&DEV_2820"]
        };

        var decision = await resolver.ResolveAsync(fingerprint, CancellationToken.None);

        Assert.AreEqual(CompatibilityMode.ReadOnlySafeMode, decision.Mode);
        CollectionAssert.Contains(decision.Reasons.Select(reason => reason.Code).ToArray(), "fingerprintMismatch");
    }
}
