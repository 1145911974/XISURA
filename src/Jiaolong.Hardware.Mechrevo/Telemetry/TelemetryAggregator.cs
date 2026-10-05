using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Mechrevo.Telemetry;

public static class TelemetryFieldNames
{
    public const string CpuTemperature = "cpuTemperatureC";
    public const string GpuTemperature = "gpuTemperatureC";
    public const string CpuPower = "cpuPowerWatts";
    public const string GpuPower = "gpuPowerWatts";
}

public sealed record TelemetrySample(
    HardwareSnapshot Snapshot,
    IReadOnlyDictionary<string, DataQuality> Quality);

public sealed class TelemetryAggregator
{
    private const int HistoryCapacity = 60;
    private readonly IWindowsTelemetrySource windows;
    private readonly NvidiaSmiReader nvidia;
    private readonly Queue<TelemetrySample> history = new();
    private readonly object gate = new();

    public TelemetryAggregator(IWindowsTelemetrySource windows, NvidiaSmiReader nvidia)
    {
        this.windows = windows;
        this.nvidia = nvidia;
    }

    public IReadOnlyList<TelemetrySample> History
    {
        get { lock (gate) return history.ToArray(); }
    }

    public async Task<TelemetrySample> ReadAsync(CancellationToken cancellationToken)
    {
        var windowsTask = windows.ReadAsync(cancellationToken);
        var nvidiaTask = nvidia.ReadAsync(cancellationToken);
        await Task.WhenAll(windowsTask, nvidiaTask);
        var windowsReading = await windowsTask;
        var nvidiaReading = await nvidiaTask;
        var now = DateTimeOffset.UtcNow;
        var sample = new TelemetrySample(
            new HardwareSnapshot(
                now,
                windowsReading.CpuTemperatureC is null && nvidiaReading.TemperatureC is null ? "unknown" : "normal",
                windowsReading.CpuTemperatureC,
                nvidiaReading.TemperatureC,
                windowsReading.CpuPowerWatts,
                nvidiaReading.PowerWatts),
            new Dictionary<string, DataQuality>(StringComparer.Ordinal)
            {
                [TelemetryFieldNames.CpuTemperature] = windowsReading.CpuTemperatureQuality,
                [TelemetryFieldNames.GpuTemperature] = nvidiaReading.Quality,
                [TelemetryFieldNames.CpuPower] = windowsReading.CpuPowerQuality,
                [TelemetryFieldNames.GpuPower] = nvidiaReading.Quality
            });

        lock (gate)
        {
            history.Enqueue(sample);
            while (history.Count > HistoryCapacity) history.Dequeue();
        }

        return sample;
    }
}
