using System.Runtime.InteropServices;
using System.Text;

namespace Jiaolong_ControlCenter.Services;

internal static class WindowsPowerSchemeReader
{
    internal static Guid? ReadActiveId() => ReadActiveScheme();

    internal static IReadOnlyList<(Guid Id, string Name)> ReadSchemes()
    {
        var plans = new List<(Guid Id, string Name)>();
        for (uint index = 0; ; index++)
        {
            uint size = (uint)Marshal.SizeOf<Guid>();
            var buffer = new byte[size];
            uint result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
            if (result == ErrorNoMoreItems)
                break;
            if (result != 0 || size < Marshal.SizeOf<Guid>())
                break;

            var id = new Guid(buffer);
            if (ReadFriendlyName(id) is { Length: > 0 } name)
                plans.Add((id, name));
        }

        return plans;
    }

    private static Guid? ReadActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        var result = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if ((result != 0 && result != ErrorMoreData) || size is 0 or > 4096)
            return null;

        var buffer = new byte[size];
        return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? Encoding.Unicode.GetString(buffer, 0, checked((int)size)).TrimEnd('\0')
            : null;
    }

    private const uint AccessScheme = 16;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subgroupGuid,
        uint accessFlags,
        uint index,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subgroupGuid,
        IntPtr powerSettingGuid,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
