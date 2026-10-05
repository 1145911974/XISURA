using Jiaolong.Hardware.Mechrevo.Telemetry;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class NvidiaSmiReaderTests
{
    [TestMethod]
    public void Valid_nvidia_output_is_culture_invariant()
    {
        Assert.IsTrue(NvidiaSmiParser.TryParse("42.0, 78, 2200", out var value));
        Assert.AreEqual(42.0, value.TemperatureC);
        Assert.AreEqual(78, value.UtilizationPercent);
        Assert.AreEqual(2200, value.PowerWatts);
    }

    [TestMethod]
    public void Driver_event_reasons_and_fractional_power_are_parsed_without_guessing()
    {
        const string idle = "53, 8, 17.96, P0, Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active";
        Assert.IsTrue(NvidiaSmiParser.TryParse(idle, out var reading));
        Assert.AreEqual(18, reading.PowerWatts);
        Assert.AreEqual("P0", reading.PerformanceState);
        Assert.AreEqual("GPU 空闲", reading.PerformanceLimitReason);

        const string powerLimited = "53, 80, 108.17, P2, Not Active, Not Active, Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active";
        Assert.IsTrue(NvidiaSmiParser.TryParse(powerLimited, out reading));
        Assert.AreEqual("功耗限制", reading.PerformanceLimitReason);
        Assert.AreEqual(108, reading.PowerWatts);
    }

    [TestMethod]
    public void Driver_power_ceiling_is_read_without_treating_it_as_a_writable_request()
    {
        const string output = "53, 8, 17.96, P0, Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, 128.91, 140.00";
        Assert.IsTrue(NvidiaSmiParser.TryParse(output, out var reading));
        Assert.AreEqual(128.91, reading.EnforcedPowerLimitWatts);
        Assert.AreEqual(140.00, reading.MaximumPowerLimitWatts);
        Assert.AreEqual("GPU 空闲", reading.PerformanceLimitReason);
    }

    [TestMethod]
    public async Task Unsupported_extended_fields_fall_back_to_basic_telemetry()
    {
        var queries = new List<string>();
        var reader = new NvidiaSmiReader(outputProvider: (query, _) =>
        {
            queries.Add(query);
            return Task.FromResult<string?>(queries.Count == 1
                ? null
                : "53, 8, 17.96, P0, Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active, Not Active");
        });

        var reading = await reader.ReadAsync(CancellationToken.None);

        Assert.AreEqual(2, queries.Count);
        Assert.AreEqual(18, reading.PowerWatts);
        Assert.IsNull(reading.EnforcedPowerLimitWatts);
    }

    [TestMethod]
    public void Malformed_nvidia_output_is_unknown()
    {
        Assert.IsFalse(NvidiaSmiParser.TryParse("N/A, [malformed]", out var value));
        Assert.AreEqual(DataQuality.Unknown, value.Quality);
    }

    [TestMethod]
    public void Out_of_range_nvidia_output_is_rejected()
    {
        Assert.IsFalse(NvidiaSmiParser.TryParse("999999999999", out var value));
        Assert.AreEqual(DataQuality.Unknown, value.Quality);
    }
}
