using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class V35Mp1AdmissionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly HardwareIdentity Identity = new("MRID6-23", "MRID6_23_P_V35",
        "AMD Ryzen 7 7745HX with Radeon Graphics", "NVIDIA GeForce RTX 4070 Laptop GPU");
    private static readonly CapabilityDescriptor[] Items = [new("cpuTuning", CapabilityState.Available, null),
        new("cpuTuning:smu", CapabilityState.Available, null)];
    private static readonly CpuTuningState Cpu = new(99, null, null, null, null, 8, null, null, null)
    { AdvancedLimits = new(PptWatts: 120, Mp1TemperatureC: 99) };

    [TestMethod]
    public void Only_verified_v35_profile_grants_independent_scalars_without_unlocking_oem_or_aggregate()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        Seed(provider);
        var fields = Fields(provider);
        var mp1 = fields.Single(field => field.Key == "mp1TemperatureC");
        Assert.IsTrue(mp1.Readable && mp1.Writable);
        Assert.IsNotNull(mp1.VerifiedAtUtc);
        Assert.AreEqual(40d, mp1.Minimum);
        Assert.AreEqual(100d, mp1.Maximum);
        var ppt = fields.Single(field => field.Key == "pptWatts");
        Assert.IsTrue(ppt.Readable && ppt.Writable);
        Assert.IsNotNull(ppt.VerifiedAtUtc);
        Assert.AreEqual(45d, ppt.Minimum);
        Assert.AreEqual(120d, ppt.Maximum);
        Assert.AreEqual(.001, ppt.Step);
        foreach (string key in new[] { "advancedCpuTuning", "temperatureLimitC", "splWatts", "spptWatts" })
            Assert.IsFalse(fields.Single(field => field.Key == key).Writable, key);
        foreach (var wrong in new[] { Identity with { BoardProduct = "other" }, Identity with { BiosVersion = "V35-other" },
            Identity with { CpuModel = "AMD Ryzen 9 7945HX" } })
        {
            Seed(provider);
            Set(provider, "identity", wrong);
            Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
            Assert.IsFalse(Fields(provider).Single(field => field.Key == "pptWatts").Writable);
        }
        foreach (var snapshot in new CpuSmuLimitSnapshot?[] { null, new(new(Mp1TemperatureC: 99), null, 0x540108),
            new(new(), null, 0x540105), new(new(Mp1TemperatureC: 98), null, 0x540105) })
        {
            Seed(provider);
            Set(provider, "smuLimitCache", snapshot);
            Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        }
        foreach (double value in new[] { 1d, 120.125, 250d, 0d, 250.001, double.NaN, double.PositiveInfinity, 65.0005 })
        {
            Seed(provider);
            var limits = Cpu.AdvancedLimits! with { PptWatts = value };
            Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(limits, null, 0x540105));
            Assert.AreEqual(value is >= 1 and <= 250 && value == Math.Round(value, 3),
                Fields(provider, Cpu with { AdvancedLimits = limits }).Single(field => field.Key == "pptWatts").Writable);
        }
        Seed(provider);
        Assert.IsFalse(Fields(provider, Cpu with { AdvancedLimits = Cpu.AdvancedLimits! with { PptWatts = 119 } })
            .Single(field => field.Key == "pptWatts").Writable);
        foreach (var timestamp in new[] { DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(1) })
        {
            Seed(provider);
            Set(provider, "smuLimitCacheAt", timestamp);
            Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
            Assert.IsFalse(Fields(provider).Single(field => field.Key == "pptWatts").Writable);
        }
        Seed(provider);
        Set(provider, "compatibilityDecision", new CompatibilityDecision(CompatibilityMode.ReadOnlySafeMode, new(Items), null, []));
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "pptWatts").Writable);
        Seed(provider);
        Set(provider, "smuPmTableUnsupported", true);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "pptWatts").Writable);
        Seed(provider);
        Assert.IsFalse(Fields(provider, Cpu with { AdvancedLimits = null }).Single(field => field.Key == "mp1TemperatureC").Writable);
    }

    [TestMethod]
    public void Cached_direct_authority_cannot_survive_pause_resume_or_transport_disposal()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        Seed(provider);
        Assert.IsTrue(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        provider.SetBackgroundWorkPaused(true);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "pptWatts").Writable);
        provider.SetBackgroundWorkPaused(false);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "pptWatts").Writable);
        Seed(provider);
        Assert.IsTrue(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        Invoke(provider, "DisposeCpuTuning", new object[] { null! });
        Assert.IsNull(Get(provider, "smuLimitCache"));
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "mp1TemperatureC").Writable);
        Assert.IsFalse(Fields(provider).Single(field => field.Key == "pptWatts").Writable);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var smu = new PawnIoCurveOptimizerTransport((_, _) => throw new AssertFailedException("No device I/O expected"));
        Set(provider, "curveOptimizer", smu);
        Set(provider, "cpuTuningTransport", new WindowsCpuTuningTransport(null!, null!, null!));
        foreach (string method in new[] { "ReadSmuLimits", "ReadCpuTuningState" })
        foreach (bool fresh in new[] { false, true })
        {
            Seed(provider);
            if (!fresh) Set(provider, "smuLimitCacheAt", default(DateTimeOffset));
            var error = Assert.Throws<TargetInvocationException>(() => Invoke(provider, method,
                method == "ReadSmuLimits" ? [cancelled.Token] : [cancelled.Token, true]));
            Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
            Assert.IsNull(Get(provider, "smuLimitCache"));
        }
    }

    [TestMethod]
    public void Both_dispatch_paths_refresh_direct_profile_and_refuse_cached_only_authority_without_writes()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        var transport = new NoIoTransport();
        Set(provider, "cpuTuningTransport", new WindowsCpuTuningTransport(null!, null!, null!));
        Set(provider, "performanceController", new PerformanceController(cpuTransport: transport));
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null) { Advanced = new(Mp1TemperatureC: 98) };
        foreach (bool batch in new[] { false, true })
        foreach (var advanced in new[] { new AdvancedCpuTuningPlan(Mp1TemperatureC: 98), new(PptWatts: 64.999) })
        {
            Seed(provider);
            plan = plan with { Advanced = advanced };
            HardwareCommand command = batch ? new SetCpuTuningBatchCommand(Guid.NewGuid(), [plan], true)
                : new SetCpuTuningCommand(Guid.NewGuid(), plan, true);
            var result = (CommandResult)Invoke(provider, batch ? "ExecuteCpuTuningBatchLocked" : "ExecuteAdvancedCpuLimitLocked", command, CancellationToken.None)!;
            Assert.AreEqual(CommandState.Rejected, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.IsNull(Get(provider, "smuLimitCache"), "No actual SMU transport exists; a display cache must not authorize a write.");
        }
        Assert.AreEqual(0, transport.Calls);
        var malformed = new SetCpuTuningCommand(Guid.NewGuid(), plan with { AcMaxFrequencyMhz = 4600 }, true);
        var refused = (CommandResult)Invoke(provider, "ExecuteAdvancedCpuLimitLocked", malformed, CancellationToken.None)!;
        Assert.AreEqual(CommandState.Rejected, refused.State);
        Assert.AreEqual(HardwareMutation.NoMutation, refused.Mutation);
        Seed(provider);
        foreach (var (target, needsHeadroom) in new[] { (70, false), (99, false), (100, true) })
            Assert.AreEqual(needsHeadroom, (bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(Mp1TemperatureC: target), null!)!);
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(Mp1TemperatureC: 99), new AdvancedCpuTuningPlan(Mp1TemperatureC: 70))!);
        foreach (var (target, needsHeadroom) in new[] { (60d, false), (120d, false), (120.001, true) })
            Assert.AreEqual(needsHeadroom, (bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(PptWatts: target), null!)!);
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(PptWatts: 64), new AdvancedCpuTuningPlan(PptWatts: 60))!);
        Set(provider, "smuLimitCache", null);
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(Mp1TemperatureC: 70), null!)!);
    }

    [TestMethod]
    public void Normalized_summary_preserves_verified_direct_authority_without_unlocking_the_aggregate()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        Seed(provider);
        var fields = Fields(provider);
        var normalized = HomeCapabilityCatalog.WithFieldAvailability(Items, fields);
        Assert.AreEqual(CapabilityState.Available, normalized.Single(item => item.Key == "cpuTuning:smu").State);
        Set(provider, "compatibilityDecision", new CompatibilityDecision(CompatibilityMode.Writable, new(normalized), null, []));
        Assert.IsTrue((bool)Invoke(provider, "CanWriteDirectMp1Limit", Get(provider, "smuLimitCache")!)!);
        var refreshed = (FieldCapability[])Invoke(provider, "CreateFieldCapabilities", normalized, Cpu, null!)!;
        Assert.IsTrue(refreshed.Single(field => field.Key == "mp1TemperatureC").Writable);
        Assert.IsTrue(refreshed.Single(field => field.Key == "pptWatts").Writable);
        foreach (string key in new[] { "advancedCpuTuning", "temperatureLimitC", "splWatts", "spptWatts" })
            Assert.IsFalse(refreshed.Single(field => field.Key == key).Writable, key);

        foreach (string scalar in new[] { "mp1TemperatureC", "pptWatts" })
            Assert.AreEqual(CapabilityState.Available, HomeCapabilityCatalog.WithFieldAvailability(Items,
                fields.Select(field => field.Key == scalar ? field : field with { Writable = false }).ToArray())
                .Single(item => item.Key == "cpuTuning:smu").State);
        foreach (var state in new[] { CapabilityState.ReadOnly, CapabilityState.Unavailable })
        {
            var restricted = Items.Select(item => item.Key == "cpuTuning:smu" ? item with { State = state, Reason = "sourceDenied" } : item).ToArray();
            CollectionAssert.AreEqual(restricted, HomeCapabilityCatalog.WithFieldAvailability(restricted, fields));
        }
        foreach (string scalar in new[] { "mp1TemperatureC", "pptWatts" })
        {
        var mp1 = fields.Single(field => field.Key == scalar);
        foreach (var invalid in new[] { mp1 with { Writable = false }, mp1 with { Readable = false },
            mp1 with { Availability = CapabilityState.ReadOnly }, mp1 with { VerifiedAtUtc = null } })
        {
            var denied = HomeCapabilityCatalog.WithFieldAvailability(Items,
                fields.Select(field => field.Key == mp1.Key ? invalid : field with { Writable = false }).ToArray());
            Assert.AreEqual(CapabilityState.ReadOnly, denied.Single(item => item.Key == "cpuTuning:smu").State);
        }
        }
        foreach (var residual in new[] { new AdvancedCpuTuningPlan(PptWatts: 60, Mp1TemperatureC: 98),
            new(PptWatts: 44.999), new(PptWatts: 120.001), new(PptWatts: 60.0005), new(PptWatts: double.NaN), new(PptWatts: double.PositiveInfinity),
            new(Mp1TemperatureC: 98, TdcCurrentMilliamps: 108000), new(Mp1TemperatureC: 98, PboScalar: 2) })
            Assert.IsFalse((bool)Invoke(provider, "VerifyFreshDirectSmuLimit", residual, CancellationToken.None)!);
    }

    [TestMethod]
    public void Tdc_authority_is_independent_fresh_profile_scoped_and_raising_requires_headroom()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        var cpu = Cpu with { AdvancedLimits = Cpu.AdvancedLimits! with { TdcCurrentMilliamps = 108000 } };
        void Ready()
        {
            Seed(provider);
            Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(cpu.AdvancedLimits!, null, 0x540105));
        }
        Ready();
        var field = Fields(provider, cpu).Single(item => item.Key == "tdcCurrentMilliamps");
        Assert.IsTrue(field.Readable && field.Writable);
        Assert.AreEqual(1000d, field.Minimum);
        Assert.AreEqual(200000d, field.Maximum);
        Assert.AreEqual(1d, field.Step);
        Assert.AreEqual("mA", field.Unit);
        foreach (string key in new[] { "advancedCpuTuning", "temperatureLimitC", "splWatts", "spptWatts" })
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == key).Writable);
        foreach (var wrong in new[] { Identity with { BiosVersion = "V39" }, Identity with { CpuModel = "other" }, Identity with { BoardProduct = "other" } })
        {
            Ready(); Set(provider, "identity", wrong);
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "tdcCurrentMilliamps").Writable);
        }
        foreach (var limits in new AdvancedCpuTuningPlan[] { new(), new(TdcCurrentMilliamps: 999), new(TdcCurrentMilliamps: 200001), new(TdcCurrentMilliamps: 107999) })
        {
            Ready();Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(limits, null, 0x540105));
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "tdcCurrentMilliamps").Writable);
        }
        foreach (var time in new[] { DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(1) })
        {
            Ready();Set(provider, "smuLimitCacheAt", time);
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "tdcCurrentMilliamps").Writable);
        }
        Ready();Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(cpu.AdvancedLimits!, null, 0x540108));
        Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "tdcCurrentMilliamps").Writable);
        Ready();
        Assert.IsFalse((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(TdcCurrentMilliamps: 107999), null!)!);
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(TdcCurrentMilliamps: 108001), null!)!);
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(TdcCurrentMilliamps: 107999), new AdvancedCpuTuningPlan(TdcCurrentMilliamps: 107998))!);
        Assert.IsFalse((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(TdcCurrentMilliamps: 107998), new AdvancedCpuTuningPlan(TdcCurrentMilliamps: 107999))!);
        provider.SetBackgroundWorkPaused(true);
        Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "tdcCurrentMilliamps").Writable);
    }

    [TestMethod]
    public void Edc_authority_is_independent_fresh_profile_scoped_and_batch_prefix_raising_requires_headroom()
    {
        using var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        var cpu = Cpu with { AdvancedLimits = Cpu.AdvancedLimits! with { EdcCurrentMilliamps = 160000 } };
        void Ready()
        {
            Seed(provider);
            Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(cpu.AdvancedLimits!, null, 0x540105));
        }
        Ready();
        var field = Fields(provider, cpu).Single(item => item.Key == "edcCurrentMilliamps");
        Assert.IsTrue(field.Readable && field.Writable);
        Assert.AreEqual(1000d, field.Minimum);
        Assert.AreEqual(200000d, field.Maximum);
        Assert.AreEqual(1d, field.Step);
        Assert.AreEqual("mA", field.Unit);
        foreach (string key in new[] { "advancedCpuTuning", "temperatureLimitC", "splWatts", "spptWatts" })
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == key).Writable);
        foreach (var wrong in new[] { Identity with { BiosVersion = "V39" }, Identity with { CpuModel = "other" }, Identity with { BoardProduct = "other" } })
        {
            Ready(); Set(provider, "identity", wrong);
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "edcCurrentMilliamps").Writable);
        }
        foreach (int value in new[] { 1000, 159999, 200000 })
        {
            Ready();
            var limits = cpu.AdvancedLimits! with { EdcCurrentMilliamps = value };
            Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(limits, null, 0x540105));
            Assert.IsTrue(Fields(provider, cpu with { AdvancedLimits = limits }).Single(item => item.Key == "edcCurrentMilliamps").Writable);
        }
        foreach (var limits in new AdvancedCpuTuningPlan[] { new(), new(EdcCurrentMilliamps: 999), new(EdcCurrentMilliamps: 200001), new(EdcCurrentMilliamps: 159999) })
        {
            Ready(); Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(limits, null, 0x540105));
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "edcCurrentMilliamps").Writable);
        }
        Ready();
        Assert.IsFalse(Fields(provider, cpu with { AdvancedLimits = null }).Single(item => item.Key == "edcCurrentMilliamps").Writable);
        foreach (var time in new[] { DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(1) })
        {
            Ready(); Set(provider, "smuLimitCacheAt", time);
            Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "edcCurrentMilliamps").Writable);
        }
        Ready(); Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(cpu.AdvancedLimits!, null, 0x540108));
        Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "edcCurrentMilliamps").Writable);
        Ready(); Set(provider, "compatibilityDecision", new CompatibilityDecision(CompatibilityMode.ReadOnlySafeMode, new(Items), null, []));
        Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "edcCurrentMilliamps").Writable);
        Ready();
        Assert.IsFalse((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 159999), null!)!);
        Assert.IsFalse((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 160000), null!)!);
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 160001), null!)!);
        // The return to the original value is still an increase after a lower batch prefix.
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 160000), new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 159999))!);
        Assert.IsFalse((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 159998), new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 159999))!);
        Set(provider, "smuLimitCache", null);
        Assert.IsTrue((bool)Invoke(provider, "RequiresDirectSmuHeadroom", new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 159999), null!)!);
        Ready();
        provider.SetBackgroundWorkPaused(true);
        Assert.IsFalse(Fields(provider, cpu).Single(item => item.Key == "edcCurrentMilliamps").Writable);
    }

    private static void Seed(WindowsHomeHardwareProvider provider)
    {
        Set(provider, "identity", Identity);
        Set(provider, "compatibilityDecision", new CompatibilityDecision(CompatibilityMode.Writable, new(Items), null, []));
        Set(provider, "smuPmTableUnsupported", false);
        Set(provider, "smuLimitCache", new CpuSmuLimitSnapshot(Cpu.AdvancedLimits!, null, 0x540105));
        Set(provider, "smuLimitCacheAt", DateTimeOffset.UtcNow);
        var access = typeof(WindowsHomeHardwareProvider).GetField("smuLimitCacheAccess", Private);
        access?.SetValue(provider, Invoke(provider, "CaptureSensorAccess"));
    }

    private static FieldCapability[] Fields(WindowsHomeHardwareProvider provider, CpuTuningState? cpu = null) =>
        (FieldCapability[])Invoke(provider, "CreateFieldCapabilities", Items, cpu ?? Cpu, null!)!;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Set(object target, string field, object? value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static object? Get(object target, string field) => target.GetType().GetField(field, Private)!.GetValue(target);

    private sealed class NoIoTransport : ICpuTuningTransport
    {
        public int Calls { get; private set; }
        private void Fail() { Calls++; throw new AssertFailedException("No hardware access is permitted in this fixture"); }
        public Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken token) { Fail(); return Task.FromResult<object?>(null); }
        public Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken token) { Fail(); return Task.CompletedTask; }
        public Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken token) { Fail(); return Task.CompletedTask; }
    }
}
