using Jiaolong.Hardware.Mechrevo.Telemetry;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class ReadOnlyServiceTests
{
    [TestMethod]
    public async Task Service_without_verified_sources_exposes_unknown_readings_only()
    {
        var sample = await new TelemetryAggregator(new WindowsTelemetryReader(), new NvidiaSmiReader())
            .ReadAsync(CancellationToken.None);

        Assert.AreEqual(DataQuality.Unknown, sample.Quality[TelemetryFieldNames.CpuTemperature]);
        Assert.IsNull(sample.Snapshot.CpuPowerWatts);
    }
}
