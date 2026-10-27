using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Home;

internal interface IFanEcPortAccess : IDisposable
{
    IDisposable AcquireBus();
    void Open();
    byte ReadByte(ushort port);
    void WriteByte(ushort port, byte value);
}

internal sealed class PawnIoLpcEcPorts : IFanEcPortAccess
{
    private const string ModuleName = "LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin";
    private const string ModuleHash = "3DCF8B2BC80FF642D97C4608511A818642B5BF315FF53DF3DF393D043E71D101";
    private const string DriverHash = "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940";
    private readonly object gate = new();
    private readonly Func<bool> accessAllowed;
    private readonly IPawnIoDeviceApi api;
    private readonly Func<byte[]?> readModule;
    private readonly Func<DriverIdentity?> readDriver;
    private readonly string busMutexName;
    private readonly int busTimeoutMilliseconds;
    private SafeFileHandle? handle;
    private int leaseThreadId;
    private bool disposed;

    internal sealed record DriverIdentity(string State, string Path, string Sha256, string Authenticode);
    internal delegate bool DeviceIoControlCall(SafeFileHandle device, uint code, byte[] input, byte[]? output, out uint returned);

    public PawnIoLpcEcPorts(Func<bool> accessAllowed)
        : this(accessAllowed, new NativeDeviceApi(), ReadEmbeddedModule, ReadRunningDriver,
            @"Global\Access_ISABUS.HTP.Method", 200) { }

    internal PawnIoLpcEcPorts(Func<bool> accessAllowed, IPawnIoDeviceApi api, Func<byte[]?> readModule,
        Func<DriverIdentity?> readDriver, string busMutexName, int busTimeoutMilliseconds = 100)
    {
        this.accessAllowed = accessAllowed;
        this.api = api;
        this.readModule = readModule;
        this.readDriver = readDriver;
        this.busMutexName = busMutexName;
        this.busTimeoutMilliseconds = busTimeoutMilliseconds;
    }

    public IDisposable AcquireBus()
    {
        Monitor.Enter(gate);
        Mutex? mutex = null;
        bool acquired = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureAccess();
            if (leaseThreadId != 0) throw new InvalidOperationException("pawnIoLpcBusAlreadyHeld");
            mutex = new Mutex(false, busMutexName);
            try { acquired = mutex.WaitOne(busTimeoutMilliseconds); }
            catch (AbandonedMutexException error)
            {
                acquired = true;
                throw new IOException("pawnIoLpcBusAbandoned", error);
            }
            if (!acquired) throw new IOException("pawnIoLpcBusTimeout");
            EnsureAccess();
            leaseThreadId = Environment.CurrentManagedThreadId;
            return new BusLease(this, mutex);
        }
        catch
        {
            try { if (acquired) mutex!.ReleaseMutex(); }
            finally { mutex?.Dispose(); Monitor.Exit(gate); }
            throw;
        }
    }

    public void Open()
    {
        EnsureLease();
        EnsureAccess();
        if (handle is not null) return;
        if (!IsTrustedDriver(readDriver())) throw new IOException("pawnIoLpcDriverUntrusted");
        EnsureAccess();
        var module = readModule();
        if (!IsPinnedModule(module)) throw new IOException("pawnIoLpcModuleUntrusted");
        EnsureAccess();
        var candidate = api.Open();
        try
        {
            EnsureAccess();
            if (candidate.IsInvalid || candidate.IsClosed) throw new IOException("pawnIoLpcOpenFailed");
            if (!api.LoadModule(candidate, module!)) throw new IOException("pawnIoLpcModuleLoadFailed");
            EnsureAccess();
            if (!Execute(candidate, "ioctl_select_slot", [1], null)) throw new IOException("pawnIoLpcSlotFailed");
            EnsureAccess();
            handle = candidate;
        }
        catch { candidate.Dispose(); throw; }
    }

    public byte ReadByte(ushort port)
    {
        EnsureLease();
        var result = new ulong[1];
        if (!Execute(RequireHandle(), "ioctl_pio_inb", [port], result) || result[0] > byte.MaxValue)
            throw new IOException("pawnIoLpcReadFailed");
        return (byte)result[0];
    }

    public void WriteByte(ushort port, byte value)
    {
        EnsureLease();
        if (!Execute(RequireHandle(), "ioctl_pio_outb", [port, value], null))
            throw new IOException("pawnIoLpcWriteFailed");
    }

    private bool Execute(SafeFileHandle device, string functionName, ulong[] input, ulong[]? output)
    {
        bool valid = functionName switch
        {
            "ioctl_select_slot" => input.Length == 1 && input[0] == 1 && output is null,
            "ioctl_pio_inb" => input.Length == 1 && output is { Length: 1 },
            "ioctl_pio_outb" => input.Length == 2 && input[1] <= byte.MaxValue && output is null,
            _ => false
        };
        if (!valid) throw new ArgumentException("pawnIoLpcCommandRejected", nameof(functionName));
        if (functionName != "ioctl_select_slot" && input[0] is not (0x4E or 0x4F))
            throw new ArgumentOutOfRangeException(nameof(input), "pawnIoLpcPortRejected");
        return api.Execute(device, functionName, input, output);
    }

    private SafeFileHandle RequireHandle() => handle is { IsClosed: false, IsInvalid: false } device
        ? device : throw new IOException("pawnIoLpcNotOpen");

    private void EnsureLease()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (leaseThreadId != Environment.CurrentManagedThreadId || !Monitor.IsEntered(gate))
            throw new InvalidOperationException("pawnIoLpcBusRequired");
    }

    private void EnsureAccess()
    {
        if (!accessAllowed()) throw new IOException("hardwareAccessPaused");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            handle?.Dispose();
            handle = null;
        }
    }

    private sealed class BusLease(PawnIoLpcEcPorts owner, Mutex mutex) : IDisposable
    {
        private bool released;
        public void Dispose()
        {
            if (released) return;
            if (owner.leaseThreadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("pawnIoLpcBusWrongThread");
            released = true;
            try { mutex.ReleaseMutex(); }
            finally
            {
                owner.leaseThreadId = 0;
                mutex.Dispose();
                Monitor.Exit(owner.gate);
            }
        }
    }

    internal static bool HasTrustedDependency(Func<byte[]?>? module = null, Func<DriverIdentity?>? driver = null) =>
        DependencyFailure(module ?? ReadEmbeddedModule, driver ?? ReadRunningDriver) is null;

    private static string? DependencyFailure(Func<byte[]?> module, Func<DriverIdentity?> driver)
    {
        try
        {
            return !IsPinnedModule(module()) ? "pawnIoLpcModuleUntrusted"
                : !IsTrustedDriver(driver()) ? "pawnIoLpcDriverUntrusted" : null;
        }
        catch { return "pawnIoLpcDependencyInspectionFailed"; }
    }

    internal static string DiagnoseDependency()
    {
        string reason = DependencyFailure(ReadEmbeddedModule, ReadRunningDriver) ?? "protocolUnverified";
        return JsonSerializer.Serialize(new
        {
            backend = "pawnIoLpc", dependency = reason == "protocolUnverified" ? "trusted" : "unavailable", reason
        });
    }

    private static bool IsPinnedModule(byte[]? module) => module is { Length: 17172 } &&
        Convert.ToHexString(SHA256.HashData(module)) == ModuleHash;

    internal static bool IsTrustedDriver(DriverIdentity? driver) => driver is not null &&
        driver.State == "Running" && Path.IsPathFullyQualified(driver.Path) &&
        string.Equals(Path.GetExtension(driver.Path), ".sys", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(driver.Sha256, DriverHash, StringComparison.OrdinalIgnoreCase) && driver.Authenticode == "valid";

    internal static byte[]? ReadEmbeddedModule() => ReadEmbeddedModule(ModuleName);

    internal static byte[]? ReadEmbeddedModule(string resourceName)
    {
        var assembly = typeof(Computer).Assembly;
        if (assembly.GetManifestResourceNames().Count(name => name == resourceName) != 1) return null;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static DriverIdentity? ReadRunningDriver()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT State, PathName FROM Win32_SystemDriver WHERE Name='PawnIO'");
            using var drivers = searcher.Get();
            foreach (ManagementObject driver in drivers)
            {
                using (driver)
                {
                    string? state = driver["State"]?.ToString();
                    string? path = NormalizeDriverPath(driver["PathName"]?.ToString());
                    if (state != "Running" || path is null) return null;
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string hash = Convert.ToHexString(SHA256.HashData(file));
                    return new DriverIdentity(state, path, hash, BldingFanEcTransport.VerifyAuthenticode(path));
                }
            }
        }
        catch { return null; }
        return null;
    }

    private static string? NormalizeDriverPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        string path = rawPath.Trim().Trim('"');
        const string systemRoot = @"\SystemRoot\";
        if (path.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[systemRoot.Length..]);
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);
        else if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        return Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".sys", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path) : null;
    }

    internal sealed class NativeDeviceApi : IPawnIoDeviceApi
    {
        private const uint DeviceType = 41394u << 16;
        private readonly DeviceIoControlCall control;
        internal NativeDeviceApi() : this(Control) { }
        internal NativeDeviceApi(DeviceIoControlCall control) => this.control = control;

        public SafeFileHandle Open() => new(CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",
            0xC0000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero), ownsHandle: true);

        public bool LoadModule(SafeFileHandle device, byte[] module) =>
            !device.IsInvalid && !device.IsClosed && control(device, DeviceType | (0x821u << 2), module, null, out var returned) && returned == 0;

        public bool Execute(SafeFileHandle device, string functionName, ulong[]? input, ulong[]? output)
        {
            if (device.IsInvalid || device.IsClosed) return false;
            if (functionName.Length is < 1 or > 31 || functionName.Any(character => character is < ' ' or > '~'))
                throw new ArgumentException("pawnIoFunctionNameInvalid", nameof(functionName));
            var request = new byte[checked(32 + (input?.Length ?? 0) * sizeof(ulong))];
            Encoding.ASCII.GetBytes(functionName).CopyTo(request, 0);
            if (input is { Length: > 0 }) Buffer.BlockCopy(input, 0, request, 32, checked(input.Length * sizeof(ulong)));
            var outputBuffer = output is { Length: > 0 } ? new byte[checked(output.Length * sizeof(ulong))] : null;
            if (!control(device, DeviceType | (0x841u << 2), request, outputBuffer, out var returned) ||
                returned != (outputBuffer?.Length ?? 0)) return false;
            if (outputBuffer is not null) Buffer.BlockCopy(outputBuffer, 0, output!, 0, outputBuffer.Length);
            return true;
        }

        private static bool Control(SafeFileHandle device, uint code, byte[] input, byte[]? output, out uint returned) =>
            DeviceIoControl(device, code, input, checked((uint)input.Length), output,
                checked((uint)(output?.Length ?? 0)), out returned, IntPtr.Zero);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input,
            uint inputSize, [Out] byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
    }
}
