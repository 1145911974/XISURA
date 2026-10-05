namespace Jiaolong.Automation.Rules;

internal static class SessionRule
{
    internal static bool IsLocked(AutomationInputs inputs) => inputs.IsSessionLocked;
}
