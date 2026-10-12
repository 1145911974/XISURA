using System.Runtime.InteropServices;

namespace Jiaolong.Service.Home;

// The driver must accept these versioned layouts; unsupported responses fail before any write.
internal sealed class NvapiVfInterop : IDisposable
{
    internal readonly record struct PstateMemory(int OffsetKhz, int MinimumKhz, int MaximumKhz,
        int CorePstateOffsetKhz, int CoreVoltageDeltaUv, int StateCount, int ClockCount);
    private const int TableSize = 9248;
    private const int CurveSize = 7208;
    private const int MaskSize = 6188;
    private const int DeltaBase = 100;
    private const int DeltaStride = 36;
    private const int DeltaOffset = 24;
    private IntPtr library;
    private QueryInterface? query;
    private readonly Func<uint, Func<IntPtr, byte[], int>>? gpuCall;
    private readonly Func<IntPtr[]>? enumerateGpus;
    private readonly Func<IntPtr, GpuAdapterIdentity>? adapterIdentityReader;
    private readonly Func<bool>? hardwareAccessAllowed;
    private readonly Func<bool>? hardwareReadAllowed;
    internal sealed record LifetimeHooks(Func<int> Initialize, Func<int> Unload, Action FreeLibrary);
    private readonly LifetimeHooks? lifetimeHooks;
    private bool initialized;
    private bool libraryLoaded;
    private bool initializationUncertain;
    private bool unloadUncertain;
    private bool libraryReleaseUncertain;
    private bool disposed;
    public bool IsDisposed => disposed;
    public bool DisposalRequested { get; private set; }
    public Exception? CleanupError { get; private set; }
    public bool InitializationUncertain => initializationUncertain;
    public bool UnloadUncertain => unloadUncertain;
    public bool LibraryReleaseUncertain => libraryReleaseUncertain;
    public bool WriteAttempted { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumGpus([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GpuData(IntPtr gpu, IntPtr data);

    public NvapiVfInterop(Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null)
    {
        this.hardwareAccessAllowed = hardwareAccessAllowed;
        this.hardwareReadAllowed = hardwareReadAllowed;
    }

    internal NvapiVfInterop(Func<uint, Func<IntPtr, byte[], int>> gpuCall, Func<IntPtr[]> enumerateGpus,
        Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null, LifetimeHooks? lifetimeHooks = null,
        Func<IntPtr, GpuAdapterIdentity>? adapterIdentityReader = null)
    {
        this.gpuCall = gpuCall;
        this.enumerateGpus = enumerateGpus;
        this.hardwareAccessAllowed = hardwareAccessAllowed;
        this.hardwareReadAllowed = hardwareReadAllowed;
        this.lifetimeHooks = lifetimeHooks;
        this.adapterIdentityReader = adapterIdentityReader;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PciIdentifiers(IntPtr gpu,
        out uint deviceId, out uint subsystemId, out uint revisionId, out uint externalDeviceId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GpuScalar(IntPtr gpu, out uint value);

    internal GpuAdapterIdentity ReadAdapterIdentity(IntPtr gpu)
    {
        EnsureAccess(readOnly: true);
        EnsureInitialized();
        GpuAdapterIdentity identity;
        try
        {
            if (gpuCall is not null)
            {
                EnsureAccess(readOnly: true);
                identity = adapterIdentityReader?.Invoke(gpu) ?? throw new IOException("gpuAdapterIdentityUnavailable");
            }
            else
            {
                // Documented scalar read APIs; retain raw IDs, never persist an ephemeral GPU handle.
                var pci = Get<PciIdentifiers>(0x2DDFB66E);
                EnsureAccess(readOnly: true);
                Check(pci(gpu, out uint device, out uint subsystem, out uint revision, out uint external), "GetPCIIdentifiers");
                EnsureAccess(readOnly: true);
                var readBus = Get<GpuScalar>(0x1BE0B8E5);
                EnsureAccess(readOnly: true);
                Check(readBus(gpu, out uint bus), "GetBusId");
                EnsureAccess(readOnly: true);
                var readSlot = Get<GpuScalar>(0x2A0A350F);
                EnsureAccess(readOnly: true);
                Check(readSlot(gpu, out uint slot), "GetBusSlotId");
                identity = new(device, subsystem, revision, external, bus, slot);
            }
        }
        finally { EnsureAccess(readOnly: true); }
        if (!ControlExecutionJournal.ValidGpuAdapter(identity)) throw new InvalidDataException("gpuAdapterIdentityUnavailable");
        return identity;
    }

    private void EnsureInitialized()
    {
        if (initializationUncertain) throw new IOException("nvapiInitializationRecoveryRequired");
        if (disposed || DisposalRequested) throw new ObjectDisposedException(nameof(NvapiVfInterop));
        if (initialized) return;
        EnsureAccess(readOnly: true);
        if (!libraryLoaded)
        {
            try
            {
                if (gpuCall is null) library = NativeLibrary.Load("nvapi64.dll");
                libraryLoaded = true;
            }
            finally { EnsureAccess(readOnly: true); }
        }
        Func<int> initialize;
        if (gpuCall is not null) initialize = lifetimeHooks?.Initialize ?? (() => 0);
        else
        {
            query ??= Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(library, "nvapi_QueryInterface"));
            initialize = Get<Initialize>(0x0150E828).Invoke;
        }
        EnsureAccess(readOnly: true);
        // Only confirmed success owns a ref-counter increment. An ambiguous failure must not unload another client.
        initializationUncertain = true;
        try
        {
            Check(initialize(), "NvAPI_Initialize");
            initialized = true;
            initializationUncertain = false;
        }
        catch (Exception error) { CleanupError = error; throw; }
        finally { EnsureAccess(readOnly: true); }
    }

    private bool IsAccessAllowed(bool readOnly) =>
        (readOnly ? hardwareReadAllowed ?? hardwareAccessAllowed : hardwareAccessAllowed)?.Invoke() ?? true;

    private void EnsureAccess(bool readOnly)
    {
        if (!IsAccessAllowed(readOnly)) throw new IOException("hardwareAccessPaused");
    }

    public (IntPtr Gpu, byte[] Mask) OpenSingleGpu()
    {
        EnsureAccess(readOnly: true);
        EnsureInitialized();
        var handles = new IntPtr[64];
        int count;
        try
        {
            if (enumerateGpus is not null) { handles = enumerateGpus(); count = handles.Length; }
            else
            {
                var enumerate = Get<EnumGpus>(3853292063u);
                EnsureAccess(readOnly: true);
                Check(enumerate(handles, out count), "EnumPhysicalGPUs");
            }
        }
        finally { EnsureAccess(readOnly: true); }
        if (count != 1) throw new InvalidOperationException($"Expected one NVIDIA GPU, found {count}");
        return (handles[0], ReadRaw(handles[0], 1350257497u, MaskSize, [])[4..36]);
    }

    public (int VoltageMv, int BaseMhz)[] ReadCurve(IntPtr gpu, byte[] mask)
    {
        var bytes = ReadRaw(gpu, 559119060u, CurveSize, mask);
        var nodes = new List<(int, int)>();
        int previousMv = 0;
        for (int offset = 64; offset + 28 <= bytes.Length && BitConverter.ToInt32(bytes, offset + 4) == 0; offset += 28)
        {
            int mv = BitConverter.ToInt32(bytes, offset + 12) / 1000;
            int mhz = BitConverter.ToInt32(bytes, offset + 8) / 1000;
            if (mv <= previousMv || mv is < 300 or > 1300 || mhz < 0) break;
            nodes.Add((mv, mhz));
            previousMv = mv;
        }
        return nodes.ToArray();
    }

    public int[] ReadDeltas(IntPtr gpu, byte[] mask, int count)
    {
        var bytes = ReadRaw(gpu, 603042099u, TableSize, mask);
        return Enumerable.Range(0, count).Select(index => index == 0 ? 0 : BitConverter.ToInt32(bytes, DeltaBase + (index - 1) * DeltaStride + DeltaOffset)).ToArray();
    }


    internal sealed record PowerPolicy(uint MinimumMilliPercent, uint MaximumMilliPercent,
        uint DefaultMilliPercent, uint CurrentMilliPercent, byte[] Status);

    public PowerPolicy ReadPowerPolicy(IntPtr gpu)
    {
        // V1 info: 8-byte header + four 44-byte entries. Status: 8 + four 16-byte entries.
        var info = ReadRaw(gpu, 0x34206D86, 184, []);
        var status = ReadRaw(gpu, 0x70916171, 72, []);
        if (info[4] != 1 || info[5] != 1 || BitConverter.ToUInt32(info, 8) != 0 ||
            BitConverter.ToUInt32(status, 4) != 1) throw new InvalidDataException("Unsupported GPU power policy layout");
        uint minimum = BitConverter.ToUInt32(info, 20), normal = BitConverter.ToUInt32(info, 32),
            maximum = BitConverter.ToUInt32(info, 44), current = BitConverter.ToUInt32(status, 16);
        if (minimum < 1000 || maximum > 200_000 || minimum > maximum ||
            normal < minimum || normal > maximum || current < minimum || current > maximum)
            throw new InvalidDataException("Invalid GPU power policy range");
        return new(minimum, maximum, normal, current, status);
    }

    public void SetPowerPolicy(IntPtr gpu, PowerPolicy before, uint milliPercent)
    {
        if (milliPercent < before.MinimumMilliPercent || milliPercent > before.MaximumMilliPercent)
            throw new ArgumentOutOfRangeException(nameof(milliPercent));
        var bytes = before.Status.ToArray();
        BitConverter.GetBytes(milliPercent).CopyTo(bytes, 16);
        Call(gpu, 0xAD95F5ED, bytes, write: true);
    }

    public bool HasPowerPolicySetter
    {
        get
        {
            EnsureAccess(readOnly: true);
            try { EnsureInitialized(); return gpuCall is not null || query!(0xAD95F5ED) != IntPtr.Zero; }
            finally { EnsureAccess(readOnly: true); }
        }
    }

    public void SetPowerPolicyWithoutReadback(IntPtr gpu, uint milliPercent)
    {
        if (milliPercent is < 20_000 or > 100_000 || milliPercent % 1000 != 0)
            throw new ArgumentOutOfRangeException(nameof(milliPercent));
        // Same-model console uses V1, one policy, and zero reserved fields. No watts conversion.
        var bytes = new byte[72];
        BitConverter.GetBytes((uint)(72 | 1 << 16)).CopyTo(bytes, 0);
        BitConverter.GetBytes(1u).CopyTo(bytes, 4);
        BitConverter.GetBytes(milliPercent).CopyTo(bytes, 16);
        Call(gpu, 0xAD95F5ED, bytes, write: true);
    }

    public (int MinimumKhz, int MaximumKhz)? ReadGraphicsDeltaRange(IntPtr gpu)
    {
        var bytes = ReadRaw(gpu, 1689533034u, 2344, []);
        for (int offset = 40; offset + 72 <= bytes.Length; offset += 72)
            if (BitConverter.ToInt32(bytes, offset + 4) == 0 && BitConverter.ToInt32(bytes, offset + 40) != 0)
                return (BitConverter.ToInt32(bytes, offset + 44), BitConverter.ToInt32(bytes, offset + 40));
        return null;
    }

    public PstateMemory ReadPstate0MemoryOffset(IntPtr gpu)
    {
        const int stateStride = 456;
        const int clockStride = 44;
        var bytes = ReadRaw(gpu, 1878528531u, 20 + 16 * stateStride, []);
        int states = BitConverter.ToInt32(bytes, 8);
        int clocks = BitConverter.ToInt32(bytes, 12);
        if (states is < 1 or > 16 || clocks is < 1 or > 8) throw new InvalidDataException("Unexpected PStates20 header");
        for (int state = 0; state < states; state++)
        {
            int stateStart = 20 + state * stateStride;
            if (BitConverter.ToInt32(bytes, stateStart) != 0) continue;
            for (int clock = 0; clock < clocks; clock++)
            {
                int entry = stateStart + 8 + clock * clockStride;
                if (BitConverter.ToInt32(bytes, entry) != 4) continue;
                int coreOffset = 0;
                for (int otherClock = 0; otherClock < clocks; otherClock++)
                {
                    int otherEntry = stateStart + 8 + otherClock * clockStride;
                    if (BitConverter.ToInt32(bytes, otherEntry) == 0)
                        coreOffset = BitConverter.ToInt32(bytes, otherEntry + 12);
                }
                int voltageEntry = stateStart + 8 + 8 * clockStride;
                int voltageDelta = BitConverter.ToInt32(bytes, voltageEntry) == 0
                    ? BitConverter.ToInt32(bytes, voltageEntry + 12) : 0;
                return new PstateMemory(BitConverter.ToInt32(bytes, entry + 12),
                    BitConverter.ToInt32(bytes, entry + 16), BitConverter.ToInt32(bytes, entry + 20),
                    coreOffset, voltageDelta, states, clocks);
            }
        }
        throw new InvalidDataException("P0 memory clock domain absent");
    }

    public void WritePstate0MemoryOffset(IntPtr gpu, PstateMemory before, int targetKhz)
        => WritePstate0Offsets(gpu, before, before.CorePstateOffsetKhz, targetKhz);

    public void WritePstate0Offsets(IntPtr gpu, PstateMemory before, int coreKhz, int memoryKhz)
    {
        const int clockStride = 44;
        var bytes = new byte[20 + 16 * 456];
        BitConverter.GetBytes(bytes.Length | (1 << 16)).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 8);
        BitConverter.GetBytes(2).CopyTo(bytes, 12);
        BitConverter.GetBytes(1).CopyTo(bytes, 16);
        int graphics = 20 + 8;
        BitConverter.GetBytes(coreKhz).CopyTo(bytes, graphics + 12);
        int memory = graphics + clockStride;
        BitConverter.GetBytes(4).CopyTo(bytes, memory);
        BitConverter.GetBytes(memoryKhz).CopyTo(bytes, memory + 12);
        int coreVoltage = graphics + 8 * clockStride;
        BitConverter.GetBytes(before.CoreVoltageDeltaUv).CopyTo(bytes, coreVoltage + 12);
        Call(gpu, 256749163u, bytes, write: true);
    }

    public void WriteDeltas(IntPtr gpu, byte[] mask, int[] deltas)
    {
        EnsureAccess(readOnly: false);
        if (deltas.Length != 127) throw new ArgumentOutOfRangeException(nameof(deltas));
        var bytes = ReadRaw(gpu, 603042099u, TableSize, mask, readOnly: false);
        for (int index = 1; index < deltas.Length; index++)
            BitConverter.GetBytes(deltas[index]).CopyTo(bytes, DeltaBase + (index - 1) * DeltaStride + DeltaOffset);
        BitConverter.GetBytes(TableSize | (1 << 16)).CopyTo(bytes, 0);
        mask.CopyTo(bytes, 4);
        Call(gpu, 120840201u, bytes, write: true);
    }

    private byte[] ReadRaw(IntPtr gpu, uint function, int size, byte[] mask, bool readOnly = true)
    {
        var bytes = new byte[size];
        BitConverter.GetBytes(size | (1 << 16)).CopyTo(bytes, 0);
        mask.CopyTo(bytes, 4);
        Call(gpu, function, bytes, requireWriteAccess: !readOnly);
        return bytes;
    }

    private void Call(IntPtr gpu, uint function, byte[] bytes, bool write = false, bool requireWriteAccess = false)
    {
        bool readOnly = !write && !requireWriteAccess;
        EnsureAccess(readOnly);
        EnsureInitialized();
        if (gpuCall is not null)
        {
            try
            {
                var call = gpuCall(function);
                EnsureAccess(readOnly);
                if (write) WriteAttempted = true;
                Check(call(gpu, bytes), $"NVAPI 0x{function:X8}");
            }
            finally { EnsureAccess(readOnly); }
            return;
        }
        IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var call = Get<GpuData>(function);
            EnsureAccess(readOnly);
            if (write) WriteAttempted = true;
            try { Check(call(gpu, memory), $"NVAPI 0x{function:X8}"); }
            finally { EnsureAccess(readOnly); }
            Marshal.Copy(memory, bytes, 0, bytes.Length);
        }
        finally { try { Marshal.FreeHGlobal(memory); } finally { EnsureAccess(readOnly); } }
    }

    private T Get<T>(uint id) where T : Delegate
    {
        IntPtr address = query!(id);
        if (address == IntPtr.Zero) throw new InvalidOperationException($"NVAPI function unavailable: 0x{id:X8}");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new NvapiCallException(status, operation);
    }

    internal void RequestDisposal() => DisposalRequested = true;
    internal bool TryDispose()
    {
        DisposalRequested = true;
        if (disposed) return true;
        if (initializationUncertain || unloadUncertain || libraryReleaseUncertain) return false;
        try
        {
            EnsureAccess(readOnly: false);
            if (initialized)
            {
                Func<int> unload = gpuCall is not null ? lifetimeHooks?.Unload ?? (() => 0)
                    : Get<Initialize>(0xD22BDD7E).Invoke;
                EnsureAccess(readOnly: false);
                unloadUncertain = true;
                int status = unload();
                unloadUncertain = false;
                Check(status, "NvAPI_Unload");
                // Record completed unload before checking permission again; it must not decrement twice.
                initialized = false;
                EnsureAccess(readOnly: false);
            }
            if (libraryLoaded)
            {
                EnsureAccess(readOnly: false);
                libraryReleaseUncertain = true;
                if (gpuCall is not null) lifetimeHooks?.FreeLibrary();
                else NativeLibrary.Free(library);
                library = IntPtr.Zero;
                libraryLoaded = false;
                libraryReleaseUncertain = false;
            }
            disposed = true;
            CleanupError = null;
            return true;
        }
        catch (Exception error) { CleanupError = error; return false; }
    }
    public void Dispose() => TryDispose();
}

internal sealed class NvapiCallException(int status, string operation) : InvalidOperationException($"{operation} status={status}")
{
    public int Status { get; } = status;
}
