namespace Jiaolong.Hardware.Mechrevo.Wmi;

public sealed class MiFanTelemetryReader(MiCommonInterfaceClient client)
{
    private const byte FanSpeedMethod = 13;

    public async Task<(double? CpuRpm, double? GpuRpm)> ReadAsync(
        MiReadBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var result = await client.ReadAsync(binding with { MethodName = FanSpeedMethod }, cancellationToken);
        return result.Payload is { Length: > 0 }
            ? WmiResponseParser.ReadFanRpmPair(result.Payload)
            : (null, null);
    }
}
