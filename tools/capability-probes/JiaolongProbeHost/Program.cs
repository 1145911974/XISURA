using System.Reflection;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Principal;
using System.Text.Json;

const string install = @"F:\JiaoLongControl";
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Probe must run elevated.");

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(install, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};

var outputPath = args[0];
var mode = args.Length > 1 ? args[1] : "read";
var result = new Dictionary<string, object?>
{
    ["timestamp"] = DateTimeOffset.Now,
    ["elevated"] = true,
    ["areas"] = new Dictionary<string, object?>(),
    ["errors"] = new List<object>()
};
var areas = (Dictionary<string, object?>)result["areas"]!;
var errors = (List<object>)result["errors"]!;

object? Capture(string area, Func<object?> action)
{
    try { return action(); }
    catch (Exception ex)
    {
        var root = ex is TargetInvocationException && ex.InnerException is not null ? ex.InnerException : ex;
        errors.Add(new { area, type = root.GetType().FullName, message = root.Message });
        return null;
    }
}

object Create(Assembly assembly, string typeName) => Activator.CreateInstance(assembly.GetType(typeName, true)!)!;
object? Invoke(object target, string method, params object[] methodArgs) =>
    target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, methodArgs);

Directory.SetCurrentDirectory(install);
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(install, "JiaoLongControl.dll"));

var smu = Capture("smu.create", () => Create(assembly, "JiaoLongControl.Server.Core.Controllers.RyzenSmuController"));
if (smu is not null)
{
    areas["smu"] = new
    {
        initialized = smu.GetType().GetProperty("IsInitialized")?.GetValue(smu),
        family = smu.GetType().GetProperty("CurrentFamily")?.GetValue(smu)?.ToString(),
        telemetry = Capture("smu.telemetry", () => Invoke(smu, "GetSmuTelemetry")),
        mp1Mailbox = Capture("smu.mp1Mailbox", () => Invoke(smu, "ReadMailboxArgs", false)),
        rsmuMailbox = Capture("smu.rsmuMailbox", () => Invoke(smu, "ReadMailboxArgs", true)),
        coreVoltage = Capture("smu.coreVoltage", () => Invoke(smu, "GetCoreVoltage"))
    };

    if (mode == "smu-am5-read")
    {
        areas["smuAm5Read"] = Capture("smu.am5Read", () =>
        {
            object ReadRsmu(uint command, string name)
            {
                var response = Invoke(smu, "SendRaw", command, 0u, false, name)!;
                var argsAfter = (ulong[])Invoke(smu, "ReadMailboxArgs", false)!;
                return new { command = $"0x{command:X}", response, argsAfter };
            }

            return new
            {
                pboScalar = ReadRsmu(0x6d, "Get PBO Scalar"),
                overclockingSupport = ReadRsmu(0x6f, "Get Overclocking Support"),
                maxCpuClock = ReadRsmu(0x6e, "Get Max CPU Clock"),
                fusedPowerLimit = ReadRsmu(0xdc, "Get Fused Slow Power Limit"),
                fusedTemperatureLimit = ReadRsmu(0xde, "Get Fused Tctl Temperature Limit")
            };
        });
    }

    if (mode == "smu-final-read")
    {
        areas["smuFinalRead"] = Capture("smu.finalRead", () =>
        {
            object Read(uint command, bool mp1, string name)
            {
                var response = Invoke(smu, "SendRaw", command, 0u, mp1, name)!;
                var argsAfter = (ulong[])Invoke(smu, "ReadMailboxArgs", mp1)!;
                return new { command = $"0x{command:X}", mp1, response, argsAfter };
            }
            return new
            {
                sustainedPowerAndThermalLimit = Read(0x23, true, "Get Sustained Power And Thermal Limit"),
                perCoreOptions = Read(0xD5, false, "Get Per-Core Options"),
                overclockingSupport = Read(0x6F, false, "Get Overclocking Support")
            };
        });
    }

    if (mode is "smu-pbo-same" or "controlled-same")
    {
        areas["smuPboSameValueWrite"] = Capture("smu.pboSameValueWrite", () =>
        {
            object ReadPbo()
            {
                var response = Invoke(smu, "SendRaw", 0x6du, 0u, false, "Get PBO Scalar")!;
                var argsAfter = (ulong[])Invoke(smu, "ReadMailboxArgs", false)!;
                return new { response, raw = (uint)argsAfter[0], scalar = BitConverter.Int32BitsToSingle((int)argsAfter[0]) };
            }

            dynamic before = ReadPbo();
            uint encoded = checked((uint)Math.Round((double)before.scalar * 100d));
            var write = Invoke(smu, "SetPboScalar", encoded)!;
            dynamic after = ReadPbo();
            return new { before, encoded, write, after, equal = before.raw == after.raw };
        });
    }

    if (mode == "controlled-roundtrip")
    {
        areas["smuPboRoundTrip"] = Capture("smu.pboRoundTrip", () =>
        {
            (uint Raw, float Scalar) ReadPbo()
            {
                Invoke(smu, "SendRaw", 0x6du, 0u, false, "Get PBO Scalar");
                var mailbox = (ulong[])Invoke(smu, "ReadMailboxArgs", false)!;
                return ((uint)mailbox[0], BitConverter.Int32BitsToSingle((int)mailbox[0]));
            }

            var before = ReadPbo();
            object? write = null;
            (uint Raw, float Scalar) changed = default;
            object? restore = null;
            (uint Raw, float Scalar) restored = default;
            try
            {
                write = Invoke(smu, "SetPboScalar", 200u);
                changed = ReadPbo();
            }
            finally
            {
                restore = Invoke(smu, "SetPboScalar", checked((uint)Math.Round(before.Scalar * 100d)));
                restored = ReadPbo();
            }
            return new { before, write, changed, changedOk = changed.Scalar == 2f, restore, restored, restoredOk = restored.Raw == before.Raw };
        });
    }

}

var gpu = Capture("gpu.create", () => Create(assembly, "JiaoLongControl.Server.Core.Controllers.NvidiaGpuController"));
if (gpu is not null)
{
    areas["gpu"] = new
    {
        stats = Capture("gpu.stats", () => Invoke(gpu, "GetGpuAllStats", 0)),
        powerRange = Capture("gpu.powerRange", () => Invoke(gpu, "GetGpuPowerLimitRange", 0)),
        curveCapabilities = Capture("gpu.curveCapabilities", () => Invoke(gpu, "GetGpuCurveCapabilities", 0)),
        curveStatus = Capture("gpu.curveStatus", () => Invoke(gpu, "GetGpuCurveStatus", 0))
    };

    if (mode is "vf-same" or "controlled-same")
    {
        areas["vfSameValueWrite"] = Capture("gpu.vfSameValueWrite", () =>
        {
            var handle = (IntPtr)Invoke(gpu, "GetCurveGpu", 0)!;
            var interop = assembly.GetType("JiaoLongControl.Server.Core.Native.NvGpuCurveInterop", true)!;
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var curve = (System.Collections.ICollection)interop.GetMethod("GetVfCurve", flags)!.Invoke(null, [handle])!;
            var get = interop.GetMethod("GetCurveFreqDeltasKhz", flags)!;
            var set = interop.GetMethod("SetCurveFreqDeltasKhz", flags)!;
            var before = (int[])get.Invoke(null, [handle, curve.Count])!;
            set.Invoke(null, [handle, before]);
            var after = (int[])get.Invoke(null, [handle, curve.Count])!;
            return new
            {
                curvePointCount = curve.Count,
                nodeCount = before.Length,
                equal = before.SequenceEqual(after),
                changedNodes = before.Zip(after).Count(pair => pair.First != pair.Second),
                before,
                after
            };
        });
    }


    if (mode == "controlled-roundtrip")
    {
        areas["vfNodeRoundTrip"] = Capture("gpu.vfNodeRoundTrip", () =>
        {
            var handle = (IntPtr)Invoke(gpu, "GetCurveGpu", 0)!;
            var interop = assembly.GetType("JiaoLongControl.Server.Core.Native.NvGpuCurveInterop", true)!;
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var curve = (System.Collections.ICollection)interop.GetMethod("GetVfCurve", flags)!.Invoke(null, [handle])!;
            var get = interop.GetMethod("GetCurveFreqDeltasKhz", flags)!;
            var set = interop.GetMethod("SetCurveFreqDeltasKhz", flags)!;
            var before = (int[])get.Invoke(null, [handle, curve.Count])!;
            var target = (int[])before.Clone();
            const int node = 50;
            target[node] = checked(target[node] + 15000);
            int[] changed = [];
            int[] restored = [];
            try
            {
                set.Invoke(null, [handle, target]);
                changed = (int[])get.Invoke(null, [handle, curve.Count])!;
            }
            finally
            {
                set.Invoke(null, [handle, before]);
                restored = (int[])get.Invoke(null, [handle, curve.Count])!;
            }
            return new
            {
                node,
                beforeKhz = before[node],
                requestedKhz = target[node],
                changedKhz = changed[node],
                changedOnlyTarget = changed.Zip(before).Count(pair => pair.First != pair.Second) == 1,
                restoredKhz = restored[node],
                restoredExact = restored.SequenceEqual(before)
            };
        });
    }

    if (mode == "gpu-private-same")
    {
        areas["gpuPrivatePowerSameValueWrite"] = Capture("gpu.privatePowerSameValueWrite", () =>
        {
            var physicalGpu = Invoke(gpu, "GetGPU", 0)!;
            var handle = physicalGpu.GetType().GetProperty("Handle")!.GetValue(physicalGpu)!;
            var nativeAssembly = physicalGpu.GetType().Assembly;
            var api = nativeAssembly.GetType("NvAPIWrapper.Native.GPUApi", true)!;
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var getInfo = api.GetMethod("ClientPowerPoliciesGetInfo", flags)!;
            var getStatus = api.GetMethod("ClientPowerPoliciesGetStatus", flags)!;
            var setStatus = api.GetMethod("ClientPowerPoliciesSetStatus", flags)!;
            var info = getInfo.Invoke(null, [handle])!;
            var before = getStatus.Invoke(null, [handle])!;
            var beforeJson = JsonSerializer.Serialize(before);
            setStatus.Invoke(null, [handle, before]);
            var after = getStatus.Invoke(null, [handle])!;
            var afterJson = JsonSerializer.Serialize(after);
            return new { equal = beforeJson == afterJson, info, before, after };
        });
    }

    if (mode == "drs-read")
    {
        areas["drsDynamicBoost"] = Capture("gpu.drsDynamicBoost", () =>
        {
            var physicalGpu = Invoke(gpu, "GetGPU", 0)!;
            var nativeAssembly = physicalGpu.GetType().Assembly;
            var settingInfoType = nativeAssembly.GetType("NvAPIWrapper.DRS.SettingInfo", true)!;
            var allSettings = (Array)settingInfoType.GetMethod("GetAvailableSetting", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            var matching = allSettings.Cast<object>().Select(item => new
            {
                name = item.GetType().GetProperty("Name")!.GetValue(item)?.ToString(),
                id = item.GetType().GetProperty("SettingId")!.GetValue(item),
                known = item.GetType().GetProperty("IsKnown")!.GetValue(item)
            }).Where(item => item.name is not null &&
                (item.name.Contains("Dynamic", StringComparison.OrdinalIgnoreCase) ||
                 item.name.Contains("Boost", StringComparison.OrdinalIgnoreCase) ||
                 item.name.Contains("Max-Q", StringComparison.OrdinalIgnoreCase) ||
                 item.name.Contains("Power", StringComparison.OrdinalIgnoreCase))).ToArray();
            return new { availableSettingCount = allSettings.Length, matching };
        });
    }

    if (mode == "gpu-voltage-read")
    {
        areas["gpuVoltageTable"] = Capture("gpu.voltageTable", () =>
        {
            var handle = (IntPtr)Invoke(gpu, "GetCurveGpu", 0)!;
            var interop = assembly.GetType("JiaoLongControl.Server.Core.Native.NvGpuCurveInterop", true)!;
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var table = interop.GetMethod("GetPstates20", flags)!.Invoke(null, [handle])!;
            var tableType = table.GetType();
            var fields = tableType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(field => field.Name.Contains("volt", StringComparison.OrdinalIgnoreCase) ||
                                field.Name.Contains("state", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(field => field.Name, field => field.GetValue(table));
            return new { tableType = tableType.FullName, fields };
        });
    }

    if (mode == "gpu-voltage-roundtrip")
    {
        areas["gpuVoltageNodeRoundTrip"] = Capture("gpu.voltageNodeRoundTrip", () =>
        {
            var handle = (IntPtr)Invoke(gpu, "GetCurveGpu", 0)!;
            var interop = assembly.GetType("JiaoLongControl.Server.Core.Native.NvGpuCurveInterop", true)!;
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var getCurve = interop.GetMethod("GetVfCurve", flags)!;
            object[] Snapshot() => ((System.Collections.IEnumerable)getCurve.Invoke(null, [handle])!).Cast<object>().ToArray();
            string Serialize(object[] curve) => JsonSerializer.Serialize(curve, new JsonSerializerOptions { IncludeFields = true });
            var before = Snapshot();
            const int node = 50;
            bool writeOk = false;
            object[] changed = [];
            bool restoreOk = false;
            object[] restored = [];
            try
            {
                writeOk = NvPointProbe.Set(handle, node, 0, -12500);
                Thread.Sleep(1500);
                changed = Snapshot();
            }
            finally
            {
                restoreOk = NvPointProbe.Set(handle, node, 0, 0);
                Thread.Sleep(1500);
                restored = Snapshot();
            }
            return new
            {
                node,
                requestedVoltageOffsetUv = -12500,
                writeOk,
                curveChanged = Serialize(changed) != Serialize(before),
                restoreOk,
                restoredExact = Serialize(restored) == Serialize(before)
            };
        });
    }

    if (mode == "gpu-voltage-recover")
    {
        areas["gpuVoltageRecovery"] = Capture("gpu.voltageRecovery", () =>
        {
            var handle = (IntPtr)Invoke(gpu, "GetCurveGpu", 0)!;
            var interop = assembly.GetType("JiaoLongControl.Server.Core.Native.NvGpuCurveInterop", true)!;
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var getCurve = interop.GetMethod("GetVfCurve", flags)!;
            (int Mv, int Mhz)[] Snapshot() => ((System.Collections.IEnumerable)getCurve.Invoke(null, [handle])!).Cast<object>()
                .Select(item => ((int)item.GetType().GetField("Item1")!.GetValue(item)!, (int)item.GetType().GetField("Item2")!.GetValue(item)!)).ToArray();
            var before = Snapshot();
            var clearOk = NvPointProbe.Set(handle, 50, 0, 0);
            Thread.Sleep(1500);
            var after = Snapshot();
            var differences = before.Zip(after).Select((pair, index) => new { index, before = pair.First, after = pair.Second })
                .Where(item => item.before != item.after).ToArray();
            return new { clearOk, beforeCount = before.Length, afterCount = after.Length, differenceCount = differences.Length, differences };
        });
    }
}

var mux = Capture("mux.create", () => Create(assembly, "JiaoLongControl.Server.Core.Controllers.GpuController"));
if (mux is not null) areas["mux"] = new { current = Capture("mux.get", () => Invoke(mux, "Get")) };

var fan = Capture("fan.create", () => Create(assembly, "JiaoLongControl.Server.Core.Controllers.FanController"));
if (fan is not null)
{
    areas["fan"] = new
    {
        initialized = fan.GetType().GetProperty("IsInitialized")?.GetValue(fan),
        speed = Capture("fan.speed", () => Invoke(fan, "GetFanSpeed")),
        maxSwitch = Capture("fan.maxSwitch", () => Invoke(fan, "GetMaxFanSpeedSwitch"))
    };

    if (mode == "fan-ec-read")
    {
        areas["fanEcRead"] = Capture("fan.ecRead", () =>
        {
            var baseType = fan.GetType().BaseType!;
            var read = baseType.GetMethod("EC_RAM_READ", BindingFlags.NonPublic | BindingFlags.Instance)!;
            byte Read(ushort address) => (byte)read.Invoke(fan, [address])!;
            return new
            {
                control = Read(0x0B20),
                cpuTarget = Read(0xC83C),
                gpuTarget = Read(0xC83D),
                speed = Invoke(fan, "GetFanSpeed")
            };
        });
    }

    if (mode == "fan-roundtrip")
    {
        areas["fanIndependentRoundTrip"] = Capture("fan.independentRoundTrip", () =>
        {
            var baseType = fan.GetType().BaseType!;
            var read = baseType.GetMethod("EC_RAM_READ", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var write = baseType.GetMethod("EC_RAM_WRITE", BindingFlags.NonPublic | BindingFlags.Instance)!;
            byte Read(ushort address) => (byte)read.Invoke(fan, [address])!;
            bool Write(ushort address, byte value) => (bool)write.Invoke(fan, [address, value])!;
            var originalControl = Read(0x0B20);
            var originalCpu = Read(0xC83C);
            var originalGpu = Read(0xC83D);
            object? cpuSpeed = null;
            object? gpuSpeed = null;
            byte cpuControl = 0, cpuTarget = 0, cpuGpuTarget = 0;
            byte gpuControl = 0, gpuTarget = 0, gpuCpuTarget = 0;
            try
            {
                Invoke(fan, "CpuFanSetSpeed", (byte)60);
                Thread.Sleep(1500);
                cpuControl = Read(0x0B20);
                cpuTarget = Read(0xC83C);
                cpuGpuTarget = Read(0xC83D);
                cpuSpeed = Invoke(fan, "GetFanSpeed");
                Write(0xC83C, originalCpu);
                Write(0x0B20, originalControl);

                Invoke(fan, "GpuFanSetSpeed", (byte)60);
                Thread.Sleep(1500);
                gpuControl = Read(0x0B20);
                gpuTarget = Read(0xC83D);
                gpuCpuTarget = Read(0xC83C);
                gpuSpeed = Invoke(fan, "GetFanSpeed");
            }
            finally
            {
                Write(0xC83C, originalCpu);
                Write(0xC83D, originalGpu);
                Write(0x0B20, originalControl);
            }
            var restoredControl = Read(0x0B20);
            var restoredCpu = Read(0xC83C);
            var restoredGpu = Read(0xC83D);
            return new
            {
                before = new { originalControl, originalCpu, originalGpu },
                cpu = new { cpuControl, cpuTarget, otherTarget = cpuGpuTarget, cpuSpeed },
                gpu = new { gpuControl, gpuTarget, otherTarget = gpuCpuTarget, gpuSpeed },
                restored = new { restoredControl, restoredCpu, restoredGpu },
                cpuIndependent = cpuTarget == 60 && cpuGpuTarget == originalGpu,
                gpuIndependent = gpuTarget == 60 && gpuCpuTarget == originalCpu,
                restoredExact = restoredControl == originalControl && restoredCpu == originalCpu && restoredGpu == originalGpu
            };
        });
    }
}

await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
}));

static class NvPointProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GpuBufferDelegate(IntPtr gpu, byte[] buffer);

    [DllImport("nvapi64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvapi_QueryInterface")]
    private static extern IntPtr QueryInterface(uint id);

    public static bool Set(IntPtr gpu, int pointIndex, int frequencyOffsetKhz, int voltageOffsetUv)
    {
        const int size = 0x2420;
        var pointer = QueryInterface(0x0733E009);
        if (pointer == IntPtr.Zero || pointIndex is < 0 or >= 127) return false;
        var set = Marshal.GetDelegateForFunctionPointer<GpuBufferDelegate>(pointer);
        var buffer = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (1 << 16) | size);
        buffer[4 + pointIndex / 8] = (byte)(1 << (pointIndex % 8));
        var entry = 0x20 + pointIndex * 0x48;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry, 4), frequencyOffsetKhz);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 4, 4), voltageOffsetUv);
        return set(gpu, buffer) == 0;
    }
}
