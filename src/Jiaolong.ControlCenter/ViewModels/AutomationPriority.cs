namespace Jiaolong_ControlCenter.ViewModels;

public static class AutomationPriority
{
    public static string[] DisplayOrder { get; } =
    [
        "thermal",
        "compatibilityOrConflict",
        "dc",
        "sessionLock",
        "manualOverride",
        "foregroundApp",
        "adaptiveLoad",
        "baseline"
    ];
}
