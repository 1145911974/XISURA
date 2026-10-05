namespace Jiaolong.Automation.Rules;

internal static class ForegroundAppRule
{
    internal static bool IsStable(AutomationInputs inputs, DateTimeOffset now) =>
        inputs.ForegroundApp is { } app && now - app.ObservedAtUtc >= TimeSpan.FromSeconds(5);
}
