namespace Jiaolong.Automation.Rules;

internal static class ThermalRule
{
    internal static bool IsEmergency(AutomationInputs inputs) =>
        inputs.Telemetry.IsThermalEmergency();
}
