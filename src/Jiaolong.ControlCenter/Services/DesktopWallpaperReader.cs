using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

internal static class DesktopWallpaperReader
{
    internal readonly record struct Wallpaper(int Left, int Top, int Right, int Bottom, string Path);

    private static readonly Guid ClassId = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");
    private static readonly Guid InterfaceId = new("B92B56A9-8B55-4E14-9A89-0199BBB6F93B");

    internal static Wallpaper[] Read()
    {
        var classId = ClassId;
        var interfaceId = InterfaceId;
        if (CoCreateInstance(ref classId, IntPtr.Zero, 23, ref interfaceId, out var instance) != 0 || instance == 0)
            return [];

        try
        {
            var count = Method<GetMonitorDevicePathCount>(instance, 6);
            var getId = Method<GetMonitorDevicePathAt>(instance, 5);
            var getRect = Method<GetMonitorRect>(instance, 7);
            var getWallpaper = Method<GetWallpaper>(instance, 4);
            if (count(instance, out var monitorCount) != 0) return [];

            List<Wallpaper> wallpapers = [];
            for (uint index = 0; index < monitorCount; index++)
            {
                if (getId(instance, index, out var idPointer) != 0) continue;
                string? id;
                try { id = Marshal.PtrToStringUni(idPointer); }
                finally { if (idPointer != 0) Marshal.FreeCoTaskMem(idPointer); }
                if (string.IsNullOrEmpty(id) || getRect(instance, id, out var rect) != 0) continue;

                if (getWallpaper(instance, id, out var pathPointer) != 0) continue;
                string? path;
                try { path = Marshal.PtrToStringUni(pathPointer); }
                finally { if (pathPointer != 0) Marshal.FreeCoTaskMem(pathPointer); }
                if (!string.IsNullOrWhiteSpace(path))
                    wallpapers.Add(new(rect.Left, rect.Top, rect.Right, rect.Bottom, path));
            }
            return [.. wallpapers];
        }
        catch (Exception exception) when (exception is COMException or MarshalDirectiveException or ArgumentException)
        {
            return [];
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context,
        ref Guid interfaceId, out IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMonitorDevicePathCount(IntPtr instance, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMonitorDevicePathAt(IntPtr instance, uint index, out IntPtr id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMonitorRect(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string id, out Rect rect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetWallpaper(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr path);
}
