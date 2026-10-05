using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Mechrevo.Telemetry;

public sealed record WindowsTelemetryReading(
    double? CpuTemperatureC,
    int? CpuPowerWatts,
    DataQuality CpuTemperatureQuality,
    DataQuality CpuPowerQuality)
{
    public static WindowsTelemetryReading Unknown { get; } = new(null, null, DataQuality.Unknown, DataQuality.Unknown);
}

public interface IWindowsTelemetrySource
{
    Task<WindowsTelemetryReading> ReadAsync(CancellationToken cancellationToken);
}

public sealed class WindowsTelemetryReader : IWindowsTelemetrySource
{
    private readonly IWindowsTelemetrySource? source;

    public WindowsTelemetryReader(IWindowsTelemetrySource? source = null) => this.source = source;

    public async Task<WindowsTelemetryReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null) return WindowsTelemetryReading.Unknown;

        try
        {
            return await source.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return WindowsTelemetryReading.Unknown;
        }
    }
}
