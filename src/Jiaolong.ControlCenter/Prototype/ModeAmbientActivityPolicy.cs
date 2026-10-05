namespace Jiaolong_ControlCenter.Prototype;

public static class ModeAmbientActivityPolicy
{
    public static bool ShouldPause(bool deactivated, bool minimized, bool energySaver) =>
        deactivated || minimized || energySaver;

    public static bool DecorationsVisible(bool highContrast) => !highContrast;
}
