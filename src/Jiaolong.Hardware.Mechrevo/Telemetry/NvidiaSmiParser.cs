using System.Globalization;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Mechrevo.Telemetry;

public sealed record NvidiaSmiReading(
    double? TemperatureC,
    int? UtilizationPercent,
    int? PowerWatts,
    DataQuality Quality)
{
    public string? PerformanceState { get; init; }
    public string? PerformanceLimitReason { get; init; }
    public double? EnforcedPowerLimitWatts { get; init; }
    public double? MaximumPowerLimitWatts { get; init; }
    public static NvidiaSmiReading Unknown { get; } = new(null, null, null, DataQuality.Unknown);
}

public static class NvidiaSmiParser
{
    public static bool TryParse(string? output, out NvidiaSmiReading value)
    {
        value = NvidiaSmiReading.Unknown;
        if (string.IsNullOrWhiteSpace(output)) return false;

        var fields = output.Trim().Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length is not (3 or 14 or 16) ||
            !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ||
            !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utilization) ||
            !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var power) ||
            temperature is < -40 or > 150 ||
            utilization is < 0 or > 100 ||
            power is < 0 or > 5000)
        {
            return false;
        }

        value = new NvidiaSmiReading(temperature, utilization, (int)Math.Round(power, MidpointRounding.AwayFromZero), DataQuality.Good)
        {
            PerformanceState = fields.Length >= 14 && IsPState(fields[3]) ? fields[3] : null,
            PerformanceLimitReason = fields.Length >= 14 ? ReadLimitReasons(fields) : null,
            EnforcedPowerLimitWatts = fields.Length == 16 ? ReadPowerLimit(fields[14]) : null,
            MaximumPowerLimitWatts = fields.Length == 16 ? ReadPowerLimit(fields[15]) : null
        };
        return true;
    }

    private static bool IsPState(string value) => value.Length is 2 or 3 && value[0] == 'P' && value[1..].All(char.IsDigit);

    private static double? ReadPowerLimit(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var watts) && watts is > 0 and <= 5000
            ? watts : null;

    private static string? ReadLimitReasons(string[] fields)
    {
        string[] descriptions = ["GPU 空闲", "应用时钟限制", "功耗限制", "硬件降频", "硬件过热", "供电限制", "温度限制", "同步提升", "板级限制", "可靠性限制"];
        var active = new List<string>();
        bool unknown = false;
        for (int index = 4; index < 14; index++)
        {
            if (fields[index] == "Active" && index != 4) active.Add(descriptions[index - 4]);
            else if (fields[index] != "Not Active" && fields[index] != "Active") unknown = true;
        }
        if (active.Count > 0) return string.Join("、", active);
        if (fields[4] == "Active") return "GPU 空闲";
        return unknown ? null : "无活动限制";
    }
}
