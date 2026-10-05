using System.Runtime.InteropServices;

namespace Jiaolong.Service.Home;

internal readonly record struct SystemPowerStatus(bool? AcPowerConnected, int? BatteryPercent);

internal static class SystemPowerStatusReader
{
    private const byte Unknown = 255;
    private const byte NoBattery = 128;

    internal static SystemPowerStatus Parse(byte acLineStatus, byte batteryFlag, byte batteryLifePercent) => new(
        acLineStatus switch { 0 => false, 1 => true, _ => null },
        batteryFlag != Unknown && (batteryFlag & NoBattery) == 0 && batteryLifePercent <= 100
            ? batteryLifePercent
            : null);

    internal static SystemPowerStatus Read()
    {
        try
        {
            return GetSystemPowerStatus(out var status)
                ? Parse(status.AcLineStatus, status.BatteryFlag, status.BatteryLifePercent)
                : new(null, null);
        }
        catch (DllNotFoundException)
        {
            return new(null, null);
        }
        catch (EntryPointNotFoundException)
        {
            return new(null, null);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out NativeSystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
