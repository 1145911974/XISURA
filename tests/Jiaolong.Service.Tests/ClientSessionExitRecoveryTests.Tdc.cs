using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static CpuTuningPlan TdcPlan(int value) => new(null, null, null, null, null, null, null, null)
    { Advanced = new(TdcCurrentMilliamps: value) };

    [TestMethod]
    public async Task Tdc_single_field_wal_keeps_first_baseline_and_formal_exit_restores_without_alias_writes()
    {
        using var fixture = new Fixture();
        fixture.Provider.DirectLimitsEnabled = true;
        var handle = await fixture.Begin(null);
        int checks = 0;
        fixture.Provider.BeforeWrite = async _ =>
        {
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(1, saved.Current!.Fields.Count);
            var field = saved.Current.Fields["performance.tdcCurrentMilliamps"];
            Assert.AreEqual(ApplyState.Unknown, field.WriteState);
            Assert.AreEqual(108000, field.Baseline!.Value!.Value.GetInt32());
            Assert.IsNotNull(field.WriteBefore);
            Assert.IsNull(saved.Current.NativeModeOwnerClientSessionId);
            checks++;
        };
        foreach (int target in new[] { 107999, 107998 })
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), TdcPlan(target), true), handle)).State);
        Assert.AreEqual(2, checks);
        fixture.Provider.BeforeWrite = null;
        fixture.Provider.SetActiveScheme(Fixture.OtherScheme);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(108000, fixture.Provider.DirectTdc);
        Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
        Assert.AreEqual(99, fixture.Provider.DirectTemperature);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        var after = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, after.Current!.Fields.Count);
        Assert.AreEqual(0, after.Unresolved.Length);
    }

    [TestMethod]
    public async Task Tdc_pending_restart_checks_ownership_and_retains_parent_on_restore_failure()
    {
        foreach (string fault in new[] { "restore", "external", "none" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.Provider.DirectLimitsEnabled = true;
            var handle = await fixture.Begin(null);
            fixture.Provider.InterruptCpuBatchAfterPlans = 1;
            var command = new SetCpuTuningBatchCommand(Guid.NewGuid(), [TdcPlan(107999), TdcPlan(107998)], true);
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(command, handle)).State);
            Assert.AreEqual(107999, fixture.Provider.DirectTdc);
            if (fault == "external") fixture.Provider.DirectTdc = 107997;
            if (fault == "restore") fixture.Provider.FailDirectRestoreOnce = "tdcCurrentMilliamps";
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None);
            if (fault == "external")
            {
                Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
                Assert.AreEqual(writes, fixture.Provider.WriteCount);
                AssertOriginalRecords(fixture, records);
                continue;
            }
            if (fault == "restore")
            {
                Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
                Assert.AreEqual(command.OperationId, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
                result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None);
            }
            Assert.AreEqual(CommandState.Applied, result!.State);
            Assert.AreEqual(108000, fixture.Provider.DirectTdc);
            Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }
}
