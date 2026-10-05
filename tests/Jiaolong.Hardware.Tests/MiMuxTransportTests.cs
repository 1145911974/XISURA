using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class MiMuxTransportTests
{
    [TestMethod]
    public void Verified_mux_binding_requires_a_trusted_manifest_capability()
    {
        var muxKey = new ControlKey(ControlKeys.MuxMode);
        Assert.IsFalse(VerifiedWmiBinding.TryCreate(Decision(false), muxKey, out _));
        Assert.IsTrue(VerifiedWmiBinding.TryCreate(Decision(true), muxKey, out var binding));
        Assert.AreEqual((byte)9, binding?.MethodName);
    }

    [TestMethod]
    public async Task Read_mux_uses_method_nine_and_decodes_vendor_modes()
    {
        var response = new byte[32];
        response[4] = 1;
        var read = new RecordingReadTransport(response);
        var transport = new MiMuxTransport(new MiCommonInterfaceClient(read), Binding());

        var mode = await transport.ReadMuxAsync(CancellationToken.None);

        Assert.AreEqual(MuxMode.Discrete, mode);
        Assert.AreEqual(250, read.Binding?.ReadType);
        Assert.AreEqual((byte)9, read.Binding?.MethodName);
    }

    [TestMethod]
    public async Task Write_mux_uses_vendor_write_type_method_and_mode_byte()
    {
        var write = new RecordingWriteTransport();
        var transport = new MiMuxTransport(new MiCommonInterfaceClient(writeTransport: write), Binding());

        await transport.WriteMuxAsync(MuxMode.Discrete, CancellationToken.None);

        Assert.AreEqual(251, write.Binding?.WriteType);
        Assert.AreEqual((byte)9, write.Binding?.MethodName);
        Assert.AreEqual((byte)251, write.Request![1]);
        Assert.AreEqual((byte)9, write.Request[3]);
        Assert.AreEqual((byte)1, write.Request[4]);
    }

    [TestMethod]
    public async Task Read_mux_rejects_unknown_vendor_mode_instead_of_guessing()
    {
        var response = new byte[32];
        response[4] = byte.MaxValue;
        var transport = new MiMuxTransport(new MiCommonInterfaceClient(new RecordingReadTransport(response)), Binding());

        var rejected = false;
        try
        {
            _ = await transport.ReadMuxAsync(CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Assert.IsTrue(rejected, "未知 MUX 读回值必须失败关闭。");
    }

    private static VerifiedWmiBinding Binding() => new(
        "root\\WMI", "MICommonInterface", "ACPI\\PNP0C14\\MIFS_0", 250, 251, 9, "MRID6-23-P-V39");

    private static CompatibilityDecision Decision(bool includeMuxCapability)
    {
        CapabilityBinding[] bindings = includeMuxCapability
            ? [new CapabilityBinding { Key = "muxMode", State = CapabilityState.Available }]
            : [];
        return new CompatibilityDecision(
            CompatibilityMode.Writable,
            new CapabilitySnapshot(bindings.Select(item => new CapabilityDescriptor(item.Key, item.State, null)).ToArray()),
            new CompatibilityManifest
            {
                ManifestId = "MRID6-23-P-V39",
                WmiProvider = new WmiProviderEvidence
                {
                    Namespace = "root\\WMI",
                    Class = "MICommonInterface",
                    InstanceName = "ACPI\\PNP0C14\\MIFS_0",
                    ReadType = 250,
                    WriteType = 251
                },
                Capabilities = bindings
            },
            []);
    }

    private sealed class RecordingReadTransport(byte[] response) : IMiReadTransport
    {
        public MiReadBinding? Binding { get; private set; }

        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken)
        {
            Binding = binding;
            return Task.FromResult<byte[]?>(response);
        }
    }

    private sealed class RecordingWriteTransport : IMiWriteTransport
    {
        public VerifiedWmiBinding? Binding { get; private set; }
        public byte[]? Request { get; private set; }

        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            Binding = binding;
            Request = request.ToArray();
            return Task.FromResult<byte[]?>(new byte[32]);
        }
    }
}
