using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Jiaolong.Service.Home;

// The EC protocol and WinRing0 driver are the same ones used by the installed 7.3 console.
internal sealed class WinRingFanEcTransport : IDisposable
{
    private const string DllHash = "A746FD5728E7485F741CC330A279674BC8590B1B8007D8614046C49F58698485";
    private const string DriverHash = "11BD2C9F9E2397C9A16E0990E4ED2CF0679498FE0FD418A3DFDAC60B5C160EE5";
    private const uint AddressPort = 0x4E;
    private const uint DataPort = 0x4F;
    private const uint EcStatusPort = 0x66;
    private nint module;
    private InitializeOls? initialize;
    private DeinitializeOls? deinitialize;
    private GetDllStatus? status;
    private ReadIoPortByteEx? readPort;
    private WriteIoPortByte? writePort;
    private bool initialized;
    public bool HasWritten { get; private set; }
    private static string? VerifiedDirectory => GetVerifiedDirectory();

    public static bool HasVerifiedFiles() => VerifiedDirectory is not null &&
        Matches(Path.Combine(AppContext.BaseDirectory, "WinRing0x64.sys"), DriverHash);

    private static string? GetVerifiedDirectory()
    {
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinRing0_1_2_0");
        var image = service?.GetValue("ImagePath") as string;
        if (string.IsNullOrWhiteSpace(image)) return null;
        var driver = image.StartsWith(@"\??\", StringComparison.Ordinal) ? image[4..] : image;
        var directory = Path.GetDirectoryName(driver);
        return directory is not null &&
               Matches(Path.Combine(directory, "WinRing0x64.dll"), DllHash) &&
               Matches(Path.Combine(directory, "WinRing0x64.sys"), DriverHash)
            ? directory : null;
    }

    private static bool Matches(string path, string expected)
    {
        try { return File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == expected; }
        catch { return false; }
    }

    public void Open()
    {
        if (initialized) return;
        if (!HasVerifiedFiles()) throw new InvalidOperationException("winRing0DependencyMismatch");
        var directory = VerifiedDirectory!;
        module = NativeLibrary.Load(Path.Combine(directory, "WinRing0x64.dll"));
        try
        {
            initialize = Export<InitializeOls>("InitializeOls");
            deinitialize = Export<DeinitializeOls>("DeinitializeOls");
            status = Export<GetDllStatus>("GetDllStatus");
            readPort = Export<ReadIoPortByteEx>("ReadIoPortByteEx");
            writePort = Export<WriteIoPortByte>("WriteIoPortByte");
            if (!initialize() || status() != 0)
                throw new InvalidOperationException($"winRing0InitializeFailed:{status()}");
            initialized = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetTargets(byte cpu, byte gpu)
    {
        EnsureOpen();
        WriteRegister(0xC83C, cpu);
        WriteRegister(0xC83D, gpu);
        // 7.3's Read_EC_EX always returns zero; its OR 2 therefore writes 2.
        WriteRegister(0x0B20, 2);
    }

    public void Clear()
    {
        if (!initialized) return;
        WriteRegister(0xC83C, 0);
        WriteRegister(0xC83D, 0);
        WriteRegister(0x0B20, 0);
        HasWritten = false;
    }

    private void WriteRegister(ushort index, byte value)
    {
        if (!EcStatus()) throw new IOException($"winRingEcBusy:{index:X4}");
        Write(AddressPort, 0x2E); Write(DataPort, 0x11);
        Write(AddressPort, 0x2F); Write(DataPort, (byte)(index >> 8));
        Write(AddressPort, 0x2E); Write(DataPort, 0x10);
        Write(AddressPort, 0x2F); Write(DataPort, (byte)index);
        Write(AddressPort, 0x2E); Write(DataPort, 0x12);
        Write(AddressPort, 0x2F); Write(DataPort, value);
    }

    private bool EcStatus()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            byte value = 0;
            // IBF=1 means the EC input buffer is busy; writes require IBF=0.
            if (readPort!(EcStatusPort, ref value) && (value & 2) == 0) return true;
        }
        return false;
    }

    private void Write(uint port, byte value)
    {
        HasWritten = true;
        writePort!(port, value);
    }
    private void EnsureOpen() { if (!initialized) throw new InvalidOperationException("winRing0NotInitialized"); }
    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    public void Dispose()
    {
        if (module == 0) return;
        if (initialized) deinitialize?.Invoke();
        initialized = false;
        NativeLibrary.Free(module);
        module = 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool InitializeOls();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeinitializeOls();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetDllStatus();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool ReadIoPortByteEx(uint port, ref byte value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WriteIoPortByte(uint port, byte value);
}
