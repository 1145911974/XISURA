using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

if (args.Length != 1) throw new ArgumentException("Usage: FanEcProbe <C:\\Users\\Administrator\\AppData\\Local\\Temp\\result.json>");
var output = Path.GetFullPath(args[0]);
if (!output.StartsWith(@"C:\Users\Administrator\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Result must be on the approved C: temporary path.");
try
{
    var board = Wmi("Win32_BaseBoard", "Product");
    var bios = Wmi("Win32_BIOS", "SMBIOSBIOSVersion");
    if (board != "MRID6-23" || bios != "MRID6_23_P_V39")
        throw new InvalidOperationException($"Unsupported board/BIOS: {board}/{bios}");
    using var ec = new EcPorts();
    var baseline = new
    {
        board, bios,
        initialization = ec.Read(0x1060),
        control = ec.Read(0x0B20),
        cpuTarget = ec.Read(0xC83C),
        gpuTarget = ec.Read(0xC83D),
        capturedAtUtc = DateTimeOffset.UtcNow
    };
    File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(baseline));
}
catch (Exception error)
{
    File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { error = error.ToString() }));
    Environment.ExitCode = 1;
}

static string? Wmi(string className, string property)
{
    using var search = new ManagementObjectSearcher($"SELECT {property} FROM {className}");
    using var results = search.Get();
    return results.Cast<ManagementObject>().FirstOrDefault()?[property]?.ToString();
}

internal sealed class EcPorts : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint LoadIoctl = DeviceType | (0x821u << 2);
    private const uint ExecuteIoctl = DeviceType | (0x841u << 2);
    private readonly IntPtr handle;
    private readonly Mutex gate = new(false, @"Global\Access_EC");

    public EcPorts()
    {
        var assembly = Assembly.LoadFrom(@"C:\Program Files\Jiaolong Control Center\LibreHardwareMonitorLib.dll");
        var name = assembly.GetManifestResourceNames().Single(value => value.EndsWith("PawnIo.LpcIO.bin", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("LpcIO module missing");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle.ToInt64() == -1) throw new InvalidOperationException($"PawnIO open failed: {Marshal.GetLastWin32Error()}");
        var module = bytes.ToArray();
        if (!DeviceIoControl(handle, LoadIoctl, module, (uint)module.Length, null, 0, out _, IntPtr.Zero))
            throw new InvalidOperationException($"LpcIO load failed: {Marshal.GetLastWin32Error()}");
    }

    public byte Read(ushort address)
    {
        bool locked;
        try { locked = gate.WaitOne(TimeSpan.FromMilliseconds(200)); }
        catch (AbandonedMutexException) { locked = true; }
        if (!locked) throw new TimeoutException("EC mutex unavailable");
        try
        {
            _ = ReadPort(0x4E);
            WritePort(0x4E, 0x2E);
            WritePort(0x4F, 0x11);
            WritePort(0x4E, 0x2F);
            WritePort(0x4F, (byte)(address >> 8));
            WritePort(0x4E, 0x2E);
            WritePort(0x4F, 0x10);
            WritePort(0x4E, 0x2F);
            WritePort(0x4F, (byte)address);
            WritePort(0x4E, 0x2E);
            WritePort(0x4F, 0x12);
            WritePort(0x4E, 0x2F);
            return ReadPort(0x4F);
        }
        finally { gate.ReleaseMutex(); }
    }

    private byte ReadPort(ushort port) => (byte)Execute("ioctl_pio_inb", [port], 1)[0];
    private void WritePort(ushort port, byte value) => Execute("ioctl_pio_outb", [port, value], 0);

    private ulong[] Execute(string function, ulong[] arguments, int outputs)
    {
        var input = new byte[32 + arguments.Length * sizeof(ulong)];
        Encoding.ASCII.GetBytes(function).CopyTo(input, 0);
        Buffer.BlockCopy(arguments, 0, input, 32, arguments.Length * sizeof(ulong));
        byte[]? result = outputs > 0 ? new byte[outputs * sizeof(ulong)] : null;
        var succeeded = DeviceIoControl(handle, ExecuteIoctl, input, (uint)input.Length, result, (uint)(result?.Length ?? 0), out var returned, IntPtr.Zero);
        if (!succeeded || (result is not null && returned < result.Length))
            throw new InvalidOperationException($"PawnIO {function} failed: success={succeeded}, bytes={returned}, error={Marshal.GetLastWin32Error()}");
        var values = new ulong[outputs];
        if (result is not null) Buffer.BlockCopy(result, 0, values, 0, result.Length);
        return values;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero && handle.ToInt64() != -1) CloseHandle(handle);
        gate.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
}
