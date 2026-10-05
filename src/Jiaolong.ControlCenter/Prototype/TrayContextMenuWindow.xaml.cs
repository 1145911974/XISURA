using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class TrayContextMenuWindow : Window
{
    private readonly Action openClient;
    private readonly Action exit;
    private bool closing;

    public TrayContextMenuWindow(Action openClient, Action exit)
    {
        this.openClient = openClient;
        this.exit = exit;
        InitializeComponent();
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(hwnd, -20, new nint(GetWindowLongPtr(hwnd, -20).ToInt64() | 0x80));
        ApplyChrome();
        Activated += (_, args) =>
        {
            if (!closing && args.WindowActivationState == WindowActivationState.Deactivated) HideImmediately();
        };
        MenuSurface.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) HideImmediately();
            else if (args.Key is VirtualKey.Up or VirtualKey.Down)
            {
                if (OpenItem.FocusState != FocusState.Unfocused) ExitItem.Focus(FocusState.Keyboard);
                else OpenItem.Focus(FocusState.Keyboard);
            }
            else return;
            args.Handled = true;
        };
    }

    public void ShowNearCursor(PointInt32 cursor)
    {
        var area = DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X, area.Y));
        double scale = Math.Max(96u, GetDpiForWindow(WindowNative.GetWindowHandle(this))) / 96d;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(136 * scale), (int)Math.Ceiling(62 * scale)));
        AppWindow.Move(new PointInt32(
            Math.Clamp(cursor.X + 2, area.X, Math.Max(area.X, area.X + area.Width - AppWindow.Size.Width)),
            Math.Clamp(cursor.Y - AppWindow.Size.Height - 2, area.Y, Math.Max(area.Y, area.Y + area.Height - AppWindow.Size.Height))));
        ApplyChrome();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
        OpenItem.Focus(FocusState.Programmatic);
    }

    public void HideImmediately() => AppWindow.Hide();
    public void CloseForExit()
    {
        closing = true;
        Close();
    }
    private void ApplyChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        if ((style & 0x00C40000L) != 0)
        {
            SetWindowLongPtr(hwnd, -16, new nint(style & ~0x00C40000L));
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37);
        }
        uint border = 0xFFFFFFFE, corners = 3;
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(uint));
        DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(uint));
        uint dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(uint));
    }
    private void OnOpenClick(object sender, RoutedEventArgs args) => openClient();
    private void OnExitClick(object sender, RoutedEventArgs args) { HideImmediately(); exit(); }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
}
