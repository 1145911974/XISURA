using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class WmiResponseParserTests
{
    [TestMethod]
    public void Short_wmi_buffer_returns_unknown_without_indexing_past_end()
    {
        var value = WmiResponseParser.ReadByte(new byte[4], responseIndex: 4);

        Assert.AreEqual(DataQuality.Unknown, value.Quality);
        Assert.IsNull(value.Value);
    }

    [TestMethod]
    public void Fan_response_decodes_cpu_and_gpu_rpm_from_little_endian_bytes()
    {
        var response = new byte[8];
        response[4] = 0x95;
        response[5] = 0x0D;
        response[6] = 0xCE;
        response[7] = 0x0D;

        var value = WmiResponseParser.ReadFanRpmPair(response);

        Assert.AreEqual(3477d, value.CpuRpm);
        Assert.AreEqual(3534d, value.GpuRpm);
    }

    [TestMethod]
    public void Short_fan_response_returns_unknown_values()
    {
        var value = WmiResponseParser.ReadFanRpmPair(new byte[4]);

        Assert.IsNull(value.CpuRpm);
        Assert.IsNull(value.GpuRpm);
    }

    [TestMethod]
    public void Cpu_temperature_response_decodes_degree_value_from_data_byte()
    {
        var response = new byte[8];
        response[4] = 72;

        Assert.AreEqual(72d, WmiResponseParser.ReadCpuTemperature(response));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(151)]
    public void Cpu_temperature_response_rejects_invalid_values(int rawValue)
    {
        var response = new byte[8];
        response[4] = (byte)rawValue;

        Assert.IsNull(WmiResponseParser.ReadCpuTemperature(response));
    }

    [TestMethod]
    public async Task Read_binding_passes_method_name_to_read_transport()
    {
        var transport = new CapturingReadTransport();
        var binding = new MiReadBinding("root\\WMI", "MICommonInterface", "ACPI\\PNP0C14\\MIFS_0", 250, 13);

        await new MiCommonInterfaceClient(transport).ReadAsync(binding, CancellationToken.None);

        Assert.AreEqual(13, transport.Binding?.MethodName ?? 0);
        Assert.AreEqual(250, transport.Binding?.ReadType ?? 0);
    }

    [TestMethod]
    public async Task Mi_fan_reader_returns_decoded_pair_from_read_response()
    {
        var response = new byte[8];
        response[4] = 0x95;
        response[5] = 0x0D;
        response[6] = 0xCE;
        response[7] = 0x0D;

        var value = await new MiFanTelemetryReader(
                new MiCommonInterfaceClient(new FixedReadTransport(response)))
            .ReadAsync(new MiReadBinding("root\\WMI", "MICommonInterface", "ACPI\\PNP0C14\\MIFS_0", 250), CancellationToken.None);

        Assert.AreEqual(3477d, value.CpuRpm);
        Assert.AreEqual(3534d, value.GpuRpm);
    }

    [TestMethod]
    public async Task Mi_fan_reader_returns_unknown_pair_when_read_response_is_empty()
    {
        var value = await new MiFanTelemetryReader(
                new MiCommonInterfaceClient(new FixedReadTransport(null)))
            .ReadAsync(new MiReadBinding("root\\WMI", "MICommonInterface", "ACPI\\PNP0C14\\MIFS_0", 250), CancellationToken.None);

        Assert.IsNull(value.CpuRpm);
        Assert.IsNull(value.GpuRpm);
    }

    [TestMethod]
    public async Task Mi_cpu_reader_requests_method_22_and_returns_temperature()
    {
        var transport = new CapturingReadTransport([0, 0, 0, 0, 72]);

        var value = await new MiCpuTelemetryReader(
                new MiCommonInterfaceClient(transport))
            .ReadAsync(new MiReadBinding("root\\WMI", "MICommonInterface", "ACPI\\PNP0C14\\MIFS_0", 250), CancellationToken.None);

        Assert.AreEqual(22, transport.Binding?.MethodName ?? 0);
        Assert.AreEqual(72d, value);
    }

    private sealed class CapturingReadTransport(byte[]? response = null) : IMiReadTransport
    {
        public MiReadBinding? Binding { get; private set; }

        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken)
        {
            Binding = binding;
            return Task.FromResult<byte[]?>(response ?? [0]);
        }
    }

    private sealed class FixedReadTransport(byte[]? response) : IMiReadTransport
    {
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(response);
    }
}
