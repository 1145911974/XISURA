using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Mode_entry_legacy_records_native_cpu_and_lighting_effects_and_restores_originals()
    {
        using var fixture = new Fixture();
        fixture.Provider.DirectLimitsEnabled = true;
        fixture.Provider.ProductionModeAcknowledgment = true;
        var handle = await fixture.Begin(null);
        fixture.Provider.ModeWriteEffects = () =>
        {
            fixture.Provider.DirectPpt = 55;
            fixture.Provider.DirectTdc = 80000;
            fixture.Provider.SetExternalCoreCurve(0, -10);
            fixture.Provider.SetExternalLight(Fixture.OriginalLight with { Red = 10 });
        };
        var applied = await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), handle);
        Assert.AreEqual(CommandState.Applied, applied.State);
        Assert.IsNull(applied.VerifiedPerformanceMode);
        Assert.AreEqual("InterfaceAcknowledged", applied.ModeEvidence!.VerificationLevel);
        var saved = (await fixture.Journal.ReadAsync(CancellationToken.None)).Current!;
        Assert.AreEqual(55d, saved.Fields["performance.pptWatts"].LastWritten!.Value!.Value.GetDouble());
        Assert.AreEqual(OriginalCurve, saved.Fields["performance.negativeCurveOptimizer"].BaselineDetails!.PerCoreCurveOptimizer![0]);
        Assert.AreEqual(-10, saved.Fields["performance.negativeCurveOptimizer"].LastWrittenDetails!.PerCoreCurveOptimizer![0]);
        Assert.IsTrue(saved.Fields.Keys.Any(key => key.StartsWith("lighting.")));
        fixture.Provider.ModeWriteEffects = () => fixture.Provider.DirectPpt = 50;
        await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), handle);
        saved = (await fixture.Journal.ReadAsync(CancellationToken.None)).Current!;
        Assert.AreEqual(120.125, saved.Fields["performance.pptWatts"].Baseline!.Value!.Value.GetDouble());
        Assert.AreEqual(50d, saved.Fields["performance.pptWatts"].LastWritten!.Value!.Value.GetDouble());
        fixture.Provider.ModeWriteEffects = null;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
        Assert.AreEqual(108000, fixture.Provider.DirectTdc);
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
    }

    [TestMethod]
    public async Task Mode_entry_configuration_repeated_modes_preserve_existing_owner_baseline()
    {
        using var fixture = new Fixture();
        fixture.Provider.DirectLimitsEnabled = true;
        fixture.Provider.ProductionModeAcknowledgment = true;
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        fixture.Provider.ModeWriteEffects = () => { fixture.Provider.DirectPpt -= 10; fixture.Provider.SetExternalCurve(-10); };
        foreach (var mode in new[] { PerformanceMode.Balanced, PerformanceMode.Quiet })
        {
            var id = Guid.NewGuid();
            var result = await fixture.Write(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.ManualLive,
                ControlModeId.Gaming, mode, [])), handle);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.IsNull(result.VerifiedPerformanceMode);
            Assert.AreEqual(ApplyState.SubmittedUnverified, result.ExecutionState!.NativeModeApplyState);
        }
        var saved = (await fixture.Journal.ReadAsync(CancellationToken.None)).Current!;
        Assert.AreEqual(120.125, saved.Fields["performance.pptWatts"].Baseline!.Value!.Value.GetDouble());
        Assert.AreEqual(100.125, saved.Fields["performance.pptWatts"].LastWritten!.Value!.Value.GetDouble());
        Assert.AreEqual(OriginalCurve, saved.Fields["performance.negativeCurveOptimizer"].BaselineDetails!.PerCoreCurveOptimizer![0]);
        fixture.Provider.ModeWriteEffects = null;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
    }

    [TestMethod]
    public async Task Mode_entry_does_not_claim_values_already_changed_externally_before_dispatch()
    {
        foreach (string? fault in new string?[] { null, "missing", "requested", "parsed", "level" })
        {
            using var fixture = new Fixture();
            fixture.Provider.DirectLimitsEnabled = true;
            fixture.Provider.ProductionModeAcknowledgment = true;
            fixture.Provider.ModeAcknowledgmentFault = fault;
            var handle = await fixture.Begin(null);
            if (fault is null) fixture.Provider.DirectPpt = 88;
            fixture.Provider.ModeWriteEffects = () => fixture.Provider.DirectPpt = 55;
            await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), handle);
            Assert.IsFalse((await fixture.Journal.ReadAsync(CancellationToken.None)).Current!.Fields.ContainsKey("performance.pptWatts"), fault);
        }
    }

    [TestMethod]
    public async Task Mode_entry_external_change_after_checkpoint_still_blocks_exit()
    {
        using var fixture = new Fixture();
        fixture.Provider.DirectLimitsEnabled = true;
        fixture.Provider.ProductionModeAcknowledgment = true;
        var handle = await fixture.Begin(null);
        fixture.Provider.ModeWriteEffects = () => fixture.Provider.DirectPpt = 55;
        await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), handle);
        Assert.AreEqual(55d, (await fixture.Journal.ReadAsync(CancellationToken.None)).Current!
            .Fields["performance.pptWatts"].LastWritten!.Value!.Value.GetDouble());
        fixture.Provider.ModeWriteEffects = null;
        fixture.Provider.DirectPpt = 77;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(77d, fixture.Provider.DirectPpt);
    }
}
