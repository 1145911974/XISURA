using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class FanEcRecoveryJournalTests
{
    private static readonly JsonSerializerOptions MappingJson = new(JsonSerializerDefaults.Web);

    private static FanEcRecoveryCheckpoint MappingCheckpoint(byte expected = 0x81, bool captured = false,
        bool restored = false, bool unchanged = false, bool released = false, byte[]? pending = null, byte original = 1,
        bool? writesStarted = null)
    {
        Dictionary<ushort, byte> baseline = captured ? new() { [0x1060] = 0x81, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 } : [];
        var checkpoint = new FanEcRecoveryCheckpoint(baseline, new(baseline), [], released) { Unchanged = unchanged };
        if (captured) checkpoint.Expected[0x1060] = expected;
        var node = JsonSerializer.SerializeToNode(checkpoint, MappingJson)!;
        node["mappingLease"] = JsonSerializer.SerializeToNode(new
        {
            original, expected, pending = pending ?? [], targetsCaptured = captured, restored,
            targetWritesStarted = writesStarted ?? released
        }, MappingJson);
        return node.Deserialize<FanEcRecoveryCheckpoint>(MappingJson)!;
    }

    private static JsonObject MappingValues(FanEcRecoveryCheckpoint checkpoint) =>
        JsonSerializer.SerializeToNode(checkpoint, MappingJson)!["mappingLease"]!.AsObject();

    [TestMethod]
    public async Task Mapping_only_checkpoint_survives_reopen_without_inventing_target_baseline()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var checkpoint = MappingCheckpoint(pending: [1, 0x81]);
        Assert.IsTrue(ControlExecutionJournal.ValidFanCheckpoint(checkpoint));
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, checkpoint, CancellationToken.None);
        var saved = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!;
        Assert.AreEqual(fixture.Scope, saved.Scope);
        Assert.AreEqual(0, saved.Checkpoint.Baseline.Count);
        Assert.AreEqual(0, saved.Checkpoint.Expected.Count);
        Assert.AreEqual(0, saved.Checkpoint.Pending.Count);
        CollectionAssert.AreEqual(new byte[] { 1, 0x81 }, MappingValues(saved.Checkpoint)["pending"]!.Deserialize<byte[]>()!);
        Assert.IsFalse(ControlExecutionJournal.FanCheckpointComplete(saved.Checkpoint));
    }

    [TestMethod]
    public async Task Mapping_promotion_requires_confirmed_enabled_mapping_before_full_capture()
    {
        foreach (var before in new[] { MappingCheckpoint(expected: 1), MappingCheckpoint(pending: [1, 0x81]), MappingCheckpoint() })
        {
            using var fixture = new Fixture();
            await fixture.Seed();
            await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, before, CancellationToken.None);
            var promoted = MappingCheckpoint(captured: true);
            bool confirmed = MappingValues(before)["expected"]!.GetValue<byte>() == 0x81 &&
                MappingValues(before)["pending"]!.Deserialize<byte[]>()!.Length == 0;
            if (!confirmed)
                await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, promoted, CancellationToken.None));
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope,
                    MappingCheckpoint(captured: true, writesStarted: true), CancellationToken.None));
                await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, promoted, CancellationToken.None);
                var saved = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
                Assert.AreEqual(4, saved.Baseline.Count);
                Assert.IsTrue(MappingValues(saved)["targetsCaptured"]!.GetValue<bool>());
            }
        }
    }

    [TestMethod]
    public async Task Mapping_promotion_cannot_drop_metadata_change_original_or_replace_first_targets()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, MappingCheckpoint(), CancellationToken.None);
        var full = MappingCheckpoint(captured: true);
        full.Baseline[0xC83C] = full.Expected[0xC83C] = 23;
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, full, CancellationToken.None);
        string path = Path.Combine(fixture.Root, "control-execution.json"), before = File.ReadAllText(path);
        var noMetadata = new FanEcRecoveryCheckpoint(new(full.Baseline), new(full.Expected), [], false);
        var changedTarget = MappingCheckpoint(captured: true);
        foreach (var invalid in new[] { MappingCheckpoint(), MappingCheckpoint(captured: true, original: 3), noMetadata, changedTarget })
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, invalid, CancellationToken.None));
        Assert.AreEqual(before, File.ReadAllText(path));
        var sameValueWrite = full with { Pending = [], MappingLease = MappingCheckpoint(captured: true, writesStarted: true).MappingLease };
        sameValueWrite.Pending[0xC83C] = [23];
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, sameValueWrite, CancellationToken.None);
        var observed = sameValueWrite with { Pending = [] };
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, observed, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, full, CancellationToken.None));
        var reopened = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
        Assert.IsTrue(MappingValues(reopened)["targetWritesStarted"]!.GetValue<bool>(), "Same-value writes must survive reopen as writes, not untouched targets.");
    }

    [TestMethod]
    public void Mapping_validation_rejects_fabricated_targets_pending_and_terminal_states()
    {
        Assert.IsTrue(ControlExecutionJournal.ValidFanCheckpoint(MappingCheckpoint()));
        Assert.IsTrue(ControlExecutionJournal.ValidFanCheckpoint(MappingCheckpoint(expected: 1, restored: true, unchanged: true)));
        foreach (var invalid in new[]
        {
            MappingCheckpoint(original: 0x81), MappingCheckpoint(pending: [1, 1]),
            MappingCheckpoint(pending: [1]), MappingCheckpoint(pending: [0x81, 0x82]),
            MappingCheckpoint(restored: true), MappingCheckpoint(unchanged: true), MappingCheckpoint(released: true),
            MappingCheckpoint(expected: 1, restored: true, unchanged: true, pending: [1, 0x81]),
            MappingCheckpoint(captured: true, released: true), MappingCheckpoint(captured: true, restored: true, released: true),
            MappingCheckpoint(expected: 1, captured: true, restored: true, released: true, unchanged: true),
            MappingCheckpoint(writesStarted: true),
            MappingCheckpoint(expected: 1, captured: true, restored: true, unchanged: true, writesStarted: true),
            MappingCheckpoint(expected: 1, captured: true, restored: true, released: true, writesStarted: false)
        }) Assert.IsFalse(ControlExecutionJournal.ValidFanCheckpoint(invalid), JsonSerializer.Serialize(invalid, MappingJson));
        var fabricated = MappingCheckpoint();
        fabricated.Baseline[0xC83C] = fabricated.Expected[0xC83C] = 0;
        Assert.IsFalse(ControlExecutionJournal.ValidFanCheckpoint(fabricated));
        foreach (string required in new[] { "original", "expected", "pending", "targetsCaptured", "restored", "targetWritesStarted" })
        {
            var missing = JsonSerializer.SerializeToNode(MappingCheckpoint(), MappingJson)!;
            missing["mappingLease"]!.AsObject().Remove(required);
            Assert.ThrowsExactly<JsonException>(() => missing.Deserialize<FanEcRecoveryCheckpoint>(MappingJson));
        }
        var unownedWrite = MappingCheckpoint(captured: true);
        unownedWrite.Pending[0xC83C] = [0];
        Assert.IsFalse(ControlExecutionJournal.ValidFanCheckpoint(unownedWrite));
        var unownedChange = MappingCheckpoint(captured: true);
        unownedChange.Expected[0xC83C] = 23;
        Assert.IsFalse(ControlExecutionJournal.ValidFanCheckpoint(unownedChange));
        foreach (bool started in new[] { false, true })
        {
            var prematureMapping = MappingCheckpoint(expected: 1, captured: true, pending: [1, 0x81], writesStarted: started);
            prematureMapping.Expected[0xC83C] = 23;
            Assert.IsFalse(ControlExecutionJournal.ValidFanCheckpoint(prematureMapping), "Mapping cannot be restored before untouched targets are preserved or written targets released.");
        }
        var pendingRelease = MappingCheckpoint(expected: 1, captured: true, pending: [1, 0x81], writesStarted: true);
        pendingRelease.Pending[0xC83C] = [0, 23];
        Assert.IsFalse(ControlExecutionJournal.ValidFanCheckpoint(pendingRelease));
    }

    [TestMethod]
    public void Mapping_completion_requires_valid_stage_exact_original_bit_and_no_pending_writes()
    {
        Assert.IsFalse(ControlExecutionJournal.FanCheckpointComplete(MappingCheckpoint(captured: true, released: true)));
        Assert.IsFalse(ControlExecutionJournal.FanCheckpointComplete(MappingCheckpoint(expected: 1, captured: true,
            released: true, restored: true, pending: [1, 0x81])));
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(MappingCheckpoint(expected: 1, restored: true, unchanged: true)));
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(MappingCheckpoint(expected: 1, captured: true, restored: true, released: true)));
        var untouched = MappingCheckpoint(expected: 1, captured: true, restored: true, unchanged: true);
        untouched.Baseline[0xC83C] = untouched.Expected[0xC83C] = 23;
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(untouched), "Restoring only mapping must preserve untouched targets.");
    }

    [TestMethod]
    public async Task Mapping_closing_writer_restores_only_original_bit_and_keeps_authority_private()
    {
        foreach (bool acknowledgementLost in new[] { false, true })
        {
            using var fixture = new Fixture();
            await fixture.Seed();
            await fixture.Capture();
            await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope,
                MappingCheckpoint(pending: acknowledgementLost ? [1, 0x81] : []), CancellationToken.None);
            var store = new ClientControlSessionStore(fixture.Root);
            await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None);
            var binding = await fixture.Journal.CreateFanEcRecoveryCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None);
            if (acknowledgementLost)
            {
                var lease = typeof(FanEcRecoveryCheckpoint).GetProperty("MappingLease")!.GetValue(binding.Recovery.Checkpoint)!;
                var borrowedPending = (byte[])lease.GetType().GetProperty("Pending")!.GetValue(lease)!;
                borrowedPending[0] = 3;
            }
            else
            {
                Assert.ThrowsExactly<InvalidDataException>(() => binding.Writer(MappingCheckpoint(expected: 1, pending: [1, 0x81])));
                binding.Writer(MappingCheckpoint());
            }
            var restore = MappingCheckpoint(expected: 1, pending: [1, 0x81]);
            binding.Writer(restore);
            Assert.ThrowsExactly<InvalidDataException>(() => binding.Writer(MappingCheckpoint(pending: [1, 0x81])));
            Assert.ThrowsExactly<InvalidDataException>(() => binding.Writer(MappingCheckpoint(captured: true)));
            binding.Writer(MappingCheckpoint(expected: 1, restored: true, unchanged: true));
            var saved = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
            Assert.AreEqual((byte)1, MappingValues(saved)["original"]!.GetValue<byte>());
            Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(saved));
        }
        using var untouched = new Fixture();
        await untouched.Seed();
        await untouched.Capture();
        var active = await untouched.Journal.CreateFanEcCheckpointWriterAsync(untouched.Scope.Identity, CancellationToken.None);
        active(MappingCheckpoint(captured: true));
        var untouchedStore = new ClientControlSessionStore(untouched.Root);
        await untouchedStore.WriteAsync((await untouchedStore.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None);
        var recovery = await untouched.Journal.CreateFanEcRecoveryCheckpointWriterAsync(untouched.Scope.Identity, CancellationToken.None);
        var newWrite = MappingCheckpoint(captured: true, writesStarted: true);
        newWrite.Pending[0xC83C] = [0];
        Assert.ThrowsExactly<InvalidDataException>(() => active(newWrite));
        Assert.ThrowsExactly<InvalidDataException>(() => recovery.Writer(newWrite));

        using var written = new Fixture();
        await written.Seed();
        await written.Capture();
        var owned = MappingCheckpoint(captured: true, writesStarted: true);
        owned.Expected[0xC83C] = 23;
        await written.Journal.RecordFanEcCheckpointAsync(written.Scope, owned, CancellationToken.None);
        var writtenStore = new ClientControlSessionStore(written.Root);
        await writtenStore.WriteAsync((await writtenStore.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None);
        var release = await written.Journal.CreateFanEcRecoveryCheckpointWriterAsync(written.Scope.Identity, CancellationToken.None);
        release.Writer(owned);
        var releaseWal = MappingCheckpoint(captured: true, writesStarted: true);
        releaseWal.Pending[0xC83C] = [0, 23];
        release.Writer(releaseWal);
        release.Writer(MappingCheckpoint(captured: true, writesStarted: true));
        release.Writer(MappingCheckpoint(expected: 1, captured: true, pending: [1, 0x81], writesStarted: true));
        release.Writer(MappingCheckpoint(expected: 1, captured: true, restored: true, released: true));
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete((await written.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint));
    }

    [TestMethod]
    public async Task Mapping_active_writer_promotes_once_and_unresolved_lease_blocks_reacquisition()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.Capture();
        var writer = await fixture.Journal.CreateFanEcCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None);
        writer(MappingCheckpoint(expected: 1));
        writer(MappingCheckpoint(pending: [1, 0x81]));
        writer(MappingCheckpoint());
        writer(MappingCheckpoint(expected: 1, pending: [1, 0x81]));
        writer(MappingCheckpoint(expected: 1, restored: true, unchanged: true));
        writer = await fixture.Journal.CreateFanEcCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None);
        writer(MappingCheckpoint(expected: 1));
        writer(MappingCheckpoint(pending: [1, 0x81]));
        writer(MappingCheckpoint());
        writer(MappingCheckpoint(captured: true));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.CreateFanEcCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None));
        var saved = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
        Assert.AreEqual(4, saved.Baseline.Count);
        Assert.AreEqual((byte)1, MappingValues(saved)["original"]!.GetValue<byte>());
        Assert.IsFalse(ControlExecutionJournal.FanCheckpointComplete(saved));

        using var closing = new Fixture();
        await closing.Seed();
        await closing.Capture();
        var closingWriter = await closing.Journal.CreateFanEcCheckpointWriterAsync(closing.Scope.Identity, CancellationToken.None);
        closingWriter(MappingCheckpoint());
        var store = new ClientControlSessionStore(closing.Root);
        await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None);
        string path = Path.Combine(closing.Root, "control-execution.json"), before = File.ReadAllText(path);
        Assert.ThrowsExactly<InvalidDataException>(() => closingWriter(MappingCheckpoint(captured: true)));
        Assert.AreEqual(before, File.ReadAllText(path));
        closingWriter(MappingCheckpoint(expected: 1, pending: [1, 0x81]));
        closingWriter(MappingCheckpoint(expected: 1, restored: true, unchanged: true));
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete((await closing.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint));
    }

    [TestMethod]
    public async Task Mapping_metadata_cannot_launder_legacy_unmapped_snapshots_and_recovers_without_new_targets()
    {
        using var fixture = new Fixture();
        Assert.IsTrue(ControlExecutionJournal.ValidFanCheckpoint(fixture.Checkpoint()));
        var fake = MappingCheckpoint(captured: true);
        fake.Baseline[0x1060] = 1;
        Assert.IsFalse(ControlExecutionJournal.ValidFanCheckpoint(fake));
        foreach (var checkpoint in new[] { MappingCheckpoint(), MappingCheckpoint(captured: true) })
        {
            var bus = new BldingLifecycleTests.PortBus();
            var transport = bus.Create(_ => { });
            transport.OpenForRecovery(checkpoint);
            Assert.AreEqual(0, bus.ValueWrites);
            transport.FinishLease();
            Assert.AreEqual((byte)1, bus.Registers[0x1060]);
            transport.Dispose();
        }
        await fixture.Seed();
        await fixture.Capture();
        var firstWriter = await fixture.Journal.CreateFanEcCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None);
        var initial = fixture.Checkpoint();
        initial.Baseline[0xC83C] = initial.Expected[0xC83C] = 23;
        firstWriter(initial);
        firstWriter(initial with { Expected = new(initial.Expected) { [0xC83C] = 0 }, Released = true });
        var reacquired = await fixture.Journal.CreateFanEcCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None);
        reacquired(fixture.Checkpoint() with { Unchanged = true });
        var target = fixture.Checkpoint();
        target.Expected[0xC83C] = 40;
        target.Pending[0xC83C] = [0, 40];
        reacquired(target);
        var store = new ClientControlSessionStore(fixture.Root);
        await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None);
        reacquired(fixture.Checkpoint() with { Released = true });
        var saved = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint;
        Assert.AreEqual((byte)23, saved.Baseline[0xC83C]);
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(saved));
    }
}
