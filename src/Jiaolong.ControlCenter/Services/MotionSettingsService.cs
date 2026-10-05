using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

public sealed class MotionSettingsService : INotifyPropertyChanged
{
    private bool reducedMotionEnabled;

    public MotionSettingsService() => reducedMotionEnabled = ReadReducedMotion();

    public bool IsReducedMotionEnabled => reducedMotionEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Refresh()
    {
        var next = ReadReducedMotion();
        if (next == reducedMotionEnabled) return false;
        reducedMotionEnabled = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReducedMotionEnabled)));
        return true;
    }

    private static bool ReadReducedMotion() => !SystemParametersInfo(
        SpiGetClientAreaAnimation,
        0,
        out bool enabled,
        0) || !enabled;

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        out bool result,
        uint update);
}
