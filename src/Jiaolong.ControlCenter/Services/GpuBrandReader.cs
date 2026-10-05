using System.Runtime.InteropServices;
using System.Text;

namespace Jiaolong_ControlCenter.Services;

internal static class GpuBrandReader
{
    internal readonly record struct Brands(string Discrete, string Integrated);

    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    internal static Brands Read()
    {
        var devices = new List<(string Vendor, string Name)>();
        var displayClass = DisplayClass;
        IntPtr set = SetupDiGetClassDevsW(ref displayClass, null, IntPtr.Zero, 0x2); // DIGCF_PRESENT
        if (set == new IntPtr(-1)) return default;
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info)) break;
                var id = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out _) ||
                    !id.ToString().StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                string vendor = id.ToString() switch
                {
                    var value when value.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) => "NVIDIA",
                    var value when value.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) => "AMD",
                    var value when value.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) => "Intel",
                    _ => ""
                };
                if (vendor.Length == 0) continue;
                var name = ReadName(set, ref info);
                devices.Add((vendor, name));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        string integrated = devices.FirstOrDefault(device => IsIntegrated(device.Vendor, device.Name)).Vendor ?? "";
        string discrete = devices.FirstOrDefault(device => device.Vendor == "NVIDIA" ||
            device.Vendor.Length > 0 && !IsIntegrated(device.Vendor, device.Name)).Vendor ?? "";
        return new Brands(discrete, integrated);
    }

    private static bool IsIntegrated(string vendor, string name) => vendor switch
    {
        "AMD" => name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
                 System.Text.RegularExpressions.Regex.IsMatch(name, @"Radeon\s+\d{3}M\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        "Intel" => name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string ReadName(IntPtr set, ref DeviceInfoData info)
    {
        foreach (uint property in new uint[] { 12, 0 }) // SPDRP_FRIENDLYNAME, SPDRP_DEVICEDESC
        {
            var bytes = new byte[1024];
            if (SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, bytes, (uint)bytes.Length, out _))
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        return "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfoData info);

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfoData info,
        StringBuilder id, int capacity, out int required);

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfoData info,
        uint property, out uint type, byte[] buffer, uint capacity, out uint required);

    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
