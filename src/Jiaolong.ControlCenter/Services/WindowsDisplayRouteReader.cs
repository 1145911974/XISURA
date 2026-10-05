using System.Runtime.InteropServices;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

internal static class WindowsDisplayRouteReader
{
    internal readonly record struct ActiveDisplayEndpoint(string DeviceName, bool IsInternal, string Connector, string AdapterName, string TargetName);
    private const uint QueryOnlyActivePaths = 0x00000002;
    private const uint QueryVirtualModeAware = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;

    internal static string ReadStatus()
    {
        try
        {
            if (!TryReadActivePaths(out var paths)) return "活动显示路径未读回；MUX 未读回";
            if (paths.Length == 0) return "当前无活动显示输出；MUX 未读回";

            var routes = paths.Select(path =>
            {
                var adapter = ReadAdapterName(path.Source.AdapterId);
                var display = ReadTargetName(path.Target.AdapterId, path.Target.Id);
                return $"{adapter} → {display}（{ConnectorName(path.Target.OutputTechnology)}）";
            });
            return $"活动输出：{string.Join("；", routes)}；MUX 未读回";
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or TypeLoadException)
        {
            return "活动显示路径未读回；MUX 未读回";
        }
    }

    internal static string ReadStatus(MuxMode? muxMode)
    {
        var status = ReadStatus();
        var muxStatus = muxMode switch
        {
            MuxMode.Hybrid => "MUX 设置读回：混合输出",
            MuxMode.Discrete => "MUX 设置读回：独显直连",
            _ => "MUX 未读回"
        };
        return status.Replace("MUX 未读回", muxStatus, StringComparison.Ordinal);
    }

    internal static ActiveDisplayEndpoint[] ReadActiveEndpoints()
    {
        try
        {
            if (!TryReadActivePaths(out var paths)) return [];
            return paths.Select(path => new ActiveDisplayEndpoint(
                    ReadSourceDeviceName(path.Source.AdapterId, path.Source.Id),
                    path.Target.OutputTechnology is 6 or 11 or 13 or 0x80000000,
                    ConnectorName(path.Target.OutputTechnology),
                    ReadAdapterName(path.Source.AdapterId),
                    ReadTargetName(path.Target.AdapterId, path.Target.Id)))
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.DeviceName))
                .ToArray();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or TypeLoadException)
        {
            return [];
        }
    }

    private static string ReadSourceDeviceName(Luid adapterId, uint sourceId)
    {
        var request = new SourceDeviceName
        {
            Header = CreateHeader(1, (uint)Marshal.SizeOf<SourceDeviceName>(), adapterId, sourceId),
            DeviceName = string.Empty
        };
        return DisplayConfigGetSourceName(ref request) == 0 ? request.DeviceName.TrimEnd('\0') : string.Empty;
    }

    private static bool TryReadActivePaths(out DisplayPathInfo[] paths)
    {
        const uint flags = QueryOnlyActivePaths | QueryVirtualModeAware;
        paths = [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0) return false;
            var pathBuffer = new DisplayPathInfo[checked((int)Math.Max(1u, pathCount))];
            var modeBuffer = new DisplayModeInfo[checked((int)Math.Max(1u, modeCount))];
            var result = QueryDisplayConfig(flags, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
            if (result == ErrorInsufficientBuffer) continue;
            if (result != 0 || pathCount > pathBuffer.Length) return false;
            paths = pathBuffer.Take((int)pathCount).ToArray();
            return true;
        }
        return false;
    }

    private static string ReadAdapterName(Luid adapterId)
    {
        var request = new AdapterDeviceName
        {
            Header = CreateHeader(4, (uint)Marshal.SizeOf<AdapterDeviceName>(), adapterId, 0),
            AdapterPath = string.Empty
        };
        if (DisplayConfigGetAdapterName(ref request) != 0) return "图形适配器";
        if (request.AdapterPath.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA dGPU";
        if (request.AdapterPath.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return "AMD GPU";
        if (request.AdapterPath.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel GPU";
        return "图形适配器";
    }

    private static string ReadTargetName(Luid adapterId, uint targetId)
    {
        var request = new TargetDeviceName
        {
            Header = CreateHeader(2, (uint)Marshal.SizeOf<TargetDeviceName>(), adapterId, targetId),
            FriendlyName = string.Empty,
            DevicePath = string.Empty
        };
        return DisplayConfigGetTargetName(ref request) == 0 && !string.IsNullOrWhiteSpace(request.FriendlyName)
            ? request.FriendlyName.Trim()
            : "显示器";
    }

    private static DisplayDeviceInfoHeader CreateHeader(int type, uint size, Luid adapterId, uint id) => new()
    {
        Type = type,
        Size = size,
        AdapterId = adapterId,
        Id = id
    };

    private static string ConnectorName(uint technology) => technology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 => "DisplayPort",
        11 => "eDP 内屏",
        12 => "UDI 外接",
        13 => "UDI 内置",
        15 => "Miracast",
        16 => "有线间接显示",
        17 => "虚拟显示",
        0x80000000 => "内接显示",
        _ => "接口未知"
    };

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DisplayPathInfo[] paths,
        ref uint modeCount,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] DisplayModeInfo[] modes,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigGetAdapterName(ref AdapterDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigGetTargetName(ref TargetDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigGetSourceName(ref SourceDeviceName request);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPathInfo
    {
        public DisplayPathSourceInfo Source;
        public DisplayPathTargetInfo Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayModeInfo
    {
        public ulong Slot0;
        public ulong Slot1;
        public ulong Slot2;
        public ulong Slot3;
        public ulong Slot4;
        public ulong Slot5;
        public ulong Slot6;
        public ulong Slot7;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDeviceName
    {
        public DisplayDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string AdapterPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DisplayDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DisplayDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufacturerId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
}
