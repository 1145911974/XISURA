using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed partial class FanEcRecoveryJournalTests
{
    [TestMethod]
    public async Task Fan_checkpoint_survives_reopen_parent_progress_and_keeps_first_baseline()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, fixture.Checkpoint(), CancellationToken.None);
        var next = fixture.Checkpoint();
        next.Expected[0xC83C] = 40;
        next.Pending[0xC83C] = [0, 40];
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, next, CancellationToken.None);
        await fixture.Seed();
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.IsNotNull(saved.FanEcRecovery);
        Assert.AreEqual(fixture.Scope, saved.FanEcRecovery.Scope);
        Assert.AreEqual((byte)0, saved.FanEcRecovery.Checkpoint.Baseline[0xC83C]);
        Assert.AreEqual((byte)40, saved.FanEcRecovery.Checkpoint.Expected[0xC83C]);
        CollectionAssert.AreEqual(new byte[] { 0, 40 }, saved.FanEcRecovery.Checkpoint.Pending[0xC83C]);
        Assert.AreEqual(1, saved.Unresolved.Length);
        await fixture.Journal.ResolveNoMutationAsync(Guid.NewGuid(), saved.Current, CancellationToken.None, saved.RecoveryScope);
        Assert.AreEqual((byte)40, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None))
            .FanEcRecovery!.Checkpoint.Expected[0xC83C]);
        next.Expected[0xC83C] = 55;
        Assert.AreEqual((byte)40, (await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint.Expected[0xC83C]);
    }

    [TestMethod]
    public async Task Fan_checkpoint_rejects_foreign_authority_baseline_loss_and_invalid_release_without_replacing_disk()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, fixture.Checkpoint(), CancellationToken.None);
        string path = Path.Combine(fixture.Root, "control-execution.json");
        string before = await File.ReadAllTextAsync(path);
        foreach (var scope in new[] { fixture.Scope with { ServiceSessionId = Guid.NewGuid() }, fixture.Scope with { ClientSessionId = Guid.NewGuid() },
            fixture.Scope with { HardwareEpoch = 18 }, fixture.Scope with { OwnerSid = "other" },
            fixture.Scope with { Identity = new("other", "bios", "cpu", "gpu") } })
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(scope, fixture.Checkpoint(), CancellationToken.None));
        foreach (string defect in new[] { "baseline", "missing", "pending", "release" })
        {
            var checkpoint = fixture.Checkpoint();
            if (defect == "baseline") checkpoint.Baseline[0x1060] = 0x80;
            if (defect == "missing") checkpoint.Expected.Remove(0xC83C);
            if (defect == "pending") checkpoint.Pending[0x1234] = [1];
            if (defect == "release") checkpoint = checkpoint with { Released = true, Expected = new(checkpoint.Expected) { [0xC83C] = 40 } };
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, checkpoint, CancellationToken.None));
        }
        Assert.AreEqual(before, await File.ReadAllTextAsync(path));
        var corrupt = System.Text.Json.Nodes.JsonNode.Parse(before)!;
        corrupt["fanEcRecovery"]!["checkpoint"]!["expected"]!.AsObject().Remove("51260");
        string invalid = corrupt.ToJsonString();
        await File.WriteAllTextAsync(path, invalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None));
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, fixture.Checkpoint(), CancellationToken.None));
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task Unchanged_checkpoint_is_valid_only_with_original_owned_bits_and_no_pending_value_write()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var unchanged = fixture.Checkpoint() with { Unchanged = true };
        await fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, unchanged, CancellationToken.None);
        foreach (string defect in new[] { "target", "initialization", "pending", "both" })
        {
            var invalid = fixture.Checkpoint() with { Unchanged = true };
            if (defect == "target") invalid.Expected[0xC83C] = 40;
            if (defect == "initialization") invalid.Expected[0x1060] = 0x81;
            if (defect == "pending") invalid.Pending[0xC83C] = [0, 40];
            if (defect == "both") invalid = invalid with { Released = true };
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanEcCheckpointAsync(fixture.Scope, invalid, CancellationToken.None));
        }
        Assert.IsTrue((await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint.Unchanged);
    }

    [TestMethod]
    public async Task Production_fan_factory_binds_original_capture_and_callback_survives_closing_without_ambient_admission()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.Capture();
        using var provider = fixture.CreateProvider();
        var writer = Writer(provider);
        writer(fixture.Checkpoint());
        var target = fixture.Checkpoint();
        target.Expected[0xC83C] = 40;
        target.Pending[0xC83C] = [0, 40];
        writer(target);
        var store = new ClientControlSessionStore(fixture.Root);
        var capture = await store.ReadAsync(CancellationToken.None);
        await store.WriteAsync(capture! with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None, requireExisting: true);
        Assert.ThrowsExactly<InvalidDataException>(() => writer(target));
        writer(fixture.Checkpoint() with { Released = true });
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(fixture.Scope, saved.FanEcRecovery!.Scope);
        Assert.IsTrue(saved.FanEcRecovery.Checkpoint.Released);
        await store.WriteAsync(capture! with { Phase = ClientControlSessionPhase.Completed }, CancellationToken.None, requireExisting: true);
        Assert.ThrowsExactly<InvalidDataException>(() => writer(fixture.Checkpoint()));
    }

    [TestMethod]
    public async Task Production_fan_factory_rejects_missing_foreign_or_completed_capture_before_native_open()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        using var provider = fixture.CreateProvider();
        Assert.ThrowsExactly<InvalidDataException>(() => Writer(provider));
        var failure = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() =>
            typeof(WindowsHomeHardwareProvider).GetMethod("WriteStrongCoolingLocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(provider, [(byte)0, CancellationToken.None, (byte)1, false]));
        Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        await fixture.Capture();
        string path = Path.Combine(fixture.Root, "client-control-session.json");
        string original = await File.ReadAllTextAsync(path);
        foreach (string defect in new[] { "service", "client", "sid", "epoch", "completed", "identity" })
        {
            var value = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            if (defect == "service") value["serviceSessionId"] = Guid.NewGuid();
            if (defect == "client") value["handle"]!["sessionId"] = Guid.NewGuid();
            if (defect == "sid") value["ownerSid"] = "other";
            if (defect == "epoch") value["handle"]!["hardwareEpoch"] = 18;
            if (defect == "completed") value["phase"] = "Completed";
            if (defect == "identity") value["identity"]!["boardProduct"] = "other";
            await File.WriteAllTextAsync(path, value.ToJsonString());
            Assert.ThrowsExactly<InvalidDataException>(() => Writer(provider));
        }
        Assert.IsNull((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery);
    }

    [TestMethod]
    public async Task Production_fan_reacquisition_preserves_first_baseline_and_requires_verified_prior_terminal_state()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        await fixture.Capture();
        using var provider = fixture.CreateProvider();
        var writer = Writer(provider);
        var first = fixture.Checkpoint();
        first.Baseline[0xC83C] = 20;
        first.Expected[0xC83C] = 20;
        writer(first with { Unchanged = true });
        writer(first with { Expected = new(first.Expected) { [0xC83C] = 40 }, Pending = new() { [0xC83C] = [20, 40] } });
        Assert.ThrowsExactly<InvalidDataException>(() => Writer(provider));
        writer(first with { Released = true, Expected = new(first.Expected) { [0xC83C] = 0 } });
        var second = Writer(provider);
        second(fixture.Checkpoint() with { Unchanged = true });
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual((byte)20, saved.FanEcRecovery!.Checkpoint.Baseline[0xC83C]);
        Assert.IsTrue(saved.FanEcRecovery.Checkpoint.Released);
        Assert.IsFalse(saved.FanEcRecovery.Checkpoint.Unchanged);
        second(fixture.Checkpoint() with { Expected = new(fixture.Checkpoint().Expected) { [0xC83C] = 45 }, Pending = new() { [0xC83C] = [0, 45] } });
        Assert.AreEqual((byte)20, (await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Checkpoint.Baseline[0xC83C]);
        provider.SetBackgroundWorkPaused(true);
        provider.SetBackgroundWorkPaused(false);
        Assert.ThrowsExactly<IOException>(() => second(fixture.Checkpoint()));
    }

    private static Action<FanEcRecoveryCheckpoint> Writer(WindowsHomeHardwareProvider provider)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        BldingFanEcTransport transport;
        try { transport = (BldingFanEcTransport)typeof(WindowsHomeHardwareProvider).GetMethod("CreateFanTransport", flags)!.Invoke(provider, null)!; }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException)
        { throw (InvalidDataException)error.InnerException; }
        var callback = (Action<FanEcRecoveryCheckpoint>?)typeof(BldingFanEcTransport).GetProperty("Checkpoint", flags)!.GetValue(transport);
        Assert.IsNotNull(callback, "Production transport must carry its journal checkpoint callback before opening native EC access.");
        return callback;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "XisuraFanEcJournalTests", Guid.NewGuid().ToString("N"));
        public ControlExecutionJournal Journal { get; }
        public FanEcRecoveryScope Scope { get; } = new(Guid.NewGuid(), 17, Guid.NewGuid(), "user-a", new("board", "bios", "cpu", "gpu"));
        private readonly Guid operation = Guid.NewGuid();
        public Fixture() => Journal = new(Root);
        public Task Capture() => new ClientControlSessionStore(Root).WriteAsync(new(1, Scope.ServiceSessionId,
            new(Scope.ClientSessionId, Scope.HardwareEpoch, new string('A', 64), DateTimeOffset.UtcNow), Scope.OwnerSid, 1, 42,
            DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc(), Scope.Identity, new(null, null, null, null, false, null))
            { Phase = ClientControlSessionPhase.Active }, CancellationToken.None);
        public WindowsHomeHardwareProvider CreateProvider()
        {
            var provider = new WindowsHomeHardwareProvider(controlJournal: Journal);
            typeof(WindowsHomeHardwareProvider).GetField("identity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, Scope.Identity);
            return provider;
        }
        public Task Seed()
        {
            var state = new ControlExecutionState(1, 17, 0, ControlModeId.Office, PerformanceMode.Quiet, [],
                new() { ["fan.strategy"] = new(operation, ControlOrigin.ManualLive, SettingRequest.Set("Auto"), SettingRequest.Set("Fixed"), ApplyState.Unknown)
                    { ClientSessionId = Scope.ClientSessionId } });
            return Journal.SaveAsync(state, Scope.Identity, operation, true, CancellationToken.None, Scope.OwnerSid,
                recoveryScope: new(Scope.ServiceSessionId, new() { ["fan.strategy"] = new(Scope.OwnerSid, 17, null, 1) }, null, 0, Scope.ClientSessionId));
        }
        public FanEcRecoveryCheckpoint Checkpoint() => new(new() { [0x1060] = 1, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 },
            new() { [0x1060] = 1, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 }, new(), false);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
