using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Jiaolong_ControlCenter.Services;

public enum TrayCommand
{
    Open,
    ShowQuickConsole,
    Exit,
    ReleaseFanControl
}

public sealed class TrayIconService : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint NotifyIconVersion = 4;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint LoadDefaultSize = 0x00000040;
    private const uint WmApp = 0x8000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint WmDestroy = 0x0002;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int HwndMessage = -3;
    private const int GwlWndProc = -4;
    private static readonly object CallbackWindowGate = new();
    private static readonly Dictionary<nint, TrayIconService> CallbackWindows = new();
    private static readonly WindowProcedure TrayWindowProcedureDelegate = TrayWindowProcedure;
    private readonly Action<TrayCommand> commandHandler;
    private readonly Action<Action> dispatchOnOwner;
    private NOTIFYICONDATA? data;
    private nint ownerWindowHandle;
    private nint callbackWindowHandle;
    private nint loadedIcon;
    private bool contextMenuShowing;
    private long lastContextMenuTick;

    public event Action<PointInt32>? ContextMenuRequested;
    public event Action<PointInt32>? QuickConsoleRequested;

    public TrayIconService(Action<TrayCommand> commandHandler, Action<Action>? dispatchOnOwner = null)
    {
        this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        this.dispatchOnOwner = dispatchOnOwner ?? (action => action());
    }

    public uint CallbackMessage => WmApp;

    public bool Show(nint windowHandle, string? iconPath = null)
    {
        if (windowHandle == 0) return false;
        if (data is not null) return true;
        ownerWindowHandle = windowHandle;
        callbackWindowHandle = CreateCallbackWindow();
        if (callbackWindowHandle == 0)
        {
            ownerWindowHandle = 0;
            return false;
        }

        loadedIcon = LoadApplicationIcon(iconPath);
        data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = callbackWindowHandle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmApp,
            hIcon = loadedIcon,
            szTip = "蛟龙控制中心"
        };

        if (!Shell_NotifyIcon(NimAdd, data))
        {
            data = null;
            DisposeIcon();
            DestroyCallbackWindow();
            ownerWindowHandle = 0;
            return false;
        }

        data.uTimeoutOrVersion = NotifyIconVersion;
        Shell_NotifyIcon(NimSetVersion, data);
        return true;
    }

    public bool HandleWindowMessage(uint message, nint lParam, nint? wParam = null)
    {
        if (message != WmApp) return false;
        HandleCallback(lParam, wParam);
        return true;
    }

    public bool UpdateIcon(string? iconPath)
    {
        if (data is null) return false;

        var nextIcon = LoadApplicationIcon(iconPath);
        if (nextIcon == 0) return false;

        var previousIcon = loadedIcon;
        var previousFlags = data.uFlags;
        data.hIcon = nextIcon;
        data.uFlags = NifIcon;
        if (!Shell_NotifyIcon(NimModify, data))
        {
            data.hIcon = previousIcon;
            data.uFlags = previousFlags;
            DestroyIcon(nextIcon);
            return false;
        }

        loadedIcon = nextIcon;
        data.uFlags = previousFlags;
        if (previousIcon != 0)
            DestroyIcon(previousIcon);
        return true;
    }

    public void HandleCallback(nint lParam, nint? wParam = null)
    {
        if (data is null) return;

        var raw = unchecked((uint)lParam.ToInt64());
        var callback = raw & 0xFFFF;
        switch (callback)
        {
            case WmLButtonUp:
            case NinSelect:
            case NinKeySelect:
                // Version 4 supplies the gesture's physical anchor, including keyboard selection.
                PointInt32 anchor;
                if (wParam is { } packed)
                    anchor = new PointInt32(unchecked((short)packed.ToInt64()), unchecked((short)(packed.ToInt64() >> 16)));
                else if (GetCursorPos(out var cursor))
                    anchor = new PointInt32(cursor.X, cursor.Y);
                else { Dispatch(TrayCommand.ShowQuickConsole); break; }
                dispatchOnOwner(() =>
                {
                    if (QuickConsoleRequested is { } requested) requested(anchor);
                    else commandHandler(TrayCommand.ShowQuickConsole);
                });
                break;
            case WmRButtonUp:
            case WmContextMenu:
                HandleContextMenuCallback();
                break;
        }
    }

    private void HandleContextMenuCallback()
    {
        if (contextMenuShowing) return;

        var now = Environment.TickCount64;
        if (now - lastContextMenuTick < 300) return;
        lastContextMenuTick = now;
        if (!GetCursorPos(out var cursor))
            return;

        contextMenuShowing = true;
        dispatchOnOwner(() =>
        {
            try { ContextMenuRequested?.Invoke(new PointInt32(cursor.X, cursor.Y)); }
            finally { contextMenuShowing = false; }
        });
    }

    public void Dispose()
    {
        contextMenuShowing = false;
        if (data is not null)
            Shell_NotifyIcon(NimDelete, data);
        data = null;
        DestroyCallbackWindow();
        ownerWindowHandle = 0;
        DisposeIcon();
        GC.SuppressFinalize(this);
    }

    public void Dispatch(TrayCommand command)
    {
        if (command is not (TrayCommand.Open or TrayCommand.ShowQuickConsole or TrayCommand.Exit or TrayCommand.ReleaseFanControl))
            return;

        dispatchOnOwner(() => commandHandler(command));
    }

    private nint CreateCallbackWindow()
    {
        var hwnd = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            "Static",
            "蛟龙控制中心托盘回调",
            0,
            0,
            0,
            0,
            0,
            new nint(HwndMessage),
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (hwnd == 0) return 0;

        lock (CallbackWindowGate)
            CallbackWindows[hwnd] = this;
        var previous = SetWindowLongPtr(
            hwnd,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(TrayWindowProcedureDelegate));
        if (previous == 0)
        {
            lock (CallbackWindowGate)
                CallbackWindows.Remove(hwnd);
            DestroyWindow(hwnd);
            return 0;
        }
        return hwnd;
    }

    private void DestroyCallbackWindow()
    {
        var hwnd = callbackWindowHandle;
        if (hwnd == 0) return;

        lock (CallbackWindowGate)
            CallbackWindows.Remove(hwnd);
        DestroyWindow(hwnd);
        callbackWindowHandle = 0;
    }

    private static nint TrayWindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        TrayIconService? service;
        lock (CallbackWindowGate)
            CallbackWindows.TryGetValue(hwnd, out service);

        if (service is not null && service.HandleWindowMessage(message, lParam, wParam))
            return nint.Zero;
        if (message == WmDestroy)
        {
            lock (CallbackWindowGate)
                CallbackWindows.Remove(hwnd);
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static nint LoadApplicationIcon(string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            uint dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
            int size = GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi); // Notification icon slot at the taskbar's DPI.
            var icon = LoadImage(nint.Zero, iconPath, ImageIcon, size, size, LoadFromFile);
            if (icon != 0) return icon;
        }

        return LoadIcon(nint.Zero, 32512);
    }

    private void DisposeIcon()
    {
        if (loadedIcon == 0) return;
        DestroyIcon(loadedIcon);
        loadedIcon = 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint imageType, int width, int height, uint loadFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint instance, nint icon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip = string.Empty;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo = string.Empty;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle = string.Empty;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT
    {
        public readonly int X;
        public readonly int Y;
    }

    private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);
}
