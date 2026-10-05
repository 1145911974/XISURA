using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Jiaolong.Service.Home;

// Port driver and EC sequence used by F:\JiaoLongControl, pinned to this machine's signed files.
internal sealed class BldingFanEcTransport : IDisposable
{
    private const string DllHash = "A6F6B0F5C5B5523D2E6EBCE3DF54149B65E5D07C0B5E49AB91816E766DE6B65E";
    private const string DriverHash = "A3D7FD8F8713726A54FAD81052864373E07D64822E7525505577F27D8165ABF9";
    private const string DriverName = "JiaoLongDriver64";
    private static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Drivers", "Blding");
    private nint module;
    private InitializeBldring? initialize;
    private ShutdownBldring? shutdown;
    private GetBLDPortVal? readPort;
    private SetBLDPortVal? writePort;
    private bool initialized;
    private byte? previousInitialization;
    public bool HasWritten { get; private set; }

    public static bool HasVerifiedFiles()
    {
        string driverPath = Path.Combine(DirectoryPath, DriverName + ".sys");
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + DriverName);
        var image = (service?.GetValue("ImagePath") as string)?.Trim('"');
        if (image?.StartsWith(@"\??\", StringComparison.Ordinal) == true) image = image[4..];
        return string.Equals(image, driverPath, StringComparison.OrdinalIgnoreCase) &&
            Matches(Path.Combine(DirectoryPath, DriverName + ".dll"), DllHash) && Matches(driverPath, DriverHash);
    }

    private static bool Matches(string path, string hash)
    {
        try { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)) == hash; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Open()
    {
        if (initialized) return;
        if (!HasVerifiedFiles()) throw new InvalidOperationException("bldingDependencyMismatch");
        using (var service = new ServiceController(DriverName))
        {
            if (service.Status == ServiceControllerStatus.Stopped) service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
        }
        module = NativeLibrary.Load(Path.Combine(DirectoryPath, DriverName + ".dll"));
        try
        {
            initialize = Export<InitializeBldring>("InitializeBldring");
            shutdown = Export<ShutdownBldring>("ShutdownBldring");
            readPort = Export<GetBLDPortVal>("GetBLDPortVal");
            writePort = Export<SetBLDPortVal>("SetBLDPortVal");
            if (!initialize()) throw new IOException("bldingInitializeFailed");
            initialized = true;
            WithEcGate(() =>
            {
                if (ReadRegister(0x2000) != 0x55) throw new IOException("bldingEcSignatureMismatch");
                byte before = ReadRegister(0x1060);
                previousInitialization = before;
                if ((before & 0x80) == 0) WriteRegister(0x1060, (byte)(before | 0x80));
                return true;
            });
        }
        catch
        {
            // Keep the module alive if an initialization write needs the caller's recovery path.
            if (!HasWritten) Dispose();
            throw;
        }
    }

    public void SetTargets(byte cpu, byte gpu)
    {
        EnsureOpen();
        if (cpu is < 6 or > 68 || gpu is < 6 or > 68) throw new ArgumentOutOfRangeException(nameof(cpu));
        WithEcGate(() =>
        {
            WriteRegister(0xC83D, gpu);
            WriteRegister(0x0B20, (byte)(ReadRegister(0x0B20) | 0x08));
            WriteRegister(0xC83C, cpu);
            WriteRegister(0x0B20, (byte)(ReadRegister(0x0B20) | 0x02));
            return true;
        });
    }

    public void Clear()
    {
        if (!initialized) return;
        WithEcGate(() =>
        {
            WriteRegister(0xC83D, 0);
            WriteRegister(0xC83C, 0);
            WriteRegister(0x0B20, (byte)(ReadRegister(0x0B20) & ~0x0A));
            if (previousInitialization is byte previous)
                WriteRegister(0x1060, (byte)((ReadRegister(0x1060) & 0x7F) | (previous & 0x80)));
            if ((ReadRegister(0x0B20) & 0x0A) != 0) throw new IOException("bldingFanReleaseNotConfirmed");
            return true;
        });
        previousInitialization = null;
        HasWritten = false;
    }

    public Jiaolong.Contracts.Models.FanEcControlState ReadControlState() =>
        WithEcGate(() => new Jiaolong.Contracts.Models.FanEcControlState(DateTimeOffset.UtcNow,
            ReadRegister(0x1060), ReadRegister(0x0B20), ReadRegister(0xC83C), ReadRegister(0xC83D), previousInitialization));

    internal static T WithEcGate<T>(Func<T> operation)
    {
        // Register selection spans several port writes; protect the whole transaction across processes.
        using var mutex = new Mutex(false, @"Global\Access_EC");
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("bldingEcMutexUnavailable");
            return operation();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private void SelectRegister(ushort address)
    {
        Write(0x4E, 0x2E); Write(0x4F, 0x11);
        Write(0x4E, 0x2F); Write(0x4F, (byte)(address >> 8));
        Write(0x4E, 0x2E); Write(0x4F, 0x10);
        Write(0x4E, 0x2F); Write(0x4F, (byte)address);
        Write(0x4E, 0x2E); Write(0x4F, 0x12);
        Write(0x4E, 0x2F);
    }

    private byte ReadRegister(ushort address)
    {
        EnsureOpen();
        SelectRegister(address);
        byte value = 0;
        if (!readPort!(0x4F, ref value, 1)) throw new IOException($"bldingEcReadFailed:{address:X4}");
        return value;
    }

    private void WriteRegister(ushort address, byte value)
    {
        EnsureOpen();
        SelectRegister(address);
        HasWritten = true;
        Write(0x4F, value);
    }

    private void Write(ushort port, byte value)
    {
        if (!writePort!(port, value, 1)) throw new IOException($"bldingPortWriteFailed:{port:X4}");
    }

    private void EnsureOpen() { if (!initialized) throw new InvalidOperationException("bldingNotInitialized"); }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    public void Dispose()
    {
        if (module == 0) return;
        if (initialized) shutdown?.Invoke();
        initialized = false;
        NativeLibrary.Free(module);
        module = 0;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool InitializeBldring();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ShutdownBldring();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool GetBLDPortVal(ushort port, ref byte value, byte size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool SetBLDPortVal(ushort port, byte value, byte size);
}
