using System.Runtime.InteropServices;
using System.Text;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Telemetry;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Home;

internal interface IPawnIoDeviceApi
{
    SafeFileHandle Open();
    bool LoadModule(SafeFileHandle device, byte[] module);
    bool Execute(SafeFileHandle device, string functionName, ulong[]? input, ulong[]? output);
}

internal sealed class PawnIoCurveOptimizerTransport : IPerCoreCurveOptimizerTransport, IPboScalarTransport, ISmuCpuLimitTransport, IDisposable
{
    private const int FunctionNameLength = 32;
    private const int MailboxTimeoutMilliseconds = 200;
    private const ulong SmuOk = 0x01;
    private const uint MsrPowerUnit = 0xC0010299;
    private const uint MsrPackageEnergy = 0xC001029B;
    private const uint DeviceType = 41394u << 16;
    private const uint LoadIoctl = DeviceType | (0x821u << 2);
    private const uint ExecuteIoctl = DeviceType | (0x841u << 2);
    private static readonly string PawnIoDevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    private SafeFileHandle? safeHandle;
    private SafeFileHandle? msrSafeHandle;
    private readonly IPawnIoDeviceApi? deviceApiOverride;
    private readonly Func<uint, uint, uint>? curveCommandOverride;
    private readonly CpuPackagePowerCalculator packagePowerCalculator = new();
    private readonly object mailboxGate = new();
    private readonly string pciMutexName = @"Global\Access_PCI";
    private bool initialized;
    private bool msrInitialized;
    private bool disposed;
    private readonly int physicalCoreCount;
    private readonly Func<bool>? hardwareAccessAllowed;
    private readonly Func<bool>? hardwareReadAllowed;

    private bool IsHardwareAccessAllowed(bool readOnly = false) =>
        (readOnly ? hardwareReadAllowed ?? hardwareAccessAllowed : hardwareAccessAllowed)?.Invoke() != false;

    private void EnsureHardwareAccess(bool readOnly = false)
    {
        if (!IsHardwareAccessAllowed(readOnly)) throw new IOException("hardwareAccessPaused");
    }
    // Package-power reads need no topology. CO stays unavailable until a count is supplied.
    public PawnIoCurveOptimizerTransport(Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null)
    {
        this.hardwareAccessAllowed = hardwareAccessAllowed;
        this.hardwareReadAllowed = hardwareReadAllowed;
    }
    public PawnIoCurveOptimizerTransport(int physicalCoreCount, Func<bool>? hardwareAccessAllowed = null,
        Func<bool>? hardwareReadAllowed = null) : this(hardwareAccessAllowed, hardwareReadAllowed)
    {
        if (physicalCoreCount is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(physicalCoreCount));
        this.physicalCoreCount = physicalCoreCount;
    }
    internal PawnIoCurveOptimizerTransport(Func<uint, uint, uint> curveCommand, int physicalCoreCount = 8,
        Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null,
        string pciMutexName = @"Global\Access_PCI")
        : this(physicalCoreCount, hardwareAccessAllowed, hardwareReadAllowed)
    {
        curveCommandOverride = curveCommand;
        this.pciMutexName = pciMutexName;
    }
    internal PawnIoCurveOptimizerTransport(IPawnIoDeviceApi deviceApi, int physicalCoreCount = 8,
        Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null,
        string pciMutexName = @"Global\Access_PCI")
        : this(physicalCoreCount, hardwareAccessAllowed, hardwareReadAllowed)
    {
        deviceApiOverride = deviceApi;
        this.pciMutexName = pciMutexName;
    }

    private IDisposable AcquirePciBus(CancellationToken cancellationToken)
    {
        var mutex = new Mutex(false, pciMutexName);
        bool held = false;
        try
        {
            try
            {
                held = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], MailboxTimeoutMilliseconds) == 0;
            }
            catch (AbandonedMutexException error)
            {
                held = true;
                throw new IOException("cpuSmuPciMutexAbandoned", error);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!held) throw new IOException("cpuSmuPciMutexTimeout");
            return new PciBusLease(mutex);
        }
        catch
        {
            try { if (held) mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
            throw;
        }
    }

    private sealed class PciBusLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }

    public bool TryInitialize()
    {
        EnsureHardwareAccess(readOnly: true);
        if (curveCommandOverride is not null) return !disposed;
        if (initialized) return true;
        if (disposed) return false;

        var module = ReadRyzenSmuModule();
        if (module is null) return false;

        var handle = OpenModule(module);
        if (handle is null) return false;
        try
        {
            var result = new ulong[1];
            if (!Execute("ioctl_get_code_name", null, result, handle) || result[0] != (uint)RyzenSmuProtocol.DragonRangeCodeName)
            {
                handle.Dispose();
                Dispose();
                return false;
            }
            safeHandle = handle;
            initialized = true;
            return true;
        }
        catch { handle.Dispose(); throw; }
    }

    private SafeFileHandle? OpenModule(byte[] module)
    {
        EnsureHardwareAccess(readOnly: true);
        var handle = deviceApiOverride?.Open() ?? new SafeFileHandle(
            CreateFile(PawnIoDevicePath, 0xC0000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero), ownsHandle: true);
        try
        {
            EnsureHardwareAccess(readOnly: true);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            bool loaded = deviceApiOverride?.LoadModule(handle, module) ??
                DeviceIoControl(handle, LoadIoctl, module, checked((uint)module.Length), null, 0, out _, IntPtr.Zero);
            EnsureHardwareAccess(readOnly: true);
            if (!loaded)
            {
                handle.Dispose();
                return null;
            }
            return handle;
        }
        catch { handle.Dispose(); EnsureHardwareAccess(readOnly: true); throw; }
    }

    public Task<int> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var values = ReadPerCore(cancellationToken).Values.Distinct().ToArray();
            return values.Length == 1 ? Task.FromResult(values[0]) :
                Task.FromException<int>(new InvalidOperationException("curveOptimizerMixedCoreValues"));
        }
        catch (Exception error) { return Task.FromException<int>(error); }
    }

    public IReadOnlyDictionary<int, int> ReadPerCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess(readOnly: true);
        if (physicalCoreCount == 0) throw new InvalidOperationException("cpuCoreTopologyUnknown");
        if (!TryInitialize()) throw new InvalidOperationException("curveOptimizerUnavailable");
        lock (mailboxGate)
        {
            using var pci = AcquirePciBus(cancellationToken);
            return ReadPerCoreLocked(cancellationToken);
        }
    }

    private Dictionary<int, int> ReadPerCoreLocked(CancellationToken cancellationToken)
    {
        var values = new Dictionary<int, int>();
        for (int core = 0; core < physicalCoreCount; core++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint raw = CurveCommand(0xD5, (uint)core << 20);
            int value = unchecked((short)(raw & 0xFFFF));
            if (value is < -50 or > 30) throw new InvalidOperationException("curveOptimizerReadbackInvalid");
            values.Add(core, value);
        }
        return values;
    }

    private uint CurveCommand(uint command, uint argument)
    {
        bool readOnly = command == 0xD5;
        EnsureHardwareAccess(readOnly);
        try
        {
            return curveCommandOverride is not null ? curveCommandOverride(command, argument)
                : SendMailboxAndReadFirst(RyzenSmuProtocol.RaphaelCommandAddress, RyzenSmuProtocol.RaphaelResponseAddress,
                    RyzenSmuProtocol.RaphaelArgumentAddress, command, argument, readOnly);
        }
        finally { EnsureHardwareAccess(readOnly); }
    }

    internal double? ReadPackagePower(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess(readOnly: true);
        if (!TryInitializeMsr() ||
            !ReadMsr(MsrPowerUnit, out var powerUnit) ||
            !ReadMsr(MsrPackageEnergy, out var packageEnergy))
        {
            return null;
        }

        return packagePowerCalculator.Update(powerUnit, packageEnergy, DateTimeOffset.UtcNow);
    }

    public Task WriteAsync(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is < -50 or > 0)
            return Task.FromException(new ArgumentOutOfRangeException(nameof(value)));
        try
        {
            WritePerCore(Enumerable.Range(0, physicalCoreCount).ToDictionary(core => core, _ => value), cancellationToken);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    public Task RestoreAsync(int value, CancellationToken cancellationToken) => WriteAsync(value, cancellationToken);

    public void WritePerCore(IReadOnlyDictionary<int, int> values, CancellationToken cancellationToken)
    {
        bool writeAttempted = false;
        try { WritePerCore(values, cancellationToken, ref writeAttempted); }
        catch (Exception error)
        {
            error.Data["curveOptimizerWriteAttempted"] = writeAttempted;
            throw;
        }
    }

    private void WritePerCore(IReadOnlyDictionary<int, int> values, CancellationToken cancellationToken, ref bool writeAttempted)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 1 || values.Count > physicalCoreCount)
            throw new ArgumentException("perCoreCurveRequiresEditedCores", nameof(values));
        foreach (var (core, value) in values)
        {
            if (core < 0 || core >= physicalCoreCount) throw new ArgumentOutOfRangeException(nameof(values), "perCoreIndexUnsupported");
            if (value is < -50 or > 0) throw new ArgumentOutOfRangeException(nameof(values), "perCoreCurveOutOfRange");
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess();
        if (!TryInitialize()) throw new InvalidOperationException("curveOptimizerUnavailable");

        lock (mailboxGate)
        {
            using var pci = AcquirePciBus(cancellationToken);
            EnsureHardwareAccess();
            var before = ReadPerCoreLocked(cancellationToken);
            try
            {
                foreach (var (core, value) in values.OrderBy(pair => pair.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (before[core] != value)
                    {
                        EnsureHardwareAccess();
                        writeAttempted = true;
                        WriteCore(core, value);
                    }
                }
                var after = ReadPerCoreLocked(cancellationToken);
                if (before.Any(pair => after[pair.Key] != (values.TryGetValue(pair.Key, out int target) ? target : pair.Value)))
                    throw new IOException("curveOptimizerReadbackMismatch");
            }
            catch (Exception error)
            {
                if (!writeAttempted)
                {
                    error.Data["curveOptimizerRollbackFailed"] = false;
                    throw;
                }
                try { RestorePerCoreLocked(before); }
                catch (Exception restoreError)
                {
                    var failure = new AggregateException("curveOptimizerRecoveryFailed", error, restoreError);
                    failure.Data["curveOptimizerRollbackFailed"] = true;
                    throw failure;
                }
                error.Data["curveOptimizerRollbackFailed"] = false;
                throw;
            }
        }
    }

    private void WriteCore(int core, int value) =>
        _ = CurveCommand(0x06, ((uint)core << 20) | unchecked((ushort)(short)value));

    public void RestorePerCore(IReadOnlyDictionary<int, int> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != physicalCoreCount || Enumerable.Range(0, physicalCoreCount).Any(core => !values.TryGetValue(core, out int value) || value is < -50 or > 30))
            throw new ArgumentException("curveOptimizerSnapshotInvalid", nameof(values));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess();
        if (!TryInitialize()) throw new InvalidOperationException("curveOptimizerUnavailable");
        lock (mailboxGate)
        {
            using var pci = AcquirePciBus(cancellationToken);
            RestorePerCoreLocked(values);
        }
    }

    private void RestorePerCoreLocked(IReadOnlyDictionary<int, int> values)
    {
        // Request cancellation does not cancel recovery; a power transition still forbids it.
        EnsureHardwareAccess();
        foreach (var (core, value) in values.OrderBy(pair => pair.Key)) WriteCore(core, value);
        var restored = ReadPerCoreLocked(CancellationToken.None);
        if (values.Any(pair => restored[pair.Key] != pair.Value)) throw new IOException("curveOptimizerRestoreMismatch");
    }

    private void SendCurveCommand(uint mp1Command, uint rsmuCommand, uint argument)
    {
        try
        {
            SendMailbox(RyzenSmuProtocol.Mp1CommandAddress, RyzenSmuProtocol.Mp1ResponseAddress,
                RyzenSmuProtocol.Mp1ArgumentAddress, mp1Command, argument);
        }
        catch (SmuCommandRejectedException exception) when (exception.Status is 0xFF or 0xFE or 0xFD)
        {
            // A timeout is ambiguous: only a firmware rejection permits the alternate mailbox.
            SendMailbox(RyzenSmuProtocol.RaphaelCommandAddress, RyzenSmuProtocol.RaphaelResponseAddress,
                RyzenSmuProtocol.RaphaelArgumentAddress, rsmuCommand, argument);
        }
    }

    public void WriteAdvancedLimit(RyzenSmuLimitWrite write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess();
        if (!TryInitialize()) throw new InvalidOperationException("advancedCpuProtocolUnavailable");
        lock (mailboxGate)
        {
            using var pci = AcquirePciBus(cancellationToken);
            if (write.Mp1 && write.FallbackRsmu is uint fallback)
                SendCurveCommand(write.Command, fallback, write.Argument);
            else
                SendMailbox(write.Mp1 ? RyzenSmuProtocol.Mp1CommandAddress : RyzenSmuProtocol.RaphaelCommandAddress,
                    write.Mp1 ? RyzenSmuProtocol.Mp1ResponseAddress : RyzenSmuProtocol.RaphaelResponseAddress,
                    write.Mp1 ? RyzenSmuProtocol.Mp1ArgumentAddress : RyzenSmuProtocol.RaphaelArgumentAddress,
                    write.Command, write.Argument);
        }
    }

    public CpuSmuLimitSnapshot ReadLimitSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess(readOnly: true);
        if (!TryInitialize()) throw new InvalidOperationException("advancedCpuProtocolUnavailable");
        lock (mailboxGate)
        {
            // The module requires Access_PCI across resolve, refresh and RAM sampling.
            using var pci = AcquirePciBus(cancellationToken);
            var resolved = new ulong[2];
            var resolutionSucceeded = Execute("ioctl_resolve_pm_table", [], resolved);
            if (!resolutionSucceeded || resolved[0] is not (0x540105 or 0x540108) || resolved[1] == 0)
            {
                var error = new InvalidOperationException("advancedCpuPmTableUnsupported");
                error.Data["pmTableId"] = $"0x{resolved[0]:X}";
                error.Data["pmTableResolutionSucceeded"] = resolutionSucceeded.ToString();
                error.Data["pmTableAddressAvailable"] = (resolved[1] != 0).ToString();
                throw error;
            }
            bool refreshed = false;
            for (int attempt = 0; attempt < 3 && !refreshed; attempt++)
            {
                if (attempt > 0 && cancellationToken.WaitHandle.WaitOne(40))
                    cancellationToken.ThrowIfCancellationRequested();
                refreshed = Execute("ioctl_update_pm_table", [], null);
            }
            var raw = new ulong[32];
            if (!refreshed || !Execute("ioctl_read_pm_table", [], raw))
                throw new InvalidOperationException("advancedCpuPmTableReadFailed");
            return RyzenSmuAdvancedLimits.DecodeSnapshot((uint)resolved[0], MemoryMarshal.Cast<ulong, float>(raw));
        }
    }

    private sealed class SmuCommandRejectedException(ulong status)
        : InvalidOperationException($"curveOptimizerSmuStatus:{status:X2}")
    {
        public ulong Status { get; } = status;
    }

    public int Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess(readOnly: true);
        if (!TryInitialize()) throw new InvalidOperationException("pboScalarUnavailable");
        lock (mailboxGate)
        {
            using var pci = AcquirePciBus(cancellationToken);
            uint raw = SendMailboxAndReadFirst(
                RyzenSmuProtocol.RaphaelCommandAddress,
                RyzenSmuProtocol.RaphaelResponseAddress,
                RyzenSmuProtocol.RaphaelArgumentAddress,
                RyzenSmuProtocol.GetPboScalarCommand, 0, readOnly: true);
            float scalar = BitConverter.Int32BitsToSingle(unchecked((int)raw));
            if (!float.IsFinite(scalar) || scalar < 1 || scalar > 10 || scalar != MathF.Round(scalar))
                throw new InvalidOperationException("pboScalarReadInvalid");
            return (int)scalar;
        }
    }

    public void Write(int scalar, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scalar is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(scalar));
        EnsureHardwareAccess();
        if (!TryInitialize()) throw new InvalidOperationException("pboScalarUnavailable");
        lock (mailboxGate)
        {
            using var pci = AcquirePciBus(cancellationToken);
            SendMailbox(
                RyzenSmuProtocol.RaphaelCommandAddress,
                RyzenSmuProtocol.RaphaelResponseAddress,
                RyzenSmuProtocol.RaphaelArgumentAddress,
                RyzenSmuProtocol.SetPboScalarCommand,
                checked((uint)(scalar * 100)));
        }
    }

    private void SendMailbox(
        uint commandAddress,
        uint responseAddress,
        uint argumentAddress,
        uint command,
        uint argument)
        => _ = SendMailboxAndReadFirst(commandAddress, responseAddress, argumentAddress, command, argument);

    private uint SendMailboxAndReadFirst(
        uint commandAddress,
        uint responseAddress,
        uint argumentAddress,
        uint command,
        uint argument,
        bool readOnly = false)
    {
        EnsureHardwareAccess(readOnly);
        try { return SendMailboxAndReadFirstCore(commandAddress, responseAddress, argumentAddress, command, argument); }
        finally { EnsureHardwareAccess(readOnly); }
    }

    private uint SendMailboxAndReadFirstCore(
        uint commandAddress, uint responseAddress, uint argumentAddress, uint command, uint argument)
    {
        if (!WaitForMailboxIdle(responseAddress) ||
            !WriteRegister(responseAddress, 0))
        {
            throw new InvalidOperationException("curveOptimizerMailboxBusy");
        }

        for (var index = 0; index < 6; index++)
        {
            var value = index == 0 ? argument : 0u;
            if (!WriteRegister(argumentAddress + (uint)(index * 4), value))
                throw new InvalidOperationException("curveOptimizerArgumentWriteFailed");
        }

        if (!WriteRegister(commandAddress, command))
            throw new InvalidOperationException("curveOptimizerCommandWriteFailed");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ulong status = 0;
        while (stopwatch.ElapsedMilliseconds < MailboxTimeoutMilliseconds)
        {
            if (!ReadRegister(responseAddress, out status))
                throw new InvalidOperationException("curveOptimizerStatusReadFailed");
            if (status != 0) break;
            Thread.Yield();
        }

        if (status != SmuOk)
        {
            var error = new SmuCommandRejectedException(status);
            error.Data["smuCommand"] = $"0x{command:X2}";
            error.Data["smuStatus"] = $"{status:X2}";
            throw error;
        }
        if (!ReadRegister(argumentAddress, out var responseArgument))
            throw new InvalidOperationException("pboScalarArgumentReadFailed");
        return unchecked((uint)responseArgument);
    }

    private bool WaitForMailboxIdle(uint responseAddress)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < MailboxTimeoutMilliseconds)
        {
            if (!ReadRegister(responseAddress, out var value)) return false;
            if (value != 0) return true;
            Thread.Yield();
        }

        return false;
    }

    private bool ReadRegister(uint address, out ulong value)
    {
        value = 0;
        var output = new ulong[1];
        if (!Execute("ioctl_read_smu_register", [address], output, guardLifecycle: false)) return false;
        value = output[0];
        return true;
    }

    private bool ReadMsr(uint address, out ulong value)
    {
        value = 0;
        var output = new ulong[1];
        if (!Execute("ioctl_read_msr", [address], output, msrSafeHandle)) return false;
        value = output[0];
        return true;
    }

    private bool WriteRegister(uint address, uint value) =>
        Execute("ioctl_write_smu_register", [address, value], null, guardLifecycle: false);

    private bool TryInitializeMsr()
    {
        EnsureHardwareAccess(readOnly: true);
        if (msrInitialized) return true;
        if (disposed) return false;

        var module = ReadEmbeddedModule("AMDFamily17.bin");
        if (module is null) return false;

        msrSafeHandle = OpenModule(module);
        if (msrSafeHandle is null) return false;
        msrInitialized = true;
        return true;
    }

    private bool Execute(
        string functionName,
        ulong[]? input,
        ulong[]? output,
        SafeFileHandle? deviceHandle = null,
        bool guardLifecycle = true)
    {
        // Register transfers finish the already-started mailbox transaction; no speculative mid-protocol abort.
        if (!guardLifecycle) return ExecuteCore(functionName, input, output, deviceHandle);
        EnsureHardwareAccess(readOnly: true);
        try { return ExecuteCore(functionName, input, output, deviceHandle); }
        finally { EnsureHardwareAccess(readOnly: true); }
    }

    private bool ExecuteCore(string functionName, ulong[]? input, ulong[]? output, SafeFileHandle? deviceHandle)
    {
        deviceHandle ??= safeHandle;
        if (deviceHandle is null || deviceHandle.IsInvalid || deviceHandle.IsClosed) return false;
        if (deviceApiOverride is not null) return deviceApiOverride.Execute(deviceHandle, functionName, input, output);
        var nameBytes = Encoding.ASCII.GetBytes(functionName);
        var inputCount = input?.Length ?? 0;
        var request = new byte[FunctionNameLength + inputCount * sizeof(ulong)];
        Buffer.BlockCopy(nameBytes, 0, request, 0, Math.Min(nameBytes.Length, FunctionNameLength - 1));
        if (inputCount > 0)
        {
            var inputBytes = new byte[inputCount * sizeof(ulong)];
            Buffer.BlockCopy(input!, 0, inputBytes, 0, inputBytes.Length);
            Buffer.BlockCopy(inputBytes, 0, request, FunctionNameLength, inputBytes.Length);
        }

        var outputBuffer = output is { Length: > 0 } ? new byte[output.Length * sizeof(ulong)] : null;
        var succeeded = DeviceIoControl(
            deviceHandle,
            ExecuteIoctl,
            request,
            checked((uint)request.Length),
            outputBuffer,
            checked((uint)(outputBuffer?.Length ?? 0)),
            out var bytesReturned,
            IntPtr.Zero);
        return CopyNativeResponse(succeeded, bytesReturned, outputBuffer, output);
    }

    internal static bool CopyNativeResponse(bool succeeded, uint bytesReturned, byte[]? outputBuffer, ulong[]? output)
    {
        int expected = checked((output?.Length ?? 0) * sizeof(ulong));
        if (!succeeded || bytesReturned != expected || (outputBuffer?.Length ?? 0) != expected) return false;
        if (expected > 0) Buffer.BlockCopy(outputBuffer!, 0, output!, 0, expected);
        return true;
    }

    private static byte[]? ReadRyzenSmuModule() => ReadEmbeddedModule("RyzenSMU.bin");

    private static byte[]? ReadEmbeddedModule(string fileName)
    {
        var assembly = typeof(Computer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return null;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        initialized = false;
        safeHandle?.Dispose();
        safeHandle = null;
        msrInitialized = false;
        msrSafeHandle?.Dispose();
        msrSafeHandle = null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint code,
        [In] byte[] input,
        uint inputSize,
        [Out] byte[]? output,
        uint outputSize,
        out uint returned,
        IntPtr overlapped);
}
