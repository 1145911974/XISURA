using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class MiProviderLifecycleTests
{
    [TestMethod]
    public async Task Fan_archived_ec_provider_requires_exact_completed_archive_before_firmware_only_restore()
    {
        foreach (string fault in new[] { "none", "missing", "mismatch", "pending" })
        {
            FanEcRecoverySnapshot? prior = null;
            using var fixture = await FirmwareFixture.Create(1,
                (_, _, _) => throw new AssertFailedException("Historical EC must not be reopened."),
                async seeded => { prior = await FanArchivedRecoveryFixture.SeedAsync(seeded.Root, seeded.Scope, fault); });
            var operation = await SeedFirmwareOnlyRecovery(fixture);
            int writes = fixture.Bus.Writes;
            string journal = File.ReadAllText(fixture.Path);
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(fault == "none" ? CommandState.Applied : CommandState.RecoveryRequired, result.State, fault);
            Assert.AreEqual(writes + (fault == "none" ? 1 : 0), fixture.Bus.Writes, fault);
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(JsonSerializer.Serialize(prior), JsonSerializer.Serialize(saved.FanEcRecovery));
            if (fault != "none")
            {
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
                Assert.AreEqual(journal, File.ReadAllText(fixture.Path));
            }
        }
    }

    [TestMethod]
    public async Task Fan_unchanged_provider_preserves_external_targets_without_ec_or_firmware_value_writes()
    {
        foreach (byte cpu in new byte[] { 0, 20 })
        {
            var ports = new BldingLifecycleTests.PortBus();
            using var fixture = await FirmwareFixture.Create(0, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
            var operation = await SeedFanRecovery(fixture, ports, originalTarget: cpu, unchanged: true);
            var registers = new Dictionary<ushort, byte> { [0x1060] = 0x81, [0x0B20] = cpu == 0 ? (byte)0 : (byte)0x0A,
                [0xC83C] = cpu, [0xC83D] = cpu };
            foreach (var pair in registers) ports.Registers[pair.Key] = pair.Value;
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(0, ports.ValueWrites);
            Assert.AreEqual(0, fixture.Bus.Writes);
            foreach (var pair in registers) Assert.AreEqual(pair.Value, ports.Registers[pair.Key]);
            Assert.IsTrue((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint.Unchanged);
        }
    }

    private static async Task<Guid> SeedFirmwareOnlyRecovery(FirmwareFixture fixture)
    {
        fixture.Prepare();
        typeof(WindowsHomeHardwareProvider).GetField("fanPreviousStrongCooling", Private)!.SetValue(fixture.Provider, null);
        var store = new ClientControlSessionStore(fixture.Root);
        await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing },
            CancellationToken.None, requireExisting: true);
        return (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId;
    }

    [TestMethod]
    public async Task Fan_firmware_only_provider_restores_or_reconciles_without_opening_ec()
    {
        foreach (bool lostAck in new[] { false, true })
        {
            using var fixture = await FirmwareFixture.Create(1, (_, _, _) => throw new AssertFailedException("Firmware-only recovery must not open EC."));
            var operation = await SeedFirmwareOnlyRecovery(fixture);
            int writes = fixture.Bus.Writes;
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            fixture.Bus.FailReadAfterWrite = lostAck;
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            if (lostAck)
            {
                Assert.AreEqual(CommandState.RecoveryRequired, result.State);
                Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
                fixture.Bus.FailReadAfterWrite = fixture.Bus.ReadUnavailable = false;
                result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            }
            Assert.AreEqual(CommandState.Applied, result.State, result.Error?.ToString());
            Assert.AreEqual((byte)1, fixture.Bus.Value);
            Assert.AreEqual(writes + 1, fixture.Bus.Writes);
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.IsNull(saved.FanEcRecovery);
            Assert.AreEqual(fixture.Scope, saved.FanFirmwareRecovery!.Scope);
            Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete(saved.FanFirmwareRecovery));
        }
    }

    [TestMethod]
    public async Task Fan_firmware_only_provider_rejects_missing_evidence_and_external_flags_without_mutation()
    {
        foreach (bool external in new[] { false, true })
        {
            using var fixture = await FirmwareFixture.Create(external ? (byte)1 : (byte)0,
                (_, _, _) => throw new AssertFailedException("Rejected recovery must not open EC."));
            var operation = await SeedFirmwareOnlyRecovery(fixture);
            if (external) fixture.Bus.Value = 2;
            int writes = fixture.Bus.Writes;
            string journal = File.ReadAllText(fixture.Path);
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(writes, fixture.Bus.Writes);
            Assert.AreEqual(journal, File.ReadAllText(fixture.Path));
        }
    }

    private static async Task<Guid> SeedFanRecovery(FirmwareFixture fixture, BldingLifecycleTests.PortBus ports, byte originalTarget = 0,
        bool unchanged = false)
    {
        fixture.Prepare();
        typeof(WindowsHomeHardwareProvider).GetField("fanPreviousStrongCooling", Private)!.SetValue(fixture.Provider, null);
        // Targets are captured only after the durable mapping lease has enabled DLM RAM.
        var baseline = new Dictionary<ushort, byte> { [0x1060] = 0x81, [0x0B20] = originalTarget == 0 ? (byte)0 : (byte)0x0A,
            [0xC83C] = originalTarget, [0xC83D] = originalTarget };
        var expected = unchanged ? new(baseline) : new Dictionary<ushort, byte>
            { [0x1060] = 0x81, [0x0B20] = 0x0A, [0xC83C] = 40, [0xC83D] = 41 };
        foreach (var pair in expected) ports.Registers[pair.Key] = pair.Value;
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope,
            new(baseline, expected, new(), false)
            { Unchanged = unchanged, MappingLease = unchanged ? null :
                new(1, 0x81, [], true, false) { TargetWritesStarted = true } }, CancellationToken.None);
        var store = new ClientControlSessionStore(fixture.Root);
        await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing },
            CancellationToken.None, requireExisting: true);
        return (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId;
    }

    [TestMethod]
    public async Task Fan_provider_releases_persisted_ec_before_restoring_original_firmware_flag()
    {
        foreach (byte original in new byte[] { 0, 1 })
        {
            var ports = new BldingLifecycleTests.PortBus();
            using var fixture = await FirmwareFixture.Create(original, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
            var operation = await SeedFanRecovery(fixture, ports);
            int firmwareWrites = fixture.Bus.Writes;
            fixture.Bus.BeforeWrite = () =>
            {
                var saved = fixture.Journal.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(saved.FanEcRecovery!.Checkpoint.Released);
                Assert.AreEqual(fixture.Scope, saved.FanFirmwareRecovery!.Scope);
                Assert.IsTrue(saved.FanFirmwareRecovery.Restoring);
                Assert.IsNull(saved.FanFirmwareRecovery.Observed);
                Assert.AreEqual((byte)0, ports.Registers[0xC83C]);
                Assert.AreEqual((byte)0, ports.Registers[0xC83D]);
            };
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State, result.Error?.ToString());
            Assert.AreEqual(HardwareMutation.Changed, result.Mutation);
            Assert.AreEqual(original, fixture.Bus.Value);
            Assert.AreEqual(firmwareWrites + original, fixture.Bus.Writes);
            Assert.AreEqual((byte)1, ports.Registers[0x1060]);
            Assert.AreEqual((byte)0, ports.Registers[0x0B20]);
            Assert.AreEqual(0, ports.Shutdowns, "Keep the completed lease available for independent mapping confirmation.");
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(fixture.Scope, saved.FanEcRecovery!.Scope);
            Assert.IsTrue(saved.FanEcRecovery.Checkpoint.Released);
            if (original == 1) Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete(saved.FanFirmwareRecovery!));
        }
    }

    [TestMethod]
    public async Task Fan_provider_keeps_unreleased_ec_and_original_firmware_until_the_same_parent_can_retry()
    {
        foreach (bool cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var ports = new BldingLifecycleTests.PortBus();
            using var fixture = await FirmwareFixture.Create(1, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
            var operation = await SeedFanRecovery(fixture, ports);
            if (cancel) ports.AfterTransfer = _ => { if (ports.ValueWrites > 0) cancellation.Cancel(); };
            else ports.IgnoreValueWrite = (address, value) => address == 0xC83D && value == 0;
            int firmwareWrites = fixture.Bus.Writes;
            using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var failed = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, cancellation.Token);
            Assert.AreEqual(CommandState.RecoveryRequired, failed.State);
            Assert.AreEqual(HardwareMutation.Unknown, failed.Mutation);
            Assert.AreEqual((byte)0, fixture.Bus.Value);
            Assert.AreEqual(firmwareWrites, fixture.Bus.Writes);
            Assert.AreEqual(0, ports.Shutdowns);
            Assert.IsFalse((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint.Released);
            ports.IgnoreValueWrite = null;
            ports.AfterTransfer = null;
            var retried = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, retried.State);
            Assert.AreEqual((byte)1, fixture.Bus.Value);
            Assert.AreEqual(operation, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        }
    }

    [TestMethod]
    public async Task Fan_provider_requires_original_admission_capture_and_owned_registers_before_any_value_write()
    {
        foreach (string failure in new[] { "admission", "capture", "external" })
        {
            var ports = new BldingLifecycleTests.PortBus();
            using var fixture = await FirmwareFixture.Create(1, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
            var operation = await SeedFanRecovery(fixture, ports);
            if (failure == "capture") File.Delete(System.IO.Path.Combine(fixture.Root, "client-control-session.json"));
            if (failure == "external") ports.Registers[0xC83C] = 43;
            int firmwareWrites = fixture.Bus.Writes;
            string journal = File.ReadAllText(fixture.Path);
            using var admission = failure == "admission" ? null : fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
            var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
            Assert.AreNotEqual(CommandState.Applied, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(0, ports.ValueWrites);
            Assert.AreEqual(firmwareWrites, fixture.Bus.Writes);
            Assert.AreEqual(journal, File.ReadAllText(fixture.Path));
        }
    }

    [TestMethod]
    public async Task Fan_provider_reconciles_lost_firmware_ack_without_repeating_verified_ec_or_firmware_writes()
    {
        var ports = new BldingLifecycleTests.PortBus();
        using var fixture = await FirmwareFixture.Create(1, (allowed, readAllowed, writer) => ports.Create(writer, allowed, readAllowed));
        var operation = await SeedFanRecovery(fixture, ports);
        using var admission = fixture.Journal.BindFanRecovery(new(operation, fixture.Scope));
        fixture.Bus.FailReadAfterWrite = true;
        var failed = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, failed.State);
        Assert.AreEqual(HardwareMutation.Unknown, failed.Mutation);
        Assert.AreEqual((byte)1, fixture.Bus.Value);
        int ecWrites = ports.ValueWrites, miWrites = fixture.Bus.Writes;
        fixture.Bus.FailReadAfterWrite = fixture.Bus.ReadUnavailable = false;
        var result = await fixture.Provider.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.AreEqual(ecWrites, ports.ValueWrites);
        Assert.AreEqual(miWrites, fixture.Bus.Writes);
        Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete((await fixture.Journal.ReadAsync(CancellationToken.None)).FanFirmwareRecovery!));
    }
}
