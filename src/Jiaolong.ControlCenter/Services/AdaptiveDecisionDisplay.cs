using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public sealed record AdaptiveDecisionDisplay(string CpuUsage, string GpuUsage, string Power)
{
    public static AdaptiveDecisionDisplay From(HardwareSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || snapshot.CapturedAtUtc > now.AddSeconds(2) || now - snapshot.CapturedAtUtc > TimeSpan.FromSeconds(10))
            return new("--", "--", "供电待确认");

        return new(
            FormatLoad(snapshot.CpuUsagePercent),
            FormatLoad(snapshot.GpuUsagePercent),
            snapshot.AcPowerConnected switch { true => "AC 供电", false => "DC 供电", _ => "供电待确认" });
    }

    private static string FormatLoad(double? value) =>
        value is >= 0 and <= 100 && double.IsFinite(value.Value) ? $"{value:0}%" : "--";
}
