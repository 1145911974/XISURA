using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

public interface IDisplayPowerController
{
    bool TryTurnOff();
}

public sealed class WindowsDisplayPowerController : IDisplayPowerController
{
    private const uint WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const int MonitorOff = 2;
    private readonly Func<bool> sendMonitorOff;

    public WindowsDisplayPowerController(Func<bool>? sendMonitorOff = null) =>
        this.sendMonitorOff = sendMonitorOff ?? SendMonitorOffMessage;

    public bool TryTurnOff()
    {
        try
        {
            return sendMonitorOff();
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return false;
        }
    }

    private static bool SendMonitorOffMessage()
    {
        // Broadcasting this system command can synchronously wait on every
        // top-level window. A private system-class window avoids taking other
        // applications (or this UI) into the display-power call path.
        var messageWindow = CreateWindowEx(
            0,
            "Static",
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (messageWindow == IntPtr.Zero) return false;

        try
        {
            SendMessage(messageWindow, WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(MonitorOff));
            return true;
        }
        finally
        {
            DestroyWindow(messageWindow);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
