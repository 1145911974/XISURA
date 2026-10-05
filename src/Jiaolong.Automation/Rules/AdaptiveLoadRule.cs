namespace Jiaolong.Automation.Rules;

internal static class AdaptiveLoadRule
{
    internal static bool IsHigh(AutomationInputs inputs, DateTimeOffset now) =>
        inputs.Telemetry.HasHighGpuLoad(now);
}
