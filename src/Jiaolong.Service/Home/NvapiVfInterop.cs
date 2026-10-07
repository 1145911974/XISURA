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
    private readonly IntPtr library;
    private readonly QueryInterface query;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumGpus([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GpuData(IntPtr gpu, IntPtr data);

    public NvapiVfInterop()
    {
        library = NativeLibrary.Load("nvapi64.dll");
        query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(library, "nvapi_QueryInterface"));
        Check(Get<Initialize>(22079528u)(), "NvAPI_Initialize");
    }

    public (IntPtr Gpu, byte[] Mask) OpenSingleGpu()
    {
        var handles = new IntPtr[64];
        Check(Get<EnumGpus>(3853292063u)(handles, out var count), "EnumPhysicalGPUs");
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
        Call(gpu, 0xAD95F5ED, bytes);
    }

    public bool HasPowerPolicySetter => query(0xAD95F5ED) != IntPtr.Zero;

    public void SetPowerPolicyWithoutReadback(IntPtr gpu, uint milliPercent)
    {
        if (milliPercent is < 20_000 or > 100_000 || milliPercent % 1000 != 0)
            throw new ArgumentOutOfRangeException(nameof(milliPercent));
        // Same-model console uses V1, one policy, and zero reserved fields. No watts conversion.
        var bytes = new byte[72];
        BitConverter.GetBytes((uint)(72 | 1 << 16)).CopyTo(bytes, 0);
        BitConverter.GetBytes(1u).CopyTo(bytes, 4);
        BitConverter.GetBytes(milliPercent).CopyTo(bytes, 16);
        Call(gpu, 0xAD95F5ED, bytes);
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
        int states = Math.Min(BitConverter.ToInt32(bytes, 8), 16);
        int clocks = Math.Min(BitConverter.ToInt32(bytes, 12), 8);
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
        Call(gpu, 256749163u, bytes);
    }

    public void WriteDeltas(IntPtr gpu, byte[] mask, int[] deltas)
    {
        if (deltas.Length != 127) throw new ArgumentOutOfRangeException(nameof(deltas));
        var bytes = ReadRaw(gpu, 603042099u, TableSize, mask);
        for (int index = 1; index < deltas.Length; index++)
            BitConverter.GetBytes(deltas[index]).CopyTo(bytes, DeltaBase + (index - 1) * DeltaStride + DeltaOffset);
        BitConverter.GetBytes(TableSize | (1 << 16)).CopyTo(bytes, 0);
        mask.CopyTo(bytes, 4);
        Call(gpu, 120840201u, bytes);
    }

    private byte[] ReadRaw(IntPtr gpu, uint function, int size, byte[] mask)
    {
        var bytes = new byte[size];
        BitConverter.GetBytes(size | (1 << 16)).CopyTo(bytes, 0);
        mask.CopyTo(bytes, 4);
        Call(gpu, function, bytes);
        return bytes;
    }

    private void Call(IntPtr gpu, uint function, byte[] bytes)
    {
        IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Check(Get<GpuData>(function)(gpu, memory), $"NVAPI 0x{function:X8}");
            Marshal.Copy(memory, bytes, 0, bytes.Length);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private T Get<T>(uint id) where T : Delegate
    {
        IntPtr address = query(id);
        if (address == IntPtr.Zero) throw new InvalidOperationException($"NVAPI function unavailable: 0x{id:X8}");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new NvapiCallException(status, operation);
    }

    public void Dispose() => NativeLibrary.Free(library);
}

internal sealed class NvapiCallException(int status, string operation) : InvalidOperationException($"{operation} status={status}")
{
    public int Status { get; } = status;
}
