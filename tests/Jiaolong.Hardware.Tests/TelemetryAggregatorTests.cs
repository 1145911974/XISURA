using Jiaolong.Hardware.Mechrevo.Telemetry;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class TelemetryAggregatorTests
{
    [TestMethod]
    public async Task Unknown_sources_are_not_coerced_to_zero_and_history_is_bounded()
    {
        var aggregator = new TelemetryAggregator(new WindowsTelemetryReader(), new NvidiaSmiReader());
        TelemetrySample? latest = null;
        for (var index = 0; index < 61; index++) latest = await aggregator.ReadAsync(CancellationToken.None);

        Assert.IsNotNull(latest);
        Assert.IsNull(latest.Snapshot.CpuTemperatureC);
        Assert.IsNull(latest.Snapshot.GpuPowerWatts);
        Assert.AreEqual(DataQuality.Unknown, latest.Quality[TelemetryFieldNames.CpuTemperature]);
        Assert.AreEqual(60, aggregator.History.Count);
    }
}
