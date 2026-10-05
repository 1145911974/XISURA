using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

// Explorer also reads the HWND icons; keep both sizes alive until replaced or closed.
internal sealed class RuntimeWindowIcon : IDisposable
{
    private nint smallIcon;
    private nint largeIcon;

    public void Apply(nint window, string path)
    {
        uint dpi = GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;
        nint small = LoadImage(0, path, 1, GetSystemMetricsForDpi(49, dpi), GetSystemMetricsForDpi(50, dpi), 0x10);
        nint large = LoadImage(0, path, 1, GetSystemMetricsForDpi(11, dpi), GetSystemMetricsForDpi(12, dpi), 0x10);
        if (small == 0 || large == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (small != 0) DestroyIcon(small);
            if (large != 0) DestroyIcon(large);
            throw new Win32Exception(error);
        }
        SendMessage(window, 0x80, 0, small);
        SendMessage(window, 0x80, 1, large);
        Dispose();
        smallIcon = small;
        largeIcon = large;
    }

    public void Dispose()
    {
        if (smallIcon != 0) DestroyIcon(smallIcon);
        if (largeIcon != 0) DestroyIcon(largeIcon);
        smallIcon = largeIcon = 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
