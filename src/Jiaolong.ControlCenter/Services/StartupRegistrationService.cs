using Microsoft.Win32;

namespace Jiaolong_ControlCenter.Services;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void Enable(string? executablePath = null);
    void Disable();
}

public sealed class StartupRegistrationService : IStartupRegistration
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "JiaolongControlCenter";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    public void Enable(string? executablePath = null)
    {
        var path = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Executable path is unavailable.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("User startup key is unavailable.");
        key.SetValue(ValueName, Quote(path), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string Quote(string path) => $"\"{path.Replace("\"", string.Empty, StringComparison.Ordinal)}\"";
}
