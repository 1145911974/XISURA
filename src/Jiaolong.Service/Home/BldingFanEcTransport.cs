using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
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
    private bool ready;
    private bool signatureVerified;
    private byte? previousInitialization;
    private readonly Func<bool>? hardwareAccessAllowed;
    private readonly Func<bool>? hardwareReadAllowed;
    public bool HasWritten { get; private set; }

    public BldingFanEcTransport(Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null)
    {
        this.hardwareAccessAllowed = hardwareAccessAllowed;
        this.hardwareReadAllowed = hardwareReadAllowed;
    }

    internal BldingFanEcTransport(Func<ushort, byte> portReader, Action<ushort, byte> portWriter,
        Action shutdownDriver, Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null)
        : this(hardwareAccessAllowed, hardwareReadAllowed)
    {
        readPort = (ushort port, ref byte value, byte size) => { value = portReader(port); return true; };
        writePort = (port, value, size) => { portWriter(port, value); return true; };
        shutdown = () => shutdownDriver();
        initialized = true;
        ready = signatureVerified = true;
    }

    private bool IsAccessAllowed(bool readOnly = false) =>
        (readOnly ? hardwareReadAllowed ?? hardwareAccessAllowed : hardwareAccessAllowed)?.Invoke() ?? true;

    private void EnsureAccess(bool readOnly = false)
    {
        if (!IsAccessAllowed(readOnly)) throw new IOException("hardwareAccessPaused");
    }

    private T HardwareStage<T>(Func<T> operation)
    {
        EnsureAccess();
        try { return operation(); }
        finally { EnsureAccess(); }
    }

    private void HardwareStage(Action operation) => HardwareStage(() => { operation(); return true; });

    public static bool HasVerifiedFiles()
    {
        try
        {
            string driverPath = Path.Combine(DirectoryPath, DriverName + ".sys");
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + DriverName);
            var image = (service?.GetValue("ImagePath") as string)?.Trim('"');
            if (image?.StartsWith(@"\??\", StringComparison.Ordinal) == true) image = image[4..];
            return string.Equals(image, driverPath, StringComparison.OrdinalIgnoreCase) &&
                Matches(Path.Combine(DirectoryPath, DriverName + ".dll"), DllHash) && Matches(driverPath, DriverHash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static string DiagnoseDependency()
    {
        var dllPath = Path.Combine(DirectoryPath, DriverName + ".dll");
        var driverPath = Path.Combine(DirectoryPath, DriverName + ".sys");
        string dll = DiagnoseFile(dllPath, DllHash, out var dllVersion, out var dllSignature);
        string driver = DiagnoseFile(driverPath, DriverHash, out var driverVersion, out var driverSignature);
        string registration = "missing";
        string serviceState = "not-queried";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + DriverName);
            if (key is null) registration = "missing";
            else
            {
                registration = "registered";
                var image = (key.GetValue("ImagePath") as string)?.Trim('"');
                if (image?.StartsWith(@"\??\", StringComparison.Ordinal) == true) image = image[4..];
                if (!string.Equals(image, driverPath, StringComparison.OrdinalIgnoreCase)) registration = "imagePathMismatch";
                try
                {
                    using var service = new ServiceController(DriverName);
                    serviceState = service.Status.ToString().ToLowerInvariant();
                }
                catch (Win32Exception error) when (error.NativeErrorCode == 5) { serviceState = "accessDenied"; }
                catch (Win32Exception error) when (error.NativeErrorCode == 577) { serviceState = "blockedByPolicy"; }
                catch (InvalidOperationException) { serviceState = "unavailable"; }
            }
        }
        catch (UnauthorizedAccessException) { registration = "accessDenied"; }
        catch (SecurityException) { registration = "accessDenied"; }
        catch (IOException) { registration = "readFailed"; }

        return JsonSerializer.Serialize(new
        {
            dll, dllVersion, dllSignature, driver, driverVersion, driverSignature,
            driverService = registration, driverServiceState = serviceState,
            protocolHandshake = "notAttempted(readOnlyDiagnosisDoesNotInitializeEC)"
        });
    }

    private static string DiagnoseFile(string path, string expectedHash, out string? version, out string signature)
    {
        version = null;
        signature = "unavailable";
        try
        {
            if (!File.Exists(path)) return "missing";
            version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            using var stream = File.OpenRead(path);
            var matches = Convert.ToHexString(SHA256.HashData(stream)) == expectedHash;
            signature = VerifyAuthenticode(path);
            version ??= "unavailable";
            return matches ? "pinnedHashMatch" : "pinnedHashMismatch";
        }
        catch (UnauthorizedAccessException) { return "accessDenied"; }
        catch (IOException) { return "readFailed"; }
        catch (Win32Exception error) when (error.NativeErrorCode == 577) { return "blockedByPolicy"; }
        catch (SecurityException) { return "accessDenied"; }
        catch (PlatformNotSupportedException) { return "signatureCheckUnavailable"; }
        catch (Exception error) when (error is CryptographicException or ArgumentException) { return "inspectionFailed"; }
    }

    private static string VerifyAuthenticode(string path)
    {
        var file = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData(filePointer);
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return status switch
            {
                0 => "valid",
                unchecked((int)0x800B0100) => "unsigned",
                unchecked((int)0x80096010) => "badDigest",
                unchecked((int)0x800B0003) => "blockedByPolicy",
                _ => $"invalid:{status:X8}"
            };
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public readonly uint Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        public readonly string FilePath;
        public readonly IntPtr FileHandle = IntPtr.Zero;
        public readonly IntPtr KnownSubject = IntPtr.Zero;
        public WinTrustFileInfo(string filePath) => FilePath = filePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public readonly uint Size;
        public readonly IntPtr PolicyCallbackData = IntPtr.Zero;
        public readonly IntPtr SipClientData = IntPtr.Zero;
        public readonly uint UiChoice = 2;
        public readonly uint RevocationChecks = 0;
        public readonly uint UnionChoice = 1;
        public readonly IntPtr FileInfo;
        public uint StateAction;
        public readonly IntPtr StateData = IntPtr.Zero;
        public readonly IntPtr UrlReference = IntPtr.Zero;
        public readonly uint ProviderFlags = 0x1000; // WTD_CACHE_ONLY_URL_RETRIEVAL: diagnosis must not wait for network.
        public readonly uint UiContext = 0;
        public readonly IntPtr SignatureSettings = IntPtr.Zero;
        public WinTrustData(IntPtr fileInfo)
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            FileInfo = fileInfo;
            StateAction = 1;
        }
    }

    private static bool Matches(string path, string hash)
    {
        try { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)) == hash; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Open()
    {
        EnsureAccess();
        if (ready) return;
        if (module != 0 || initialized) throw new IOException("bldingInitializationRecoveryRequired");
        if (!HardwareStage(HasVerifiedFiles)) throw new InvalidOperationException("bldingDependencyMismatch");
        using (var service = new ServiceController(DriverName))
        {
            if (HardwareStage(() => service.Status) == ServiceControllerStatus.Stopped) HardwareStage(service.Start);
            HardwareStage(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3)));
        }
        try
        {
            HardwareStage(() => { module = NativeLibrary.Load(Path.Combine(DirectoryPath, DriverName + ".dll")); });
            initialize = Export<InitializeBldring>("InitializeBldring");
            shutdown = Export<ShutdownBldring>("ShutdownBldring");
            readPort = Export<GetBLDPortVal>("GetBLDPortVal");
            writePort = Export<SetBLDPortVal>("SetBLDPortVal");
            HardwareStage(() =>
            {
                if (!initialize()) throw new IOException("bldingInitializeFailed");
                initialized = true;
            });
            WithEcGate(() =>
            {
                if (ReadRegister(0x2000) != 0x55) throw new IOException("bldingEcSignatureMismatch");
                signatureVerified = true;
                byte before = ReadRegister(0x1060);
                previousInitialization = before;
                if ((before & 0x80) == 0) WriteRegister(0x1060, (byte)(before | 0x80));
                return true;
            });
            EnsureAccess();
            ready = true;
        }
        catch
        {
            // Keep the module alive if an initialization write needs the caller's recovery path.
            if (!HasWritten && IsAccessAllowed()) Dispose();
            throw;
        }
    }

    public void SetTargets(byte cpu, byte gpu)
    {
        EnsureAccess();
        EnsureOpen();
        if (!ready) throw new IOException("bldingInitializationRecoveryRequired");
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
        EnsureAccess();
        if (!initialized) return;
        if (!signatureVerified) throw new IOException("bldingEcSignatureNotVerified");
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
        EnsureAccess();
        previousInitialization = null;
        HasWritten = false;
    }

    public Jiaolong.Contracts.Models.FanEcControlState ReadControlState()
    {
        EnsureAccess(readOnly: true);
        if (!signatureVerified) throw new IOException("bldingEcSignatureNotVerified");
        return WithEcGate(() => new Jiaolong.Contracts.Models.FanEcControlState(DateTimeOffset.UtcNow,
            ReadRegister(0x1060, true), ReadRegister(0x0B20, true), ReadRegister(0xC83C, true),
            ReadRegister(0xC83D, true), previousInitialization));
    }

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

    private byte ReadRegister(ushort address, bool readOnly = false)
    {
        EnsureAccess(readOnly);
        EnsureOpen();
        try
        {
            // Finish the existing indexed-port sequence; the safe boundary is the whole register transaction.
            SelectRegister(address);
            byte value = 0;
            if (!readPort!(0x4F, ref value, 1)) throw new IOException($"bldingEcReadFailed:{address:X4}");
            return value;
        }
        finally { EnsureAccess(readOnly); }
    }

    private void WriteRegister(ushort address, byte value)
    {
        EnsureAccess();
        EnsureOpen();
        // Selection already performs native I/O; a failure there cannot prove the transaction was untouched.
        HasWritten = true;
        try { SelectRegister(address); Write(0x4F, value); }
        finally { EnsureAccess(); }
    }

    private void Write(ushort port, byte value)
    {
        if (!writePort!(port, value, 1)) throw new IOException($"bldingPortWriteFailed:{port:X4}");
    }

    private void EnsureOpen() { if (!initialized) throw new InvalidOperationException("bldingNotInitialized"); }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    public void Dispose()
    {
        if (module == 0 && !initialized) return;
        EnsureAccess();
        if (HasWritten) throw new IOException("bldingRecoveryRequired");
        if (initialized)
            HardwareStage(() => { shutdown?.Invoke(); initialized = ready = signatureVerified = false; });
        if (module != 0)
            HardwareStage(() => { NativeLibrary.Free(module); module = 0; });
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool InitializeBldring();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ShutdownBldring();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool GetBLDPortVal(ushort port, ref byte value, byte size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool SetBLDPortVal(ushort port, byte value, byte size);
}
