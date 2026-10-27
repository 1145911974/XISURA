using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class MiProviderLifecycleTests
{
    private static object? InvokeMappingProvider(WindowsHomeHardwareProvider provider, string name)
    {
        var method = typeof(WindowsHomeHardwareProvider).GetMethod(name, Private);
        Assert.IsNotNull(method, name);
        try { return method.Invoke(provider, null); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    [TestMethod]
    public async Task Mapping_fan_failed_open_rolls_back_completed_mapping_without_unmapped_target_read()
    {
        foreach (bool lostAck in new[] { false, true })
        {
            var ports = new BldingLifecycleTests.PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
            ports.Registers[0x1060] = 0;
            ports.Registers[0xC83C] = 23; ports.Registers[0xC83D] = 29;
            ports.IgnoreValueWrite = (address, _) => address == 0x1060;
            using var fixture = await FirmwareFixture.Create(1, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
            fixture.Prepare();
            Assert.AreEqual((byte)0, fixture.Bus.Value);
            var transport = (BldingFanEcTransport)InvokeMappingProvider(fixture.Provider, "CreateFanTransport")!;
            typeof(WindowsHomeHardwareProvider).GetField("fanTransport", Private)!.SetValue(fixture.Provider, transport);
            if (lostAck) ports.AfterTransfer = _ => { if (ports.ValueWrites == 1) throw new IOException("fakeMappingLostAck"); };
            Assert.AreEqual(lostAck ? "fakeMappingLostAck" : "bldingFanMappingNotConfirmed",
                Assert.ThrowsExactly<IOException>(transport.Open).Message);
            ports.AfterTransfer = null;

            InvokeMappingProvider(fixture.Provider, "ReleaseFanLocked");
            Assert.IsTrue(transport.LeaseCompleted);
            Assert.IsTrue(transport.ReadMappingState()!.Restored);
            Assert.IsNull(InvokeMappingProvider(fixture.Provider, "ReadFanEcControlLocked"));
            Assert.AreEqual((byte)1, fixture.Bus.Value, "Rollback must immediately restore the original firmware flag.");
            Assert.AreEqual(1, ports.ValueWrites, "Only the failed mapping write was attempted; rollback needs no value write.");
            Assert.AreEqual((byte)0, ports.Registers[0x1060]);
            Assert.AreEqual((byte)23, ports.Registers[0xC83C]);
            Assert.AreEqual((byte)29, ports.Registers[0xC83D]);
            Assert.AreEqual(0, ports.UnmappedTargetReads);
            var checkpoint = (await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
            Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(checkpoint));
            Assert.IsFalse(checkpoint.MappingLease!.TargetsCaptured);
            Assert.IsFalse(checkpoint.MappingLease.TargetWritesStarted);
            int writes = ports.ValueWrites;
            InvokeMappingProvider(fixture.Provider, "FinishFanLocked");
            Assert.AreEqual(writes, ports.ValueWrites, "A completed rollback must remain safe to finish again.");
        }
        foreach (bool persistBeforeThrow in new[] { false, true })
        {
            var ports = new BldingLifecycleTests.PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
            ports.Registers[0x1060] = 0;
            ports.IgnoreValueWrite = (address, _) => address == 0x1060;
            bool rejectCompletion = true;
            using var fixture = await FirmwareFixture.Create(0, (allowed, readAllowed, writer) => ports.Create(record =>
            {
                if (persistBeforeThrow) writer!(record);
                if (rejectCompletion && record.MappingLease?.Restored == true) throw new IOException("fakeFinalCheckpointUnavailable");
                if (!persistBeforeThrow) writer!(record);
            }, allowed, readAllowed));
            var transport = (BldingFanEcTransport)InvokeMappingProvider(fixture.Provider, "CreateFanTransport")!;
            typeof(WindowsHomeHardwareProvider).GetField("fanTransport", Private)!.SetValue(fixture.Provider, transport);
            Assert.AreEqual("fakeFinalCheckpointUnavailable", Assert.ThrowsExactly<IOException>(transport.Open).Message);
            Assert.ThrowsExactly<IOException>(() => InvokeMappingProvider(fixture.Provider, "ReleaseFanLocked"));
            Assert.IsFalse(transport.LeaseCompleted, "An unacknowledged final checkpoint must still block successful rollback.");
            rejectCompletion = false;
            InvokeMappingProvider(fixture.Provider, "ReleaseFanLocked");
            Assert.IsTrue(transport.LeaseCompleted);
            Assert.IsTrue(transport.ReadMappingState()!.Restored);
            Assert.AreEqual(1, ports.ValueWrites);
            Assert.AreEqual(0, ports.UnmappedTargetReads);
        }
    }

    [TestMethod]
    public async Task Mapping_fan_provider_keeps_lease_on_release_and_observes_final_unmapped_state()
    {
        var ports = new BldingLifecycleTests.PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
        ports.Registers[0x1060] = 1;
        using var fixture = await FirmwareFixture.Create(0, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
        CommandResult Release(ReleaseReason reason) => (CommandResult)typeof(WindowsHomeHardwareProvider)
            .GetMethod("ExecuteFanLocked", Private)!.Invoke(fixture.Provider,
                [new ReleaseFanControlCommand(Guid.NewGuid(), reason), CancellationToken.None])!;
        byte[] idleJournal = File.ReadAllBytes(fixture.Path);
        foreach (var reason in new[] { ReleaseReason.UserRequested, ReleaseReason.SessionEnded })
        {
            var idleRelease = Release(reason);
            Assert.AreEqual(CommandState.Applied, idleRelease.State);
            Assert.AreEqual(HardwareMutation.NoMutation, idleRelease.Mutation);
            Assert.AreEqual(0, ports.ValueWrites);
            Assert.AreEqual(0, fixture.Bus.Writes);
            CollectionAssert.AreEqual(idleJournal, File.ReadAllBytes(fixture.Path));
            Assert.IsNull((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery);
        }
        var transport = (BldingFanEcTransport)InvokeMappingProvider(fixture.Provider, "CreateFanTransport")!;
        typeof(WindowsHomeHardwareProvider).GetField("fanTransport", Private)!.SetValue(fixture.Provider, transport);
        transport.SetTargets(40, 41);
        var released = Release(ReleaseReason.UserRequested);
        Assert.AreEqual(CommandState.Applied, released.State);
        Assert.AreEqual(HardwareMutation.Changed, released.Mutation);
        Assert.AreSame(transport, typeof(WindowsHomeHardwareProvider).GetField("fanTransport", Private)!.GetValue(fixture.Provider));
        Assert.IsFalse(ControlExecutionJournal.FanCheckpointComplete((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint));
        InvokeMappingProvider(fixture.Provider, "FinishFanLocked");
        var observation = InvokeMappingProvider(fixture.Provider, "ReadFanEcMappingLocked");
        Assert.IsNotNull(observation);
        Assert.IsTrue((bool)observation.GetType().GetProperty("Restored")!.GetValue(observation)!);
        Assert.IsNull(InvokeMappingProvider(fixture.Provider, "ReadFanEcControlLocked"));
        Assert.AreEqual((byte)1, ports.Registers[0x1060]);
        Assert.AreEqual(0, ports.UnmappedTargetReads);
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint));
        int finalWrites = ports.ValueWrites;
        var finishedAgain = Release(ReleaseReason.SessionEnded);
        Assert.AreEqual(CommandState.Applied, finishedAgain.State);
        Assert.AreEqual(HardwareMutation.Unknown, finishedAgain.Mutation,
            "A retained transport is still owned even when its completed lease needs no further write.");
        Assert.AreEqual(finalWrites, ports.ValueWrites);
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint));
    }

    [TestMethod]
    public async Task Mapping_fan_provider_new_process_restores_mapping_only_or_partial_target_checkpoint()
    {
        {
            var ports = new BldingLifecycleTests.PortBus { MappingGatesTargets = true };
            ports.Registers[0x1060] = 1; ports.Registers[0xC83C] = 23; ports.Registers[0xC83D] = 29;
            bool lostWalAck = false;
            using var fixture = await FirmwareFixture.Create(0, (allowed, readAllowed, writer) => ports.Create(record =>
            {
                writer!(record);
                if (!lostWalAck && record.MappingLease?.TargetWritesStarted == true)
                { lostWalAck = true; throw new IOException("fakePersistedWalLostAck"); }
            }, allowed, readAllowed));
            var transport = (BldingFanEcTransport)InvokeMappingProvider(fixture.Provider, "CreateFanTransport")!;
            typeof(WindowsHomeHardwareProvider).GetField("fanTransport", Private)!.SetValue(fixture.Provider, transport);
            Assert.ThrowsExactly<IOException>(() => transport.SetTargets(40, 41));
            int writes = ports.ValueWrites;
            Assert.ThrowsExactly<System.IO.InvalidDataException>(() => InvokeMappingProvider(fixture.Provider, "ReleaseFanLocked"));
            Assert.AreEqual(writes, ports.ValueWrites, "A lost WAL acknowledgement must retain the journal rather than downgrade its sticky stage.");
            Assert.AreEqual((byte)23, ports.Registers[0xC83C]);
            Assert.AreEqual((byte)29, ports.Registers[0xC83D]);
            var pending = (await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
            Assert.IsTrue(pending.MappingLease!.TargetWritesStarted);
            Assert.IsTrue(pending.Pending.ContainsKey(0xC83D));
        }
        {
            var ports = new BldingLifecycleTests.PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
            ports.Registers[0x1060] = 1;
            using var fixture = await FirmwareFixture.Create(0, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
            var transport = (BldingFanEcTransport)InvokeMappingProvider(fixture.Provider, "CreateFanTransport")!;
            typeof(WindowsHomeHardwareProvider).GetField("fanTransport", Private)!.SetValue(fixture.Provider, transport);
            transport.SetTargets(40, 41);
            var store = new ClientControlSessionStore(fixture.Root);
            await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None, requireExisting: true);
            var operation = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId;
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State, "A same-process normal writer must finish its original scope without silently replacing its callback.");
            Assert.AreEqual((byte)1, ports.Registers[0x1060]);
            Assert.AreEqual((byte)0, ports.Registers[0xC83C]);
            Assert.AreEqual(0, ports.UnmappedTargetReads);
        }
        foreach (bool started in new[] { false, true })
        {
            var ports = new BldingLifecycleTests.PortBus { MappingGatesTargets = true, ThrowOnUnmappedTargetRead = true };
            using var fixture = await FirmwareFixture.Create(0, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
            Dictionary<ushort, byte> baseline = started ? new() { [0x1060] = 0x81, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 } : [];
            var expected = new Dictionary<ushort, byte>(baseline);
            if (started) expected[0xC83D] = 41;
            var checkpoint = new FanEcRecoveryCheckpoint(baseline, expected, started ? new() { [0xC83D] = [0, 41] } : [], false)
                { MappingLease = new(1, 0x81, [], started, false) { TargetWritesStarted = started } };
            ports.Registers[0xC83C] = started ? (byte)0 : (byte)37;
            ports.Registers[0xC83D] = started ? (byte)41 : (byte)38;
            await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, checkpoint, CancellationToken.None);
            var store = new ClientControlSessionStore(fixture.Root);
            await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None, requireExisting: true);
            var operation = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId;
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State, result.Error?.ToString());
            Assert.AreEqual((byte)1, ports.Registers[0x1060]);
            Assert.AreEqual(started ? (byte)0 : (byte)37, ports.Registers[0xC83C]);
            Assert.AreEqual(started ? (byte)0 : (byte)38, ports.Registers[0xC83D]);
            Assert.AreEqual(0, ports.UnmappedTargetReads);
            Assert.IsNotNull(InvokeMappingProvider(fixture.Provider, "ReadFanEcMappingLocked"));
            Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint));
            int writes = ports.ValueWrites, reads = ports.ValueReads;
            result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State, "A finalized pending recovery must remain independently verifiable by End.");
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(writes, ports.ValueWrites);
            Assert.IsTrue(ports.ValueReads > reads, "Re-observe the actual mapping; do not refresh a cached timestamp.");
        }
    }
}
