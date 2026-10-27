using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<Guid> SeedPendingMode(Fixture fixture, ClientControlSessionHandle handle, bool applied = true)
    {
        fixture.Provider.UnconfirmedNextMode = true;
        fixture.Provider.ModeFailureApplies = applied;
        var id = Guid.NewGuid();
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await fixture.Write(new SetPerformanceModeCommand(id, PerformanceMode.Balanced), handle)).State);
        return id;
    }

    [TestMethod]
    public async Task Prior_pending_mode_observes_before_or_target_then_exits_under_original_scope_without_claiming_external_before()
    {
        foreach (string state in new[] { "before", "target", "external-before" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            if (state == "external-before") fixture.Provider.SetExternalMode(PerformanceMode.Quiet);
            await SeedPendingMode(fixture, handle, state == "target");
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindGpuJournal(journal);
            fixture.BindModeJournal(journal);
            int before = fixture.Provider.NativeModeScopes.Count;
            var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, state + " " + result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(state == "external-before" ? PerformanceMode.Quiet : PerformanceMode.Turbo, fixture.Provider.Mode);
            if (state == "target") Assert.IsTrue(fixture.Provider.NativeModeScopes.Count > before);
            Assert.IsTrue(fixture.Provider.NativeModeScopes.All(scope => scope.ServiceSessionId == fixture.Runtime.SessionId &&
                scope.ClientSessionId == handle.SessionId && scope.HardwareEpoch == handle.HardwareEpoch));
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Prior_pending_mode_preserves_owned_cpu_windows_gpu_and_light_until_exit_restores_them()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 5 }), handle);
        await SeedPendingMode(fixture, handle);
        var journal = new ControlExecutionJournal(fixture.Root);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.BindGpuJournal(journal);
        fixture.BindModeJournal(journal);
        var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
    }

    [TestMethod]
    public async Task Prior_pending_mode_rejects_unproven_cpu_change_unstable_mode_and_foreign_record_without_writes()
    {
        foreach (string fault in new[] { "cpu", "unstable", "session", "kind" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            await SeedPendingMode(fixture, handle);
            if (fault == "cpu") fixture.Provider.SetExternalAcFrequency(4300);
            if (fault == "unstable") fixture.Provider.ChangeModeOnSecondEvidence = true;
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var pending = saved.Unresolved.Single();
            if (fault == "session") pending = pending with { NativeModeAttempt = pending.NativeModeAttempt! with { ServiceSessionId = Guid.NewGuid() } };
            if (fault == "kind") pending = pending with { CommandKind = nameof(ApplyControlRequestCommand) };
            await new AtomicJsonStore(fixture.Root, "control-execution.json").WriteAsync(saved with { Unresolved = [pending] }, CancellationToken.None);
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.BindGpuJournal(journal);
            fixture.BindModeJournal(journal);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(1, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
        }
    }

    [TestMethod]
    public async Task Prior_pending_mode_changed_after_preflight_keeps_original_record_and_can_retry_without_replaying_it()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        var original = await SeedPendingMode(fixture, handle);
        fixture.Provider.ChangeModeOnEvidenceRead = 4;
        var journal = new ControlExecutionJournal(fixture.Root);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.BindGpuJournal(journal);
        fixture.BindModeJournal(journal);
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(original, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        fixture.Provider.ChangeModeOnEvidenceRead = 0;
        fixture.Provider.SetExternalMode(PerformanceMode.Balanced);
        var result = await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
    }
}
