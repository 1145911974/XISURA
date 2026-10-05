using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class CpuTuningProtocolTests
{
    [TestMethod]
    public async Task Oem_gate_is_enabled_before_native_mode_selection_and_not_reenabled_by_fields()
    {
        foreach (bool initiallyEnabled in new[] { false, true })
        {
            var bus = new ModePreservingPowerBus { Enabled = initiallyEnabled };
            var binding = new VerifiedWmiBinding("root\\WMI", "MICommonInterface", "MI", 250, 251, 23, "verified");
            var transport = new WindowsCpuTuningTransport(new MiCommonInterfaceClient(bus, bus), binding, null!);
            await transport.PrepareOemLimitsAsync([new CpuTuningPlan(75, 55, 60, null, null, null, null, null)], CancellationToken.None);
            Assert.IsTrue(bus.Enabled);
            bus.Mode = 0;
            await transport.WriteFieldAsync(CpuTuningField.TemperatureLimitC, 75, CancellationToken.None);
            Assert.AreEqual(0, bus.Mode);
            Assert.AreEqual(initiallyEnabled ? 0 : 1, bus.EnableWrites);
            Assert.AreEqual(1, bus.LimitWrites);
        }
        var noOem = new WindowsCpuTuningTransport(null!, null!, null!);
        await noOem.PrepareOemLimitsAsync([new CpuTuningPlan(null, null, null, 4000, null, null, null, null)], CancellationToken.None);
    }

    private sealed class ModePreservingPowerBus : IMiReadTransport, IMiWriteTransport
    {
        public bool Enabled { get; set; }
        public int Mode { get; set; }
        public int EnableWrites { get; private set; }
        public int LimitWrites { get; private set; }
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token) =>
            Task.FromResult<byte[]?>([0, 0, 0, 0, Enabled ? (byte)1 : (byte)0]);
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            if (request.Span[4] == 1) { Enabled = true; Mode = 1; EnableWrites++; }
            else LimitWrites++;
            return Task.FromResult<byte[]?>([]);
        }
    }

    [TestMethod]
    public void Mi_cpu_power_method_encodes_verified_fields_without_inventing_limit_readback()
    {
        CollectionAssert.AreEqual(new byte[] { 4, 95 }, MiCpuPowerCodec.Encode(CpuTuningField.TemperatureLimitC, 95));
        CollectionAssert.AreEqual(new byte[] { 2, 45 }, MiCpuPowerCodec.Encode(CpuTuningField.SplWatts, 45));
        CollectionAssert.AreEqual(new byte[] { 3, 65 }, MiCpuPowerCodec.Encode(CpuTuningField.SpptWatts, 65));

        var response = new byte[] { 0, 0, 0, 0, 1, 95, 45, 65 };
        Assert.IsNull(MiCpuPowerCodec.Read(response, CpuTuningField.TemperatureLimitC));
        Assert.IsNull(MiCpuPowerCodec.Read(response, CpuTuningField.SplWatts));
        Assert.IsNull(MiCpuPowerCodec.Read(response, CpuTuningField.SpptWatts));
        Assert.AreEqual(true, MiCpuPowerCodec.ReadCustomMode(response));
        Assert.IsNull(MiCpuPowerCodec.ReadCustomMode([0, 0, 0, 0, 95]));
        var bus = new CpuPowerReadback(response);
        var binding = new VerifiedWmiBinding("root\\WMI", "MICommonInterface", "fixture", 250, 251, 23, "fixture");
        var transport = new WindowsCpuTuningTransport(new MiCommonInterfaceClient(bus), binding, null!);
        Assert.IsTrue(transport.IsMiCpuPowerProtocolAvailable(CancellationToken.None));
        Assert.AreEqual(true, transport.OemCustomPowerMode);
        Assert.AreEqual(1, bus.ReadCount);
        bus.Response = [0, 0, 0, 0, 0];
        Assert.IsTrue(transport.IsMiCpuPowerProtocolAvailable(CancellationToken.None));
        Assert.AreEqual(false, transport.OemCustomPowerMode);
        bus.Response = [0, 0, 0, 0, 95];
        Assert.IsTrue(transport.IsMiCpuPowerProtocolAvailable(CancellationToken.None));
        Assert.IsNull(transport.OemCustomPowerMode);
        bus.Response = null;
        Assert.IsFalse(transport.IsMiCpuPowerProtocolAvailable(CancellationToken.None));
        Assert.IsNull(transport.OemCustomPowerMode);
        Assert.AreEqual(4, bus.ReadCount);
    }

    [TestMethod]
    public void Mi_cpu_power_writes_require_independent_hardware_readback()
    {
        var transport = new WindowsCpuTuningTransport(null!, null!, null!);

        Assert.IsTrue(transport.IsReadBackRequired(CpuTuningField.TemperatureLimitC));
        Assert.IsTrue(transport.IsReadBackRequired(CpuTuningField.SplWatts));
        Assert.IsTrue(transport.IsReadBackRequired(CpuTuningField.SpptWatts));
    }

    [TestMethod]
    public async Task Confirmed_oem_limits_verify_independent_hardware_readback()
    {
        var transport = new SingleFieldTransport();
        var controller = new PerformanceController(cpuTransport: transport);

        var temperature = await controller.ApplyCpuOemLimitAsync(
            Guid.NewGuid(), CpuTuningField.TemperatureLimitC, 90, CancellationToken.None);
        var spl = await controller.ApplyCpuOemLimitAsync(
            Guid.NewGuid(), CpuTuningField.SplWatts, 55, CancellationToken.None);
        var sppt = await controller.ApplyCpuOemLimitAsync(
            Guid.NewGuid(), CpuTuningField.SpptWatts, 75, CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, temperature.State);
        Assert.AreEqual(CommandState.Applied, spl.State);
        Assert.AreEqual(CommandState.Applied, sppt.State);
        Assert.IsTrue(temperature.HardwareReadBackConfirmed);
        Assert.IsTrue(spl.HardwareReadBackConfirmed);
        Assert.IsTrue(sppt.HardwareReadBackConfirmed);
        CollectionAssert.AreEqual(new[] {
            (CpuTuningField.TemperatureLimitC, 90),
            (CpuTuningField.SplWatts, 55),
            (CpuTuningField.SpptWatts, 75)
        }, transport.Writes.ToArray());
        Assert.AreEqual(6, transport.ReadCount);
        Assert.AreEqual(0, transport.RestoreCount);
    }

    [TestMethod]
    public async Task Confirmed_oem_limit_writes_enforce_software_ranges()
    {
        var transport = new SingleFieldTransport();
        var controller = new PerformanceController(cpuTransport: transport);

        var temperature = await controller.ApplyCpuOemLimitAsync(
            Guid.NewGuid(), CpuTuningField.TemperatureLimitC, 101, CancellationToken.None);
        var power = await controller.ApplyCpuOemLimitAsync(
            Guid.NewGuid(), CpuTuningField.SplWatts, 76, CancellationToken.None);
        var shortPower = await controller.ApplyCpuOemLimitAsync(
            Guid.NewGuid(), CpuTuningField.SpptWatts, 44, CancellationToken.None);

        Assert.AreEqual(CommandState.Rejected, temperature.State);
        Assert.AreEqual(CommandState.Rejected, power.State);
        Assert.AreEqual(ErrorCode.ValidationFailed, power.Error!.Code);
        Assert.AreEqual(CommandState.Rejected, shortPower.State);
        Assert.AreEqual(ErrorCode.ValidationFailed, shortPower.Error!.Code);
        Assert.AreEqual(0, transport.Writes.Count);
    }

    private sealed class CpuPowerReadback(byte[]? response) : IMiReadTransport
    {
        public byte[]? Response { get; set; } = response;
        public int ReadCount { get; private set; }
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Response);
        }
    }

    private sealed class SingleFieldTransport : ICpuTuningTransport
    {
        private readonly Dictionary<CpuTuningField, int> values = new() {
            [CpuTuningField.TemperatureLimitC] = 100, [CpuTuningField.SplWatts] = 82, [CpuTuningField.SpptWatts] = 120 };
        public List<(CpuTuningField Field, int Value)> Writes { get; } = [];
        public int ReadCount { get; private set; }
        public int RestoreCount { get; private set; }
        public Task<object?> ReadFieldAsync(CpuTuningField field, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult<object?>(values.GetValueOrDefault(field));
        }
        public Task WriteFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken) =>
            WriteConfirmedOemLimitAsync(field, (int)value!, cancellationToken);
        public Task RestoreFieldAsync(CpuTuningField field, object? value, CancellationToken cancellationToken)
        {
            RestoreCount++;
            values[field] = (int)value!;
            return Task.CompletedTask;
        }
        public Task WriteConfirmedOemLimitAsync(CpuTuningField field, int value, CancellationToken cancellationToken)
        {
            Writes.Add((field, value));
            values[field] = value;
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public void Cpu_tuning_binding_uses_method_23_when_manifest_allows_cpu_tuning()
    {
        var decision = new CompatibilityDecision(
            CompatibilityMode.Writable,
            new Jiaolong.Contracts.Models.CapabilitySnapshot(
                [new Jiaolong.Contracts.Models.CapabilityDescriptor("cpuTuning", Jiaolong.Contracts.Models.CapabilityState.Available, null)]),
            new CompatibilityManifest
            {
                ManifestId = "fixture",
                WmiProvider = new WmiProviderEvidence
                {
                    Namespace = "root\\WMI",
                    Class = "MICommonInterface",
                    InstanceName = "ACPI\\PNP0C14\\MIFS_0",
                    ReadType = 250,
                    WriteType = 251
                }
            },
            []);

        Assert.IsTrue(MiCpuPowerBindingFactory.TryCreate(decision, out var binding));
        Assert.AreEqual(23, binding!.MethodName);
        Assert.AreEqual(251, binding.WriteType);
    }

    [TestMethod]
    public void Windows_boost_mode_preserves_enabled_mode_and_disables_with_zero()
    {
        Assert.AreEqual(2, WindowsPowerBoostMode.SelectTarget(0, enabled: true));
        Assert.AreEqual(3, WindowsPowerBoostMode.SelectTarget(3, enabled: true));
        Assert.AreEqual(0, WindowsPowerBoostMode.SelectTarget(4, enabled: false));
        Assert.IsTrue(WindowsPowerBoostMode.IsEnabled(2));
        Assert.IsFalse(WindowsPowerBoostMode.IsEnabled(0));
    }

    [TestMethod]
    public void Dragon_range_curve_optimizer_encoding_matches_ryzen_smu_protocol()
    {
        Assert.AreEqual(28, RyzenSmuProtocol.DragonRangeCodeName);
        Assert.AreEqual(0x000F_FFF1u, RyzenSmuProtocol.EncodeCurve(-15));
        Assert.AreEqual(0x03B10524u, RyzenSmuProtocol.RaphaelCommandAddress);
        Assert.AreEqual(0x07u, RyzenSmuProtocol.SetCurveOptimizerCommand);
        Assert.AreEqual(0x03B10530u, RyzenSmuProtocol.Mp1CommandAddress);
        Assert.AreEqual(0x03B1057Cu, RyzenSmuProtocol.Mp1ResponseAddress);
        Assert.AreEqual(0x03B109C4u, RyzenSmuProtocol.Mp1ArgumentAddress);
        Assert.AreEqual(0x36u, RyzenSmuProtocol.SetCurveOptimizerMp1Command);
        var emptyCpu = new CpuTuningPlan(null, null, null, null, null, null, null, null);
        foreach (var removed in new AdvancedCpuTuningPlan[] {
            new(OverclockEnabled: true, OcClockMhz: 3800), new(OverclockEnabled: false),
            new(OcVoltageMillivolts: 1000), new(PerCoreOcClockMhz: new Dictionary<int,int> { [0] = 3800 }) })
            Assert.IsNotNull(CommandValidation.Validate(new SetCpuTuningCommand(Guid.NewGuid(), emptyCpu with { Advanced = removed }, true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetGpuPowerLimitCommand(Guid.NewGuid(), 80, true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetGpuPowerPolicyCommand(Guid.NewGuid(), 100000, 90000, true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetCpuTuningBatchCommand(Guid.NewGuid(), [
            emptyCpu with { SpptWatts = 55 }, emptyCpu with { Advanced = new(FastPptWatts: 65) } ], true)));
        var nativeBatch = new SetCpuTuningBatchCommand(Guid.NewGuid(), [emptyCpu with { BoostEnabled = false }], true);
        Assert.IsNull(nativeBatch.NativeMode);
        foreach (var mode in new[] { PerformanceMode.Quiet, PerformanceMode.Balanced, PerformanceMode.Turbo })
            Assert.IsNull(CommandValidation.Validate(nativeBatch with { NativeMode = mode }));
        foreach (var mode in new[] { PerformanceMode.Custom, (PerformanceMode)99 })
            Assert.IsNotNull(CommandValidation.Validate(nativeBatch with { NativeMode = mode }));
        HardwareCommand batchCommand = nativeBatch with { NativeMode = PerformanceMode.Balanced };
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<HardwareCommand>(System.Text.Json.JsonSerializer.Serialize(batchCommand));
        Assert.IsInstanceOfType<SetCpuTuningBatchCommand>(roundtrip);
        Assert.AreEqual(PerformanceMode.Balanced, ((SetCpuTuningBatchCommand)roundtrip!).NativeMode);
        float[] table = new float[64];
        table[0] = 82.00001f; table[2] = 120.00001f; table[4] = 82.00001f;
        table[8] = 108.00001f; table[10] = 100; table[62] = 160;
        var limits = RyzenSmuAdvancedLimits.DecodePmTable(0x540108, table);
        Assert.AreEqual(82d, limits.StapmWatts);
        Assert.AreEqual(120d, limits.FastPptWatts);
        Assert.AreEqual(limits.FastPptWatts, limits.PptWatts);
        Assert.AreEqual(108000, limits.TdcCurrentMilliamps);
        Assert.AreEqual(160000, limits.EdcCurrentMilliamps);
        Assert.IsInstanceOfType<int>(RyzenSmuAdvancedLimits.ReadField(limits, CpuTuningField.EdcCurrentMilliamps));
        table[6] = 82.00001f;
        Assert.AreEqual(82, RyzenSmuAdvancedLimits.DecodeSnapshot(0x540108, table).OemSplWatts);
        Assert.AreEqual(limits.Mp1TemperatureC, limits.RsmuTemperatureC);
        Assert.IsNull(RyzenSmuAdvancedLimits.BuildSingle(new(FastPptWatts: 120)));
        Assert.Throws<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodePmTable(0x540104, table));
        table[2] = float.NaN;
        Assert.Throws<InvalidOperationException>(() => RyzenSmuAdvancedLimits.DecodePmTable(0x540108, table));
    }
}
