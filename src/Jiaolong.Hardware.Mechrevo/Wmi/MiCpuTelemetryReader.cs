namespace Jiaolong.Hardware.Mechrevo.Wmi;

public sealed class MiCpuTelemetryReader(MiCommonInterfaceClient client)
{
    private const byte CpuTemperatureMethod = 22;

    public async Task<double?> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var response = await client.ReadAsync(
            binding with { MethodName = CpuTemperatureMethod },
            cancellationToken);

        return response.Payload is { Length: > 0 }
            ? WmiResponseParser.ReadCpuTemperature(response.Payload)
            : null;
    }
}
