using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length is < 1 or > 2) throw new ArgumentException("Usage: GpuVfProbe <C: Temp result.json> [--trial|--memory-trial|--core-trial|--boost-trial]");
var output = Path.GetFullPath(args[0]);
if (!output.StartsWith(@"C:\Users\Administrator\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Result must be on the approved C: temporary path.");
string trialKind = args.Length == 1 ? "none" : args[1] switch
{
    "--trial" => "vf", "--memory-trial" => "memory", "--core-trial" => "core", "--boost-trial" => "boost", _ => throw new ArgumentException("Unknown argument.")
};
var report = new Dictionary<string, object?> { ["trialRequested"] = trialKind, ["capturedAtUtc"] = DateTimeOffset.UtcNow };
try
{
    using var nvapi = new NvVf();
    var (name, temperature) = ReadGpuIdentityAndTemperature();
    report["gpu"] = name;
    report["temperatureBeforeC"] = temperature;
    if (!name.Contains("RTX 4070 Laptop GPU", StringComparison.Ordinal)) throw new InvalidOperationException("GPU mismatch");
    var (gpu, mask) = nvapi.OpenSingleGpu();
    report["architectureId"] = nvapi.ReadArchitectureId(gpu);
    var deltaRange = nvapi.ReadGraphicsDeltaRange(gpu);
    report["graphicsDeltaRangeRaw"] = deltaRange is { } range ? new { range.RawMax, range.RawMin } : null;
    try { report["voltageBoostPercent"] = nvapi.ReadVoltageBoostPercent(gpu); }
    catch (Exception exception) { report["voltageBoostError"] = exception.Message; }
    try { report["pstate0Memory"] = nvapi.ReadPstate0MemoryOffset(gpu); }
    catch (Exception exception) { report["pstate0MemoryError"] = exception.Message; }
    try { report["pstate0Voltages"] = nvapi.ReadPstate0Voltages(gpu); }
    catch (Exception exception) { report["pstate0VoltageError"] = exception.Message; }
    var curve = nvapi.ReadCurve(gpu, mask);
    var before = nvapi.ReadDeltas(gpu, mask, curve.Count);
    if (curve.Count != 127 || before.Length != 127 || curve[49].Mv is < 300 or > 1300)
        throw new InvalidOperationException($"Unexpected V/F layout: {curve.Count} nodes");
    report["nodeCount"] = curve.Count;
    report["node50Mv"] = curve[49].Mv;
    report["node50BaseMhz"] = curve[49].Mhz;
    report["node50BeforeRaw"] = before[49];
    report["beforeHash"] = Hash(before);
    Save();
    if (trialKind != "none")
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("GPU write requires an elevated process");
        PowerGuard.EnsureAc();
        if (temperature is < 0 or >= 75) throw new InvalidOperationException("GPU thermal guard refused trial");
    }
    if (trialKind == "memory")
    {
        var original = nvapi.ReadPstate0MemoryOffset(gpu);
        int target = checked(original.OffsetKhz + 10_000);
        if (target > original.MaximumKhz || target < original.MinimumKhz) throw new InvalidOperationException("Memory offset trial outside driver range");
        try
        {
            nvapi.WritePstate0MemoryOffset(gpu, original, target);
            var during = nvapi.ReadPstate0MemoryOffset(gpu);
            report["memoryDuring"] = during;
            Save();
            if (during.OffsetKhz != target || during.CorePstateOffsetKhz != original.CorePstateOffsetKhz ||
                during.CoreVoltageDeltaUv != original.CoreVoltageDeltaUv || !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before))
                throw new InvalidOperationException("Memory offset or adjacent field readback mismatch");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            PowerGuard.EnsureAc();
            report["temperatureDuringC"] = ReadGpuIdentityAndTemperature().Temperature;
            if ((int)report["temperatureDuringC"]! >= 80) throw new InvalidOperationException("GPU thermal guard stopped memory trial");
        }
        finally
        {
            bool restored = false;
            for (int attempt = 0; attempt < 3 && !restored; attempt++)
            {
                try
                {
                    var current = nvapi.ReadPstate0MemoryOffset(gpu);
                    if (current.OffsetKhz != original.OffsetKhz) nvapi.WritePstate0MemoryOffset(gpu, original, original.OffsetKhz);
                    if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before)) nvapi.WriteDeltas(gpu, mask, before);
                    restored = nvapi.ReadPstate0MemoryOffset(gpu).Equals(original) && nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before);
                }
                catch (Exception error) { report["recoveryError"] = error.Message; }
            }
            report["restored"] = restored;
            Save();
            if (!restored) throw new InvalidOperationException("GPU memory offset restore mismatch");
        }
    }
    if (trialKind == "core")
    {
        var original = nvapi.ReadPstate0MemoryOffset(gpu);
        var coreRange = nvapi.ReadGraphicsDeltaRange(gpu) ?? throw new InvalidOperationException("Core offset driver range unavailable");
        int target = checked(original.CorePstateOffsetKhz + 10_000);
        if (target > coreRange.RawMax || target < coreRange.RawMin || target > 200_000)
            throw new InvalidOperationException("Core offset trial outside driver or software range");
        try
        {
            nvapi.WritePstate0Offsets(gpu, original, target, original.OffsetKhz);
            var during = nvapi.ReadPstate0MemoryOffset(gpu);
            var duringCurve = nvapi.ReadDeltas(gpu, mask, 127);
            report["coreDuring"] = during;
            report["coreCurveChanges"] = Enumerable.Range(0, 127)
                .Where(index => before[index] != duringCurve[index])
                .Select(index => new { Index = index, BeforeKhz = before[index], DuringKhz = duringCurve[index] })
                .ToArray();
            Save();
            int shift = target - original.CorePstateOffsetKhz;
            if (during != (original with { CorePstateOffsetKhz = target }) ||
                Enumerable.Range(0, 127).Any(index =>
                    duringCurve[index] != before[index] + (index == 0 ? 0 : shift)))
                throw new InvalidOperationException("Core offset or adjacent field readback mismatch");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            PowerGuard.EnsureAc();
            report["temperatureDuringC"] = ReadGpuIdentityAndTemperature().Temperature;
            if ((int)report["temperatureDuringC"]! >= 75) throw new InvalidOperationException("GPU thermal guard stopped core trial");
        }
        finally
        {
            bool restored = false;
            for (int attempt = 0; attempt < 3 && !restored; attempt++)
            {
                try
                {
                    if (nvapi.ReadPstate0MemoryOffset(gpu) != original)
                        nvapi.WritePstate0Offsets(gpu, original, original.CorePstateOffsetKhz, original.OffsetKhz);
                    if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before)) nvapi.WriteDeltas(gpu, mask, before);
                    restored = nvapi.ReadPstate0MemoryOffset(gpu) == original &&
                        nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before);
                }
                catch (Exception error) { report["recoveryError"] = error.Message; }
            }
            report["restored"] = restored;
            Save();
            if (!restored) throw new InvalidOperationException("GPU core offset restore mismatch");
        }
    }
    if (trialKind == "boost")
    {
        uint original = nvapi.ReadVoltageBoostPercent(gpu);
        uint target = checked(original + 1);
        if (target > 100) throw new InvalidOperationException("Voltage boost trial outside bounded range");
        try
        {
            nvapi.WriteVoltageBoostPercent(gpu, target);
            report["boostDuringPercent"] = nvapi.ReadVoltageBoostPercent(gpu);
            Save();
            if ((uint)report["boostDuringPercent"]! != target || !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before))
                throw new InvalidOperationException("Voltage boost or V/F readback mismatch");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            PowerGuard.EnsureAc();
            report["temperatureDuringC"] = ReadGpuIdentityAndTemperature().Temperature;
            if ((int)report["temperatureDuringC"]! >= 80) throw new InvalidOperationException("GPU thermal guard stopped boost trial");
        }
        finally
        {
            bool restored = false;
            for (int attempt = 0; attempt < 3 && !restored; attempt++)
            {
                try
                {
                    if (nvapi.ReadVoltageBoostPercent(gpu) != original) nvapi.WriteVoltageBoostPercent(gpu, original);
                    restored = nvapi.ReadVoltageBoostPercent(gpu) == original && nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before);
                }
                catch (Exception error) { report["recoveryError"] = error.Message; }
            }
            report["restored"] = restored;
            Save();
            if (!restored) throw new InvalidOperationException("GPU voltage boost restore mismatch");
        }
    }
    if (trialKind == "vf")
    {
        var target = (int[])before.Clone();
        target[49] = checked(target[49] + 15_000);
        if (target[49] is < -500_000 or > 500_000) throw new InvalidOperationException("Node offset outside bounded trial range");
        bool writeAttempted = false;
        try
        {
            writeAttempted = true;
            nvapi.WriteDeltas(gpu, mask, target);
            var during = nvapi.ReadDeltas(gpu, mask, curve.Count);
            report["node50DuringRaw"] = during[49];
            report["duringHash"] = Hash(during);
            report["changedNodes"] = during.Select((value, index) => value != before[index] ? index : -1).Where(index => index >= 0).ToArray();
            if (!during.SequenceEqual(target)) throw new InvalidOperationException("GPU V/F readback mismatch");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            PowerGuard.EnsureAc();
            report["temperatureDuringC"] = ReadGpuIdentityAndTemperature().Temperature;
            if ((int)report["temperatureDuringC"]! >= 80) throw new InvalidOperationException("GPU thermal guard stopped V/F trial");
        }
        finally
        {
            if (writeAttempted)
            {
                Exception? recoveryError = null;
                bool restored = false;
                try
                {
                    var current = nvapi.ReadDeltas(gpu, mask, curve.Count);
                    restored = current.SequenceEqual(before);
                    report["restoredHash"] = Hash(current);
                }
                catch (Exception error) { recoveryError = error; }
                for (int attempt = 0; attempt < 3 && !restored; attempt++)
                {
                    try
                    {
                        nvapi.WriteDeltas(gpu, mask, before);
                        var readBack = nvapi.ReadDeltas(gpu, mask, curve.Count);
                        report["restoredHash"] = Hash(readBack);
                        restored = readBack.SequenceEqual(before);
                    }
                    catch (Exception error) { recoveryError = error; }
                }
                report["restored"] = restored;
                Save();
                if (!restored) throw new InvalidOperationException("GPU V/F restore mismatch", recoveryError);
            }
        }
    }
    Save();
}
catch (Exception error)
{
    report["error"] = error.ToString();
    Save();
    Environment.ExitCode = 1;
}

void Save() => File.WriteAllText(output, JsonSerializer.Serialize(report));
static string Hash(int[] values)
{
    var bytes = new byte[values.Length * sizeof(int)];
    Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
    return Convert.ToHexString(SHA256.HashData(bytes));
}
static (string Name, int Temperature) ReadGpuIdentityAndTemperature()
{
    using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,temperature.gpu --format=csv,noheader")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("nvidia-smi unavailable");
    string output = process.StandardOutput.ReadToEnd().Trim();
    if (!process.WaitForExit(3000) || process.ExitCode != 0) throw new InvalidOperationException("nvidia-smi temperature unavailable");
    var fields = output.Split(',', StringSplitOptions.TrimEntries);
    if (fields.Length != 2 || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int temperature))
        throw new InvalidOperationException("GPU temperature parse failed");
    return (fields[0], temperature);
}

internal readonly record struct PstateMemory(int OffsetKhz, int MinimumKhz, int MaximumKhz,
    int CorePstateOffsetKhz, int CoreVoltageDeltaUv, int States, int Clocks);

internal static class PowerGuard
{
    internal static void EnsureAc()
    {
        if (!GetSystemPowerStatus(out var status) || status.AcLineStatus != 1)
            throw new InvalidOperationException("GPU trial requires confirmed AC power");
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}

internal sealed class NvVf : IDisposable
{
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

    public NvVf()
    {
        library = NativeLibrary.Load("nvapi64.dll");
        query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(library, "nvapi_QueryInterface"));
        Check(Get<Initialize>(22079528u)(), "NvAPI_Initialize");
    }

    public (IntPtr Gpu, byte[] Mask) OpenSingleGpu()
    {
        var handles = new IntPtr[64];
        Check(Get<EnumGpus>(3853292063u)(handles, out var count), "EnumPhysicalGPUs");
        if (count != 1) throw new InvalidOperationException($"Expected one physical NVIDIA GPU, found {count}");
        return (handles[0], ReadRaw(handles[0], 1350257497u, MaskSize, [])[4..36]);
    }

    public uint ReadArchitectureId(IntPtr gpu) => BitConverter.ToUInt32(ReadRaw(gpu, 3626392868u, 16, [], version: 2), 4);

    public (int RawMax, int RawMin)? ReadGraphicsDeltaRange(IntPtr gpu)
    {
        var bytes = ReadRaw(gpu, 1689533034u, 2344, []);
        for (int offset = 40; offset + 72 <= bytes.Length; offset += 72)
            if (BitConverter.ToInt32(bytes, offset + 4) == 0 && BitConverter.ToInt32(bytes, offset + 40) != 0)
                return (BitConverter.ToInt32(bytes, offset + 40), BitConverter.ToInt32(bytes, offset + 44));
        return null;
    }

    public uint ReadVoltageBoostPercent(IntPtr gpu) => BitConverter.ToUInt32(ReadRaw(gpu, 2649898145u, 40, []), 4);

    public void WriteVoltageBoostPercent(IntPtr gpu, uint percent)
    {
        var bytes = ReadRaw(gpu, 2649898145u, 40, []);
        BitConverter.GetBytes(percent).CopyTo(bytes, 4);
        Call(gpu, 3106958747u, bytes);
    }

    public IReadOnlyList<object> ReadPstate0Voltages(IntPtr gpu)
    {
        var bytes = ReadRaw(gpu, 1878528531u, 20 + 16 * 456, []);
        int states = BitConverter.ToInt32(bytes, 8);
        int voltages = BitConverter.ToInt32(bytes, 16);
        if (states is < 1 or > 16 || voltages is < 0 or > 4) throw new InvalidDataException("Unexpected PStates20 voltage header");
        var entries = new List<object>();
        for (int state = 0; state < states; state++)
        {
            int start = 20 + state * 456;
            if (BitConverter.ToInt32(bytes, start) != 0) continue;
            for (int index = 0; index < voltages; index++)
            {
                int entry = start + 8 + 8 * 44 + index * 24;
                entries.Add(new
                {
                    domain = BitConverter.ToUInt32(bytes, entry),
                    editable = (BitConverter.ToUInt32(bytes, entry + 4) & 1) != 0,
                    voltageUv = BitConverter.ToUInt32(bytes, entry + 8),
                    deltaUv = BitConverter.ToInt32(bytes, entry + 12),
                    minimumDeltaUv = BitConverter.ToInt32(bytes, entry + 16),
                    maximumDeltaUv = BitConverter.ToInt32(bytes, entry + 20)
                });
            }
        }
        return entries;
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
        const int stateStride = 456;
        const int clockStride = 44;
        var bytes = new byte[20 + 16 * stateStride];
        BitConverter.GetBytes(bytes.Length | (1 << 16)).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 8);  // one P0 state
        BitConverter.GetBytes(2).CopyTo(bytes, 12); // graphics and memory domains
        BitConverter.GetBytes(1).CopyTo(bytes, 16); // core voltage domain
        int graphics = 20 + 8;
        BitConverter.GetBytes(coreKhz).CopyTo(bytes, graphics + 12);
        int memory = graphics + clockStride;
        BitConverter.GetBytes(4).CopyTo(bytes, memory);
        BitConverter.GetBytes(memoryKhz).CopyTo(bytes, memory + 12);
        int coreVoltage = graphics + 8 * clockStride;
        BitConverter.GetBytes(before.CoreVoltageDeltaUv).CopyTo(bytes, coreVoltage + 12);
        Call(gpu, 256749163u, bytes);
    }

    public List<(int Mv, int Mhz)> ReadCurve(IntPtr gpu, byte[] mask)
    {
        var bytes = ReadRaw(gpu, 559119060u, CurveSize, mask);
        var nodes = new List<(int Mv, int Mhz)>();
        int previousMv = 0;
        for (int offset = 64; offset + 28 <= bytes.Length && BitConverter.ToInt32(bytes, offset + 4) == 0; offset += 28)
        {
            int mv = BitConverter.ToInt32(bytes, offset + 12) / 1000;
            int mhz = BitConverter.ToInt32(bytes, offset + 8) / 1000;
            if (mv <= previousMv || mv is < 300 or > 1300 || mhz < 0) break;
            nodes.Add((mv, mhz));
            previousMv = mv;
        }
        return nodes;
    }

    public int[] ReadDeltas(IntPtr gpu, byte[] mask, int count)
    {
        var bytes = ReadRaw(gpu, 603042099u, TableSize, mask);
        return Enumerable.Range(0, count).Select(index => index == 0 ? 0 : BitConverter.ToInt32(bytes, DeltaBase + (index - 1) * DeltaStride + DeltaOffset)).ToArray();
    }

    public void WriteDeltas(IntPtr gpu, byte[] mask, int[] deltas)
    {
        var bytes = ReadRaw(gpu, 603042099u, TableSize, mask);
        for (int index = 1; index < deltas.Length; index++)
            BitConverter.GetBytes(deltas[index]).CopyTo(bytes, DeltaBase + (index - 1) * DeltaStride + DeltaOffset);
        BitConverter.GetBytes(TableSize | (1 << 16)).CopyTo(bytes, 0);
        mask.CopyTo(bytes, 4);
        Call(gpu, 120840201u, bytes);
    }

    private byte[] ReadRaw(IntPtr gpu, uint function, int size, byte[] mask, int version = 1)
    {
        var bytes = new byte[size];
        BitConverter.GetBytes(size | (version << 16)).CopyTo(bytes, 0);
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
        if (status != 0) throw new InvalidOperationException($"{operation} status={status}");
    }
    public void Dispose() => NativeLibrary.Free(library);
}
