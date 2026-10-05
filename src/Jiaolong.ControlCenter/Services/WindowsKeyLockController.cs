using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

public sealed class WindowsKeyLockController : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly LowLevelKeyboardProc hookCallback;
    private readonly Func<IntPtr> installHook;
    private readonly Func<bool> removeHook;
    private IntPtr hookHandle;
    private bool disposed;

    public WindowsKeyLockController(Func<IntPtr>? installHook = null, Func<bool>? removeHook = null)
    {
        hookCallback = SuppressWindowsKey;
        this.installHook = installHook ?? InstallHook;
        this.removeHook = removeHook ?? RemoveHook;
        IsEnabled = true;
    }

    public bool IsEnabled { get; private set; }

    public bool TrySetEnabled(bool enabled)
    {
        if (disposed) return false;
        if (enabled == IsEnabled) return true;

        try
        {
            if (enabled)
            {
                if (!removeHook()) return false;
                hookHandle = IntPtr.Zero;
            }
            else
            {
                hookHandle = installHook();
                if (hookHandle == IntPtr.Zero) return false;
            }

            IsEnabled = enabled;
            return true;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (hookHandle != IntPtr.Zero)
            removeHook();
        hookHandle = IntPtr.Zero;
        IsEnabled = true;
        GC.SuppressFinalize(this);
    }

    private IntPtr InstallHook() => SetWindowsHookEx(
        WhKeyboardLl,
        hookCallback,
        GetModuleHandle(null),
        0);

    private bool RemoveHook() => hookHandle == IntPtr.Zero || UnhookWindowsHookEx(hookHandle);

    private IntPtr SuppressWindowsKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt32() == WmKeyDown || message.ToInt32() == WmSysKeyDown))
        {
            var virtualKey = Marshal.ReadInt32(data);
            if (virtualKey is VkLWin or VkRWin) return new IntPtr(1);
        }
        return CallNextHookEx(hookHandle, code, message, data);
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
