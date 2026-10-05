namespace Jiaolong.Automation.Rules;

internal static class AcDcRule
{
    internal static bool IsDc(AutomationInputs inputs) => !inputs.IsAcConnected;
}
