using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(ClientControlSessionHandle Handle, Guid Operation)> SeedPendingGpu(Fixture fixture, string kind)
    {
        var handle = await fixture.Begin(null);
        if (kind == "core") Assert.AreEqual(CommandState.Applied,
            (await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle)).State);
        var id = Guid.NewGuid();
        HardwareCommand command = kind switch
        {
            "core" => new SetGpuCoreOffsetCommand(id, 0, 10000, true) { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() },
            "memory" => new SetGpuMemoryOffsetCommand(id, 0, 1000, true),
            _ => new SetGpuVfCurveCommand(id, fixture.Provider.Offsets(), Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 10000).ToArray(), true)
        };
        fixture.Provider.FailNextGpuWrite = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(command, handle)).State);
        return (handle, id);
    }

    [TestMethod]
    public async Task Prior_pending_gpu_restores_core_memory_and_vf_under_original_scope_then_factory_exit()
    {
        foreach (string kind in new[] { "core", "memory", "vf" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedPendingGpu(fixture, kind);
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            Assert.AreNotEqual(fixture.Runtime.SessionId, runtime.SessionId);
            int originalAdmissions = 0;
            fixture.Provider.ReadRecoveryScope = () =>
            {
                var scope = journal.CurrentGpuOperation;
                if (scope?.OperationId == original.Operation)
                {
                    originalAdmissions++;
                    Assert.AreEqual(fixture.Runtime.SessionId, scope.SessionId);
                    Assert.AreEqual(original.Handle.HardwareEpoch, scope.HardwareEpoch);
                    Assert.AreEqual(fixture.Owner.UserSid, scope.OwnerSid);
                }
                return scope?.OperationId;
            };
            fixture.Provider.RecoverOnRequest = true;
            var result = await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, kind + " " + result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.IsTrue(originalAdmissions > 0);
            Assert.AreEqual(original.Operation, fixture.Provider.RecoveredOperationId);
            Assert.AreEqual(kind == "core" ? 1000 : 0, fixture.Provider.RecoveredMemoryBeforeExit);
            CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
            Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
            Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
            var nextOwner = fixture.Owner with { ProcessId = 43 };
            var next = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), nextOwner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, next.State);
            int writes = fixture.Provider.WriteCount;
            var stale = await runtime.ExecuteAsync(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true)
                { ClientSessionId = original.Handle.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
            Assert.AreNotEqual(CommandState.Applied, stale.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
        }
    }

    [TestMethod]
    public async Task Prior_pending_gpu_failure_retains_original_attempt_and_retries_same_or_new_runtime()
    {
        foreach (bool restart in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedPendingGpu(fixture, "core");
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
            Assert.AreEqual(original.Operation, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
            if (restart)
            {
                journal = new(fixture.Root);
                runtime = new(fixture.Provider, journal: journal, requireClientSessions: true,
                    readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            }
            fixture.Provider.RecoverOnRequest = true;
            var result = await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(1000, fixture.Provider.RecoveredMemoryBeforeExit);
            Assert.AreEqual(1, fixture.Provider.GpuRestorationWrites);
        }
    }

    [TestMethod]
    public async Task Prior_pending_gpu_rejects_foreign_service_kind_or_missing_attempt_before_dispatch()
    {
        foreach (string fault in new[] { "session", "kind", "attempt" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedPendingGpu(fixture, "memory");
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var pending = saved.Unresolved.Single();
            if (fault == "session")
            {
                var foreign = Guid.NewGuid();
                pending = pending with { GpuRecovery = pending.GpuRecovery! with { SessionId = foreign },
                    GpuAttempt = pending.GpuAttempt! with { Before = pending.GpuAttempt.Before with { SessionId = foreign } } };
            }
            if (fault == "kind") pending = pending with { CommandKind = nameof(SetPerformanceModeCommand) };
            if (fault == "attempt") pending = pending with { GpuAttempt = null };
            await new AtomicJsonStore(fixture.Root, "control-execution.json").WriteAsync(saved with { Unresolved = [pending] }, CancellationToken.None);
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
            fixture.Provider.RecoverOnRequest = true;
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(0, fixture.Provider.GpuRecoveries);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
        }
    }

    [TestMethod]
    public async Task Prior_pending_gpu_retries_lost_native_confirmation_without_repeating_restoration_writes()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var original = await SeedPendingGpu(fixture, "memory");
        var journal = new ControlExecutionJournal(fixture.Root);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
        fixture.Provider.RecoverOnRequest = true;
        fixture.Provider.FailGpuReconcileOnce = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(original.Operation, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        Assert.AreEqual(1, fixture.Provider.GpuRestorationWrites);
        var result = await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(2, fixture.Provider.GpuRecoveries);
        Assert.AreEqual(1, fixture.Provider.GpuRestorationWrites);
    }
}
