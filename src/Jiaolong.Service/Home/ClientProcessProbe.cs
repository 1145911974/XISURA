using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Home;

public enum ClientProcessState { Running, Exited, Unknown }

public interface IClientProcessProbe
{
    ClientProcessState Observe(int processId, long creationFileTimeUtc);
}

public sealed class WindowsClientProcessProbe : IClientProcessProbe
{
    public ClientProcessState Observe(int processId, long creationFileTimeUtc)
    {
        if (processId <= 0 || creationFileTimeUtc <= 0) return ClientProcessState.Unknown;
        // Query identity and liveness on the same kernel object, including after PID reuse.
        using var process = OpenProcess(0x1000 | 0x00100000, false, (uint)processId);
        if (process.IsInvalid) return Marshal.GetLastWin32Error() == 87 ? ClientProcessState.Exited : ClientProcessState.Unknown;
        if (!GetProcessTimes(process, out long creation, out _, out _, out _)) return ClientProcessState.Unknown;
        if (creation != creationFileTimeUtc) return ClientProcessState.Exited;
        return WaitForSingleObject(process, 0) switch
        {
            0 => ClientProcessState.Exited,
            258 => ClientProcessState.Running,
            _ => ClientProcessState.Unknown
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
