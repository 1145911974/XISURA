using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<Guid> SeedPendingCpu(Fixture fixture, string kind)
    {
        if (kind == "boost") fixture.Provider.SetExternalBoost(2, 4);
        var handle = await fixture.Begin(kind == "curve" ? -30 : null);
        if (kind == "curve") await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            new(null, null, null, null, null, null, null, -20), true), handle);
        if (kind == "frequency") await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            new(null, null, null, null, null, null, null, null) { AcMaxFrequencyMhz = 4400, DcMaxFrequencyMhz = 4500 }, true), handle);
        CpuTuningPlan plan = kind switch
        {
            "curve" => new(null, null, null, null, null, null, null, -5),
            "boost" => new(null, null, null, null, false, null, null, null),
            _ => new(null, null, null, null, null, null, null, null) { AcMaxFrequencyMhz = 4600, DcMaxFrequencyMhz = 4300 }
        };
        fixture.Provider.FailNextCpuWrite = true;
        var id = Guid.NewGuid();
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetCpuTuningCommand(id, plan, true), handle)).State);
        if (kind == "curve") Assert.IsTrue(fixture.Provider.Cpu.PerCoreCurveOptimizer!.All(pair => pair.Value == (pair.Key % 2 == 0 ? -20 : -5)));
        if (kind == "boost")
        {
            var actual = HomeServiceRuntime.CaptureCpuRestoration("isBoostEnabled", fixture.Provider.Cpu)!;
            Assert.AreEqual(0, actual.AcBoostMode);
            Assert.AreEqual(4, actual.DcBoostMode);
        }
        if (kind == "frequency")
        {
            Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
            Assert.AreEqual(4500, fixture.Provider.Cpu.DcFrequency());
        }
        return id;
    }

    [TestMethod]
    public async Task Prior_pending_cpu_restores_partial_curve_boost_and_frequency_from_exact_write_markers()
    {
        foreach (string kind in new[] { "curve", "boost", "frequency" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedPendingCpu(fixture, kind);
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
            var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, kind + " " + result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(kind == "curve" ? -30 : OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
            Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
            if (kind == "boost")
            { Assert.AreEqual(2, fixture.Provider.Cpu.AcBoostMode); Assert.AreEqual(4, fixture.Provider.Cpu.DcBoostMode); }
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Prior_pending_cpu_partial_final_restore_keeps_original_record_and_retries_same_or_new_runtime()
    {
        foreach (bool restart in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedPendingCpu(fixture, "curve");
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
            fixture.Provider.FailCpuRestoreAfterHalf = true;
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
            Assert.AreEqual(original, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
            if (restart)
            {
                journal = new(fixture.Root);
                runtime = new(fixture.Provider, journal: journal, requireClientSessions: true,
                    readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
                fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
            }
            var result = await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        }
    }

    [TestMethod]
    public async Task Prior_pending_cpu_rejects_missing_exact_markers_external_core_or_wrong_kind_without_writes()
    {
        foreach (string fault in new[] { "before", "target", "external", "kind" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedPendingCpu(fixture, "curve");
            if (fault == "external") fixture.Provider.SetExternalCoreCurve(0, -7);
            var path = Path.Combine(fixture.Root, "control-execution.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            if (fault is "before" or "target")
            {
                string property = fault == "before" ? "writeBeforeDetails" : "writeTargetDetails";
                node["current"]!["fields"]!["performance.negativeCurveOptimizer"]![property] = null;
                node["unresolved"]!.AsArray()[0]!["state"]!["fields"]!["performance.negativeCurveOptimizer"]![property] = null;
            }
            if (fault == "kind") node["unresolved"]!.AsArray()[0]!["commandKind"] = nameof(ApplyControlRequestCommand);
            if (fault != "external") File.WriteAllText(path, node.ToJsonString());
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(1, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Prior_pending_cpu_keeps_original_operation_if_windows_changes_after_restore_confirmation()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        Guid operation = await SeedPendingCpu(fixture, "frequency");
        bool changed = false;
        fixture.Provider.ReadBaselineOverride = controls =>
        {
            if (!changed && controls.CpuTuning!.AcFrequency() == OriginalFrequency)
            {
                fixture.Provider.SetExternalAcFrequency(4400);
                changed = true;
            }
            return controls;
        };
        var journal = new ControlExecutionJournal(fixture.Root);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(
            new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
        Assert.IsTrue(changed);
        Assert.AreEqual(4400, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(operation, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
    }

    [TestMethod]
    public async Task Cpu_pending_marker_contains_exact_before_and_target_before_first_native_dispatch()
    {
        using var fixture = new Fixture();
        fixture.Provider.SetExternalBoost(2, 4);
        var handle = await fixture.Begin(null);
        int checkedMarkers = 0;
        fixture.Provider.BeforeWrite = async command =>
        {
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var curve = saved.Current!.Fields["performance.negativeCurveOptimizer"];
            Assert.IsTrue(curve.WriteBeforeDetails!.PerCoreCurveOptimizer!.Values.All(value => value == OriginalCurve));
            Assert.IsTrue(curve.WriteTargetDetails!.PerCoreCurveOptimizer!.Values.All(value => value == -5));
            var boost = saved.Current.Fields["performance.isBoostEnabled"];
            Assert.AreEqual(2, boost.WriteBeforeDetails!.AcBoostMode);
            Assert.AreEqual(4, boost.WriteBeforeDetails.DcBoostMode);
            Assert.AreEqual(0, boost.WriteTargetDetails!.AcBoostMode);
            Assert.AreEqual(0, boost.WriteTargetDetails.DcBoostMode);
            checkedMarkers++;
        };
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            new(null, null, null, null, false, null, null, -5), true), handle)).State);
        Assert.AreEqual(1, checkedMarkers);
    }
}
