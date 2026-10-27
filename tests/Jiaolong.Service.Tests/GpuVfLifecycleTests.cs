using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Service.Home;
using Microsoft.Extensions.Logging;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class GpuVfLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void Native_boundary_rejects_entry_and_resolver_pause_without_a_driver_write()
    {
        foreach (string stage in new[] { "entry", "resolver", "partial" })
        {
            var bus = new FakeGpu { Allowed = stage != "entry", PauseStage = stage };
            using var api = bus.Create();
            Assert.AreEqual("hardwareAccessPaused", Assert.ThrowsExactly<IOException>(() =>
                api.WriteDeltas((nint)1, new byte[32], Enumerable.Repeat(20_000, 127).ToArray())).Message, stage);
            Assert.AreEqual(stage == "partial" ? 1 : 0, bus.Writes, stage);
            Assert.AreEqual(stage == "partial", api.WriteAttempted, stage);
        }
        foreach (var header in new[] { (0, 2), (17, 2), (1, 0), (1, 9) })
        {
            var bus = new FakeGpu { StateCount = header.Item1, ClockCount = header.Item2 };
            using var api = bus.Create();
            Assert.ThrowsExactly<InvalidDataException>(() => api.ReadPstate0MemoryOffset((nint)1));
            Assert.AreEqual(0, bus.Writes, "An unknown layout must not be normalized into supported state.");
        }
    }

    [TestMethod]
    public void Recovery_reads_do_not_authorize_table_or_pstate_writes_and_revoke_cached_provider_reads()
    {
        var bus = new FakeGpu { Allowed = false, ReadAllowed = true };
        using var api = bus.Create();
        Assert.AreEqual(127, api.ReadDeltas((nint)1, new byte[32], 127).Length);
        var before = api.ReadPstate0MemoryOffset((nint)1);
        Assert.ThrowsExactly<IOException>(() => api.WriteDeltas((nint)1, new byte[32], new int[127]));
        Assert.ThrowsExactly<IOException>(() => api.WritePstate0Offsets((nint)1, before, 0, 100_000));
        Assert.AreEqual(0, bus.Writes);
        using var provider = Provider(bus);
        _ = Invoke(provider, "ReadGpuVfLocked", CancellationToken.None, false);
        bus.ReadAllowed = false;
        provider.SetBackgroundWorkPaused(true);
        Assert.AreEqual("hardwareAccessPaused", Capture(() => Invoke(provider, "ReadGpuVfLocked", CancellationToken.None, false))?.Message);

        var retainedLogger = new ThrowingLogger();
        var closingBus = new FakeGpu { PauseStage = "baseline" };
        var closing = Provider(closingBus);
        _ = Invoke(closing, "ReadGpuVfLocked", CancellationToken.None, false);
        typeof(WindowsHomeHardwareProvider).GetField("logger", Private)!.SetValue(closing, retainedLogger);
        closing.Dispose();
        var contexts = (System.Collections.ICollection)Field(closing, "gpuNativeContexts")!;
        Assert.AreEqual(1, contexts.Count, "Paused cleanup retains the native context despite logger failure.");
        Assert.IsTrue(retainedLogger.Calls > 0);
    }

    [TestMethod]
    public void Zero_partial_and_recovery_pause_preserve_mutation_and_block_following_commands()
    {
        foreach (string kind in new[] { "vf", "core", "memory" })
        foreach (string stage in new[] { "baseline", "resolver", "partial", "recovery" })
        {
            var bus = new FakeGpu { PauseStage = stage };
            using var provider = Provider(bus);
            bus.OnPause = () => provider.SetBackgroundWorkPaused(true);
            HardwareCommand command = kind switch
            {
                "core" => new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10_000, true) { ExpectedVfOffsetsKhz = bus.Offsets.ToArray() },
                "memory" => new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 100_000, true),
                _ => new SetGpuVfCurveCommand(Guid.NewGuid(), bus.Offsets.ToArray(), bus.Offsets.Select((v, i) => i == 0 ? 0 : v + 10_000).ToArray(), true)
            };
            var method = kind == "vf" ? "ExecuteGpuVfLocked" : kind == "core" ? "ExecuteGpuCoreOffsetLocked" : "ExecuteGpuMemoryOffsetLocked";
            var result = (CommandResult)Invoke(provider, method, command, CancellationToken.None)!;
            bool attempted = stage is "partial" or "recovery";
            Assert.AreEqual(attempted ? CommandState.RecoveryRequired : CommandState.Rejected, result.State, stage);
            Assert.AreEqual(attempted ? HardwareMutation.Unknown : HardwareMutation.NoMutation, result.Mutation, stage);
            Assert.AreEqual(attempted ? "True" : "False", result.Error?.Details["writeAttempted"], stage);
            if (!attempted) Assert.AreEqual(0, bus.Writes, stage);
            else
            {
                Assert.AreEqual(stage == "recovery" ? 2 : 1, bus.Writes, stage);
                Assert.IsNotNull(Field(provider, "gpuVfRecoveryOffsets"), stage);
                if (kind != "vf") Assert.IsNotNull(Field(provider, "gpuPstateRecoverySnapshot"), stage);
                provider.SetBackgroundWorkPaused(false);
                bus.Allowed = bus.ReadAllowed = true;
                bus.PauseStage = null;
                int writes = bus.Writes;
                var followup = (CommandResult)Invoke(provider, method, command, CancellationToken.None)!;
                Assert.AreEqual(CommandState.RecoveryRequired, followup.State);
                Assert.AreEqual(writes, bus.Writes, "Unknown recovery must block subsequent tuning.");
            }
        }
    }

    [TestMethod]
    public void Ordinary_failure_restores_original_offsets_and_pstate_without_claiming_target_applied()
    {
        foreach (string kind in new[] { "vf", "core", "memory" })
        foreach (bool fail in new[] { false, true })
        {
            var bus = new FakeGpu { FailFirstWrite = fail };
            var original = bus.Offsets.ToArray();
            using var provider = Provider(bus);
            HardwareCommand command = kind switch
            {
                "core" => new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10_000, true) { ExpectedVfOffsetsKhz = original },
                "memory" => new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 100_000, true),
                _ => new SetGpuVfCurveCommand(Guid.NewGuid(), original, original.Select((v, i) => i == 0 ? 0 : v + 10_000).ToArray(), true)
            };
            var result = (CommandResult)Invoke(provider, kind == "vf" ? "ExecuteGpuVfLocked" : kind == "core" ? "ExecuteGpuCoreOffsetLocked" : "ExecuteGpuMemoryOffsetLocked", command, CancellationToken.None)!;
            Assert.AreEqual(fail ? CommandState.RolledBack : CommandState.Applied, result.State);
            CollectionAssert.AreEqual(fail || kind == "memory" ? original : original.Select((v, i) => i == 0 ? 0 : v + 10_000).ToArray(), bus.Offsets);
            Assert.AreEqual(!fail && kind == "core" ? 10_000 : 0, bus.CoreOffset);
            Assert.AreEqual(!fail && kind == "memory" ? 100_000 : 0, bus.MemoryOffset);
            if (fail)
            {
                Assert.AreEqual("fakeGpuWriteFailure", result.Error?.Details["reason"]);
                Assert.AreEqual("True", result.Error?.Details["writeAttempted"]);
                Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
            }
            else Assert.AreEqual(HardwareMutation.Changed, result.Mutation);
            Assert.IsNull(Field(provider, "gpuVfRecoveryOffsets"));
            Assert.IsNull(Field(provider, "gpuPstateRecoverySnapshot"));
        }
    }

    [TestMethod]
    public void Failed_state_refresh_keeps_gpu_recovery_pending_after_raw_offset_restore()
    {
        var bus = new FakeGpu { FailFirstWrite = true, FailRefreshAfterRollback = true };
        using var provider = Provider(bus);
        var command = new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10_000, true)
        { ExpectedVfOffsetsKhz = bus.Offsets.ToArray() };

        var result = (CommandResult)Invoke(provider, "ExecuteGpuCoreOffsetLocked", command, CancellationToken.None)!;

        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
        Assert.AreEqual(0, bus.CoreOffset);
        Assert.IsNotNull(Field(provider, "gpuVfRecoveryOffsets"));
        Assert.IsNotNull(Field(provider, "gpuPstateRecoverySnapshot"));

        var curveBus = new FakeGpu { FailRefreshAfterWrite = true };
        using var curveProvider = Provider(curveBus);
        var baseline = curveBus.Offsets.ToArray();
        var curveCommand = new SetGpuVfCurveCommand(Guid.NewGuid(), baseline,
            baseline.Select((value, index) => index == 0 ? 0 : value + 10_000).ToArray(), true);
        var curveResult = (CommandResult)Invoke(curveProvider, "ExecuteGpuVfLocked", curveCommand, CancellationToken.None)!;
        Assert.AreEqual(CommandState.RecoveryRequired, curveResult.State);
        CollectionAssert.AreEqual(baseline, curveBus.Offsets);
        Assert.IsNotNull(Field(curveProvider, "gpuVfRecoveryOffsets"));
    }

    [TestMethod]
    public void Nonfinite_temperature_rejects_direct_gpu_commands_before_any_driver_write()
    {
        foreach (var scenario in new[] { (Kind: "core", Temperature: double.NaN), (Kind: "memory", Temperature: double.PositiveInfinity),
            (Kind: "vf", Temperature: double.NegativeInfinity) })
        {
            var bus = new FakeGpu();
            using var provider = Provider(bus, temperature: _ => scenario.Temperature);
            var result = ExecuteThermalCommand(provider, bus, scenario.Kind);
            Assert.AreEqual(CommandState.Rejected, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(0, bus.Writes);
        }
    }

    [TestMethod]
    public void Lost_thermal_readback_after_gpu_write_rolls_back_complete_original_state()
    {
        foreach (string kind in new[] { "core", "memory", "vf" })
        {
            var bus = new FakeGpu();
            int[] original = bus.Offsets.ToArray();
            using var provider = Provider(bus, temperature: _ => bus.Writes == 0 ? 45 : double.NaN);
            var result = ExecuteThermalCommand(provider, bus, kind);
            Assert.AreEqual(CommandState.RolledBack, result.State, kind);
            Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
            CollectionAssert.AreEqual(original, bus.Offsets);
            Assert.AreEqual(0, bus.CoreOffset);
            Assert.AreEqual(0, bus.MemoryOffset);
            Assert.IsNull(Field(provider, "gpuPstateRecoverySnapshot"));
        }
        var uncertain = new FakeGpu { FailRefreshAfterRollback = true };
        using var pending = Provider(uncertain, temperature: _ => uncertain.Writes == 0 ? 45 : double.NaN);
        var failed = ExecuteThermalCommand(pending, uncertain, "core");
        Assert.AreEqual(CommandState.RecoveryRequired, failed.State);
        Assert.IsNotNull(Field(pending, "gpuPstateRecoverySnapshot"));
        int writes = uncertain.Writes;
        Assert.AreEqual(CommandState.RecoveryRequired, ExecuteThermalCommand(pending, uncertain, "core").State);
        Assert.AreEqual(writes, uncertain.Writes);
    }

    [TestMethod]
    public void Changed_temperature_or_ac_during_capture_stops_before_first_gpu_value_write()
    {
        foreach (bool thermal in new[] { false, true })
        {
            var bus = new FakeGpu();
            using var provider = Provider(bus, temperature: _ => thermal && bus.Reads > 0 ? double.NaN : 45,
                acPower: () => thermal || bus.Reads == 0);
            var result = ExecuteThermalCommand(provider, bus, "core");
            Assert.AreEqual(CommandState.Rejected, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(0, bus.Writes);
        }
    }

    private static CommandResult ExecuteThermalCommand(WindowsHomeHardwareProvider provider, FakeGpu bus, string kind)
    {
        HardwareCommand command = kind switch
        {
            "core" => new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10000, true) { ExpectedVfOffsetsKhz = bus.Offsets.ToArray() },
            "memory" => new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 100000, true),
            _ => new SetGpuVfCurveCommand(Guid.NewGuid(), bus.Offsets.ToArray(), bus.Offsets.Select((value, index) => index == 0 ? 0 : value + 10000).ToArray(), true)
        };
        return (CommandResult)Invoke(provider, kind == "core" ? "ExecuteGpuCoreOffsetLocked" : kind == "memory" ? "ExecuteGpuMemoryOffsetLocked" :
            "ExecuteGpuVfLocked", command, CancellationToken.None)!;
    }

    private static WindowsHomeHardwareProvider Provider(FakeGpu bus, ILogger<WindowsHomeHardwareProvider>? logger = null,
        Func<CancellationToken, double?>? temperature = null, Func<bool>? acPower = null)
    {
        var provider = new WindowsHomeHardwareProvider(bus.Create, acPower ?? (() => true), temperature ?? (_ => 45));
        if (logger is not null) typeof(WindowsHomeHardwareProvider).GetField("logger", Private)!.SetValue(provider, logger);
        typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
            new CompatibilityDecision(CompatibilityMode.Writable,
                new CapabilitySnapshot([new CapabilityDescriptor("gpuVfCurve", CapabilityState.Available, null)]), null, []));
        return provider;
    }
    private static object? Field(object target, string field) => target.GetType().GetField(field, Private)?.GetValue(target);
    private static object? Invoke(object target, string method, params object?[] args)
    {
        try { return target.GetType().GetMethod(method, Private)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }

    private sealed class ThrowingLogger : ILogger<WindowsHomeHardwareProvider>
    {
        public int Calls { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls++;
            throw new IOException("fakeLoggerFailure");
        }
    }

    internal sealed class FakeGpu
    {
        public bool Allowed = true, ReadAllowed = true, FailFirstWrite, FailRefreshAfterRollback, FailRefreshAfterWrite;
        public string? PauseStage;
        public Action? OnPause;
        public Action? BeforeWrite;
        public Func<IntPtr, GpuAdapterIdentity>? IdentityReader = _ => new(0x28E010DE, 1, 0xA1, 0x28E0, 1, 0);
        public int Writes, Reads, CoreOffset, MemoryOffset;
        public int StateCount = 1, ClockCount = 2;
        public int CoreMinimum = -200000, CoreMaximum = 200000;
        public int[] Offsets = Enumerable.Range(0, 127).Select(i => i == 0 ? 0 : 10_000).ToArray();
        public NvapiVfInterop Create() => new(Resolve, () => [(nint)1], () => Allowed, () => ReadAllowed,
            adapterIdentityReader: IdentityReader);
        private void Pause() { Allowed = ReadAllowed = false; OnPause?.Invoke(); }
        private Func<IntPtr, byte[], int> Resolve(uint function)
        {
            bool write = function is 120840201u or 256749163u;
            if (write && PauseStage == "resolver") Pause();
            return (_, bytes) =>
            {
                if (write)
                {
                    BeforeWrite?.Invoke();
                    Writes++;
                    if (function == 120840201u)
                        for (int i = 1; i < 127; i++) Offsets[i] = BitConverter.ToInt32(bytes, 100 + (i - 1) * 36 + 24);
                    else
                    {
                        CoreOffset = BitConverter.ToInt32(bytes, 40); MemoryOffset = BitConverter.ToInt32(bytes, 84);
                        Offsets = new int[127];
                    }
                    if (PauseStage == "partial" || PauseStage == "recovery" && Writes == 2) Pause();
                    if (Writes == 1 && (FailFirstWrite || PauseStage == "recovery")) throw new IOException("fakeGpuWriteFailure");
                }
                else
                {
                    Reads++;
                    if ((FailRefreshAfterRollback && Writes >= 3 || FailRefreshAfterWrite && Writes >= 1) && function == 559119060u)
                        throw new IOException("fakeRefreshFailed");
                    switch (function)
                    {
                        case 559119060u:
                            for (int i = 0; i < 127; i++) { Put(bytes, 64 + 28 * i + 12, (400 + i * 5) * 1000); Put(bytes, 64 + 28 * i + 8, 1_500_000); }
                            break;
                        case 603042099u:
                            for (int i = 1; i < 127; i++) Put(bytes, 100 + (i - 1) * 36 + 24, Offsets[i]);
                            break;
                        case 1878528531u:
                            Put(bytes, 8, StateCount); Put(bytes, 12, ClockCount); Put(bytes, 40, CoreOffset);
                            Put(bytes, 72, 4); Put(bytes, 84, MemoryOffset); Put(bytes, 88, -2_000_000); Put(bytes, 92, 2_000_000);
                            break;
                        case 1689533034u: Put(bytes, 80, CoreMaximum); Put(bytes, 84, CoreMinimum); break;
                    }
                    if (PauseStage == "baseline" && function == 603042099u) Pause();
                }
                return 0;
            };
        }
        private static void Put(byte[] bytes, int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    }
}
