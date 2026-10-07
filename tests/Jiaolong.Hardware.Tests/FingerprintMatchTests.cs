using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class FingerprintMatchTests
{
    [TestMethod]
    public async Task Same_verified_gpu_on_another_computer_matches_but_other_hardware_and_missing_dependencies_do_not()
    {
        var manifest = ManifestLoader.LoadEmbedded().Manifest;
        var gpu = manifest.Gpus[0];
        var provider = manifest.WmiProvider;
        var fingerprint = new HardwareFingerprint(manifest.BoardProductsExact[0], manifest.BiosVersionsExact[0],
            manifest.Cpu.ModelContains, gpu.NameContains, true)
        {
            CpuVendor = manifest.Cpu.Vendor, GpuVendorId = gpu.VendorId,
            GpuPnpDeviceIdsExact = [gpu.PnpDeviceIdsExact[0][..gpu.PnpDeviceIdsExact[0].LastIndexOf('\\')] + @"\4&ABCDEF01&0&0009"],
            OemProvider = new(provider.Namespace, provider.Class, provider.InstanceName, provider.ReadType, provider.WriteType),
            Dependencies = manifest.Dependencies.Select(item => new VerifiedDependency(item.Name, item.Version, item.Publisher, item.Sha256)).ToArray()
        };
        Assert.IsTrue(manifest.MatchesWritableFingerprint(fingerprint), "PCI instance location must not bind compatibility to one computer.");
        foreach (var invalid in new[]
        {
            fingerprint with { BiosVersion = "unverified BIOS" },
            fingerprint with { Dependencies = Array.Empty<VerifiedDependency>() },
            fingerprint with { OemProvider = null },
            fingerprint with { GpuPnpDeviceIdsExact = [fingerprint.GpuPnpDeviceIdsExact[0].Replace("DEV_2820", "DEV_2821")] },
            fingerprint with { GpuPnpDeviceIdsExact = [fingerprint.GpuPnpDeviceIdsExact[0].Replace("SUBSYS_13031D05", "SUBSYS_00000000")] },
            fingerprint with { GpuPnpDeviceIdsExact = [fingerprint.GpuPnpDeviceIdsExact[0].Replace("REV_A1", "REV_A2")] }
        }) Assert.IsFalse(manifest.MatchesWritableFingerprint(invalid));
        var decision = await new ManifestResolver().ResolveAsync(fingerprint with { Dependencies = Array.Empty<VerifiedDependency>() }, CancellationToken.None);
        Assert.AreEqual(CompatibilityMode.Writable, decision.Mode);
        Assert.AreEqual(CapabilityState.Available, decision.Capabilities.Items.Single(item => item.Key == "performanceMode").State);
        Assert.AreEqual(CapabilityState.Unavailable, decision.Capabilities.Items.Single(item => item.Key == "cpuTuning:smu").State);
        foreach (var compatible in new[]
        {
            fingerprint with { BiosVersion = "MRID6_23_P_V41", GpuName = "NVIDIA GeForce RTX 4060 Laptop GPU", GpuPnpDeviceIdsExact = [@"PCI\VEN_10DE&DEV_0000\OTHER_INSTANCE"] },
            fingerprint with { CpuModel = "AMD Ryzen 9 7945HX", BiosVersion = "another BIOS" },
            fingerprint with { BoardProduct = "another OEM board", CpuVendor = "GenuineIntel", CpuModel = "Intel Core i7", Dependencies = [] }
        })
        {
            var result = await new ManifestResolver().ResolveAsync(compatible, CancellationToken.None);
            Assert.AreEqual(CompatibilityMode.Writable, result.Mode);
            Assert.AreEqual(CapabilityState.Available, result.Capabilities.Items.Single(item => item.Key == "performanceMode").State);
            Assert.AreEqual(CapabilityState.Available, result.Capabilities.Items.Single(item => item.Key == "cpuTuning").State);
        }
        var missingProvider = await new ManifestResolver().ResolveAsync(fingerprint with { OemProvider = null }, CancellationToken.None);
        Assert.AreEqual(CapabilityState.Available, missingProvider.Capabilities.Items.Single(item => item.Key == "cpuTuning").State);
        Assert.AreEqual(CapabilityState.Unavailable, missingProvider.Capabilities.Items.Single(item => item.Key == "performanceMode").State);
    }

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
