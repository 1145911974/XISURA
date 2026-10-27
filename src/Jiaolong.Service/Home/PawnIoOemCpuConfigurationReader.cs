using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Home;

// Raw OEM configuration bytes; dormant values are not effective CPU limits.
public sealed record OemCpuConfigurationSnapshot(
    [property: JsonRequired] DateTimeOffset CapturedAtUtc,
    [property: JsonRequired] byte CustomModeByte,
    [property: JsonRequired] byte SplWatts,
    [property: JsonRequired] byte FpptWatts,
    [property: JsonRequired] byte TemperatureLimitC,
    [property: JsonRequired] byte Itsm,
    [property: JsonRequired] byte Stsm);

public sealed class PawnIoOemCpuConfigurationReader
{
    internal const string ModuleName = "LibreHardwareMonitor.Resources.PawnIo.LpcACPIEC.bin";
    private const string ModuleHash = "C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906";
    private readonly Func<bool> accessAllowed;
    private readonly IPawnIoDeviceApi api;
    private readonly Func<byte[]?> readModule;
    private readonly Func<PawnIoLpcEcPorts.DriverIdentity?> readDriver;
    private readonly string mutexName;
    private readonly int timeoutMilliseconds;

    public PawnIoOemCpuConfigurationReader(Func<bool> accessAllowed)
        : this(accessAllowed, new PawnIoLpcEcPorts.NativeDeviceApi(),
            () => PawnIoLpcEcPorts.ReadEmbeddedModule(ModuleName), PawnIoLpcEcPorts.ReadRunningDriver,
            @"Global\Access_EC", 1500) { }

    internal PawnIoOemCpuConfigurationReader(Func<bool> accessAllowed, IPawnIoDeviceApi api,
        Func<byte[]?> readModule, Func<PawnIoLpcEcPorts.DriverIdentity?> readDriver,
        string mutexName, int timeoutMilliseconds = 100)
    {
        this.accessAllowed = accessAllowed;
        this.api = api;
        this.readModule = readModule;
        this.readDriver = readDriver;
        this.mutexName = mutexName;
        this.timeoutMilliseconds = timeoutMilliseconds;
    }

    public OemCpuConfigurationSnapshot Read(CancellationToken cancellationToken = default)
    {
        EnsureAccess(cancellationToken);
        if (!PawnIoLpcEcPorts.IsTrustedDriver(readDriver())) throw new IOException("oemCpuConfigurationDriverUntrusted");
        var module = readModule();
        if (module is null || Convert.ToHexString(SHA256.HashData(module)) != ModuleHash)
            throw new IOException("oemCpuConfigurationModuleUntrusted");
        // Cooperative Windows EC mutex; this does not acquire firmware ECMT or make the sample atomic.
        using var mutex = new Mutex(false, mutexName);
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(timeoutMilliseconds); }
            catch (AbandonedMutexException) { held = true; throw new IOException("oemCpuConfigurationMutexAbandoned"); }
            if (!held) throw new IOException("oemCpuConfigurationMutexUnavailable");
            EnsureAccess(cancellationToken);
            using var device = api.Open();
            if (device.IsInvalid || device.IsClosed) throw new IOException("oemCpuConfigurationOpenFailed");
            EnsureAccess(cancellationToken);
            if (!api.LoadModule(device, module)) throw new IOException("oemCpuConfigurationModuleLoadFailed");
            var first = ReadSample(device, cancellationToken);
            var second = ReadSample(device, cancellationToken);
            if (!first.AsSpan().SequenceEqual(second)) throw new IOException("oemCpuConfigurationChanged");
            EnsureAccess(cancellationToken);
            return new(DateTimeOffset.UtcNow, second[0], second[1], second[2], second[3], second[4], second[5]);
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private byte[] ReadSample(SafeFileHandle device, CancellationToken token)
    {
        byte[] values = new byte[6];
        byte[] offsets = [0xF0, 0xF6, 0xF7, 0xF8, 0xE4, 0xF4];
        for (int i = 0; i < offsets.Length; i++)
        {
            EnsureAccess(token);
            try
            {
                Wait(device, 2, false);
                if ((ReadPort(device, 0x66) & 1) != 0) throw new IOException("oemCpuConfigurationOutputPending");
                WritePort(device, 0x66, 0x92);
                Wait(device, 2, false);
                WritePort(device, 0x62, 0x84);
                Wait(device, 2, false);
                WritePort(device, 0x62, offsets[i]);
                Wait(device, 1, true);
                values[i] = ReadPort(device, 0x62);
            }
            finally { EnsureAccess(token); }
        }
        return values;
    }

    private void Wait(SafeFileHandle device, byte mask, bool set)
    {
        long start = Stopwatch.GetTimestamp();
        while (((ReadPort(device, 0x66) & mask) != 0) != set)
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= timeoutMilliseconds)
                throw new IOException("oemCpuConfigurationTimeout");
            Thread.Sleep(1);
        }
    }

    private byte ReadPort(SafeFileHandle device, ushort port)
    {
        ulong[] output = [0];
        if (!api.Execute(device, "ioctl_pio_read", [port], output) || output[0] > byte.MaxValue)
            throw new IOException("oemCpuConfigurationReadFailed");
        return (byte)output[0];
    }

    private void WritePort(SafeFileHandle device, ushort port, byte value)
    {
        if (!api.Execute(device, "ioctl_pio_write", [port, value], null))
            throw new IOException("oemCpuConfigurationAddressWriteFailed");
    }

    private void EnsureAccess(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!accessAllowed()) throw new IOException("hardwareAccessPaused");
    }
}
