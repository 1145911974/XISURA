using System.Text.Json;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class FanEcRecoveryJournalTests
{
    private static async Task<FanEcRecoveryCheckpoint> PendingLease(Fixture fixture)
    {
        await fixture.Seed();
        await fixture.Capture();
        // This fixture starts already mapped; unmapped historical target values are untrusted.
        var checkpoint = fixture.Checkpoint() with { Baseline = new(fixture.Checkpoint().Baseline) { [0x1060] = 0x81 }, Expected = new(fixture.Checkpoint().Expected)
            { [0x1060] = 0x81, [0x0B20] = 0x0A, [0xC83C] = 40, [0xC83D] = 41 } };
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, checkpoint, CancellationToken.None);
        var store = new ClientControlSessionStore(fixture.Root);
        await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing },
            CancellationToken.None, requireExisting: true);
        return checkpoint;
    }

    [TestMethod]
    public async Task Recovery_writer_preserves_original_scope_and_accepts_only_release_writes_after_observation()
    {
        using var fixture = new Fixture();
        var pending = await PendingLease(fixture);
        string capture = File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json"));
        var binding = await fixture.Journal.CreateFanEcRecoveryCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None);
        Assert.AreEqual(fixture.Scope, binding.Recovery.Scope);
        var ports = new BldingLifecycleTests.PortBus();
        foreach (var pair in pending.Expected) ports.Registers[pair.Key] = pair.Value;
        int nativeMarkers = 0;
        ports.IgnoreValueWrite = (address, value) =>
        {
            var durable = fixture.Journal.ReadAsync(CancellationToken.None).GetAwaiter().GetResult().FanEcRecovery!;
            Assert.AreEqual(fixture.Scope, durable.Scope);
            Assert.AreEqual(value, durable.Checkpoint.Expected[address]);
            Assert.IsTrue(durable.Checkpoint.Pending[address].Contains(value));
            nativeMarkers++;
            return false;
        };
        var transport = ports.Create(binding.Writer);
        transport.OpenForRecovery(binding.Recovery.Checkpoint);
        Assert.AreEqual(0, ports.ValueWrites);
        var forbidden = pending with { Expected = new(pending.Expected) { [0xC83C] = 42 }, Pending = new() { [0xC83C] = [40, 42] } };
        string before = File.ReadAllText(Path.Combine(fixture.Root, "control-execution.json"));
        Assert.ThrowsExactly<InvalidDataException>(() => binding.Writer(forbidden));
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(fixture.Root, "control-execution.json")));
        transport.Clear();
        transport.Dispose();
        Assert.IsTrue(nativeMarkers > 0);
        Assert.AreEqual(nativeMarkers, ports.ValueWrites);
        var restored = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!;
        Assert.AreEqual(fixture.Scope, restored.Scope);
        Assert.IsTrue(restored.Checkpoint.Released);
        Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(pending.Baseline), JsonSerializer.SerializeToElement(restored.Checkpoint.Baseline)));
        Assert.AreEqual(capture, File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
    }

    [TestMethod]
    public async Task Recovery_writer_refuses_changed_first_observation_or_missing_capture_without_replacing_original_journal()
    {
        using var fixture = new Fixture();
        var pending = await PendingLease(fixture);
        var binding = await fixture.Journal.CreateFanEcRecoveryCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None);
        string path = Path.Combine(fixture.Root, "control-execution.json"), original = File.ReadAllText(path);
        binding.Recovery.Checkpoint.Expected[0xC83C] = 43;
        var foreign = pending with { Expected = new(pending.Expected) { [0xC83C] = 43 } };
        Assert.ThrowsExactly<InvalidDataException>(() => binding.Writer(foreign));
        Assert.AreEqual(original, File.ReadAllText(path));
        File.Delete(Path.Combine(fixture.Root, "client-control-session.json"));
        StringAssert.Contains(Assert.ThrowsExactly<IOException>(() => binding.Writer(pending)).Message, "recovery backups remain");
        Assert.AreEqual(original, File.ReadAllText(path));
        StringAssert.Contains((await Assert.ThrowsAsync<IOException>(() => fixture.Journal.CreateFanEcRecoveryCheckpointWriterAsync(fixture.Scope.Identity, CancellationToken.None))).Message,
            "recovery backups remain");
        Assert.AreEqual(original, File.ReadAllText(path));
    }
}
