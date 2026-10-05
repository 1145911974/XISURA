using System;
using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

internal static class TaskbarGroupIcon
{
    // Relaunch metadata pins Explorer to a cached group icon instead of the live HWND icon.
    public static void Apply(nint window)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        try
        {
            Set(store, 2, null);
            Set(store, 4, null);
            Set(store, 3, null);
            Set(store, 5, "XISURA.ControlCenter");
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    private static void Set(IPropertyStore store, uint id, string? value)
    {
        var key = new PropertyKey { FormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
        var variant = value is null ? new PropVariant() :
            new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant)); }
        finally { Marshal.FreeCoTaskMem(variant.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(nint window, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
