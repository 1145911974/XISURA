using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class MiHomeControlBindingTests
{
    [TestMethod]
    [DataRow(MiHomeControlKind.PerformanceMode, 8)]
    [DataRow(MiHomeControlKind.Touchpad, 12)]
    [DataRow(MiHomeControlKind.FnLock, 11)]
    [DataRow(MiHomeControlKind.LidLogo, 15)]
    public void Known_home_controls_use_the_verified_semantic_method(MiHomeControlKind control, int method)
    {
        var decision = WritableDecision("performanceMode", "quickSetting:touchpad", "quickSetting:fnLock", "quickSetting:lidLogo");

        Assert.IsTrue(MiHomeControlBindingFactory.TryCreate(decision, control, out var binding));
        Assert.AreEqual(method, binding!.MethodName);
        Assert.AreEqual(251, binding.WriteType);
    }

    [TestMethod]
    public void Binding_is_denied_when_manifest_does_not_allow_the_control()
    {
        var decision = WritableDecision("performanceMode");

        Assert.IsFalse(MiHomeControlBindingFactory.TryCreate(decision, MiHomeControlKind.Touchpad, out _));
    }

    [TestMethod]
    public async Task Write_once_builds_a_32_byte_semantic_request()
    {
        var transport = new CapturingWriteTransport([0x80]);
        var client = new MiCommonInterfaceClient(writeTransport: transport);
        var binding = new VerifiedWmiBinding("root\\WMI", "MICommonInterface", "ACPI\\PNP0C14\\MIFS_0", 250, 251, 8, "fixture");

        var result = await client.WriteOnceAsync(binding, new byte[] { 2 }, CancellationToken.None);

        Assert.AreEqual(DataQuality.Good, result.Quality);
        Assert.AreEqual(32, transport.Request!.Length);
        Assert.AreEqual(251, transport.Request[1]);
        Assert.AreEqual(8, transport.Request[3]);
        Assert.AreEqual(2, transport.Request[4]);
    }

    [TestMethod]
    public async Task Write_once_accepts_empty_response_as_successful_write_invocation()
    {
        var transport = new CapturingWriteTransport([]);
        var client = new MiCommonInterfaceClient(writeTransport: transport);
        var binding = new VerifiedWmiBinding("root\\WMI", "MICommonInterface", "ACPI\\PNP0C14\\MIFS_0", 250, 251, 20, "fixture");

        var result = await client.WriteOnceAsync(binding, new byte[] { 1 }, CancellationToken.None);

        Assert.AreEqual(DataQuality.Good, result.Quality);
    }

    [TestMethod]
    public void Home_controls_read_the_first_data_byte()
    {
        Assert.AreEqual(4, MiHomeControlBindingFactory.ResponseIndex(MiHomeControlKind.FnLock));
    }

    private static CompatibilityDecision WritableDecision(params string[] keys) => new(
        CompatibilityMode.Writable,
        new CapabilitySnapshot(keys.Select(key => new CapabilityDescriptor(key, CapabilityState.Available, null)).ToArray()),
        new CompatibilityManifest
        {
            ManifestId = "fixture",
            WmiProvider = new WmiProviderEvidence
            {
                Namespace = "root\\WMI",
                Class = "MICommonInterface",
                InstanceName = "ACPI\\PNP0C14\\MIFS_0",
                ReadType = 250,
                WriteType = 251
            }
        },
        []);

    private sealed class CapturingWriteTransport(byte[] response) : IMiWriteTransport
    {
        public byte[]? Request { get; private set; }

        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            Request = request.ToArray();
            return Task.FromResult<byte[]?>(response);
        }
    }
}
