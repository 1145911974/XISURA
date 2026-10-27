using System.Globalization;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Mechrevo.Telemetry;

public sealed record NvidiaSmiReading(
    double? TemperatureC,
    int? UtilizationPercent,
    int? PowerWatts,
    DataQuality Quality)
{
    public double? RawPowerWatts { get; init; }
    public string? AdapterName { get; init; }
    public string? AdapterUuid { get; init; }
    public string? PerformanceState { get; init; }
    public string? PerformanceLimitReason { get; init; }
    public double? EnforcedPowerLimitWatts { get; init; }
    public double? MaximumPowerLimitWatts { get; init; }
    public static NvidiaSmiReading Unknown { get; } = new(null, null, null, DataQuality.Unknown);
}

public static class NvidiaSmiParser
{
    public static bool TryParse(string? output, out NvidiaSmiReading value, string? expectedAdapterName = null)
    {
        value = NvidiaSmiReading.Unknown;
        if (string.IsNullOrWhiteSpace(output)) return false;

        var rows = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!string.IsNullOrWhiteSpace(expectedAdapterName) && rows.Count(row =>
            row.Split(',')[0].Trim() is var name && !string.IsNullOrWhiteSpace(name) &&
            (name.Contains(expectedAdapterName, StringComparison.OrdinalIgnoreCase) ||
             expectedAdapterName.Contains(name, StringComparison.OrdinalIgnoreCase))) > 1) return false;
        foreach (var row in rows)
        {
        var fields = row.Split(',', StringSplitOptions.TrimEntries);
        var hasIdentity = fields.Length is 16 or 18 &&
            !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
            double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        var offset = hasIdentity ? 2 : 0;
        if (fields.Length - offset is not (3 or 14 or 16) ||
            !double.TryParse(fields[offset], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ||
            !int.TryParse(fields[offset + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utilization) ||
            !double.TryParse(fields[offset + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var power) ||
            !double.IsFinite(temperature) || temperature is < -40 or > 150 ||
            utilization is < 0 or > 100 ||
            !double.IsFinite(power) || power < 0)
        {
            continue;
        }
        var adapterName = hasIdentity ? fields[0] : null;
        if (!string.IsNullOrWhiteSpace(expectedAdapterName) &&
            (adapterName is null || !adapterName.Contains(expectedAdapterName, StringComparison.OrdinalIgnoreCase) &&
             !expectedAdapterName.Contains(adapterName, StringComparison.OrdinalIgnoreCase))) continue;

        var limitOffset = offset + 14;
        value = new NvidiaSmiReading(temperature, utilization, RoundPower(power), DataQuality.Good)
        {
            RawPowerWatts = power,
            AdapterName = adapterName,
            AdapterUuid = hasIdentity ? fields[1] : null,
            PerformanceState = fields.Length - offset >= 14 && IsPState(fields[offset + 3]) ? fields[offset + 3] : null,
            PerformanceLimitReason = fields.Length - offset >= 14 ? ReadLimitReasons(fields, offset) : null,
            EnforcedPowerLimitWatts = fields.Length - offset == 16 ? ReadPowerLimit(fields[limitOffset]) : null,
            MaximumPowerLimitWatts = fields.Length - offset == 16 ? ReadPowerLimit(fields[limitOffset + 1]) : null
        };
        return true;
        }
        return false;
    }

    private static int? RoundPower(double power) => power <= int.MaxValue ? (int)Math.Round(power, MidpointRounding.AwayFromZero) : null;

    private static bool IsPState(string value) => value.Length is 2 or 3 && value[0] == 'P' && value[1..].All(char.IsDigit);

    private static double? ReadPowerLimit(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var watts) && watts is > 0 and <= 5000
            ? watts : null;

    private static string? ReadLimitReasons(string[] fields, int offset)
    {
        string[] descriptions = ["GPU 空闲", "应用时钟限制", "功耗限制", "硬件降频", "硬件过热", "供电限制", "温度限制", "同步提升", "板级限制", "可靠性限制"];
        var active = new List<string>();
        bool unknown = false;
        for (int index = 4; index < 14; index++)
        {
            if (fields[offset + index] == "Active" && index != 4) active.Add(descriptions[index - 4]);
            else if (fields[offset + index] != "Not Active" && fields[offset + index] != "Active") unknown = true;
        }
        if (active.Count > 0) return string.Join("、", active);
        if (fields[offset + 4] == "Active") return "GPU 空闲";
        return unknown ? null : "无活动限制";
    }
}
