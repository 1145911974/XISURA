namespace Jiaolong_ControlCenter.Prototype;

public sealed class HomeQuickToggleState
{
    public const string Wifi = "WiFi";
    public const string Bluetooth = "Bluetooth";
    public const string Touchpad = "Touchpad";
    public const string SystemKey = "SystemKey";
    public const string Osd = "Osd";
    public const string FunctionKey = "FunctionKey";

    private static readonly HashSet<string> Keys =
    [
        Wifi,
        Bluetooth,
        Touchpad,
        SystemKey,
        Osd,
        FunctionKey,
    ];

    private readonly HashSet<string> enabled = [];

    public bool IsEnabled(string key)
    {
        Validate(key);
        return enabled.Contains(key);
    }

    public void Set(string key, bool value)
    {
        Validate(key);
        if (value) enabled.Add(key);
        else enabled.Remove(key);
    }

    private static void Validate(string key)
    {
        if (!Keys.Contains(key)) throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown home quick toggle.");
    }
}
