using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class GpuRecoveryJournalTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly HardwareIdentity Identity = new("fake-board", "fake-bios", "fake-cpu", "fake-gpu");
    private static readonly GpuAdapterIdentity Adapter = new(0x28E010DE, 1, 0xA1, 0x28E0, 1, 0);
    private static ControlExecutionState Pending => new(1, 17, 1, ControlModeId.Office, PerformanceMode.Quiet, [], [])
    { NativeModeApplyState = ApplyState.Unknown };

    [TestMethod]
    public async Task Baseline_survives_restart_and_pending_updates_without_losing_first_parent_snapshot()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var baseline = fixture.Baseline();
        await fixture.Journal.RecordGpuRecoveryAsync(fixture.Id, baseline, CancellationToken.None);
        baseline.VfOffsetsKhz[1] = 999;
        await fixture.Journal.SaveAsync(Pending, Identity, fixture.Id, true, CancellationToken.None, "user-a", "child-gpu-write");
        var restarted = new ControlExecutionJournal(fixture.Root);
        var record = (await restarted.ReadAsync(CancellationToken.None)).Unresolved.Single().GpuRecovery;
        Assert.IsNotNull(record);
        Assert.AreEqual(10_000, record.VfOffsetsKhz[1]);
        Assert.AreEqual(fixture.Session, record.SessionId);
        Assert.AreEqual(Adapter, record.Adapter);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ControlExecutionJournal.GpuOperation?> captured;
        using (fixture.Bind()) captured = Task.Run(async () => { await release.Task; return fixture.Journal.CurrentGpuOperation; });
        release.SetResult();
        Assert.IsNull(await captured, "Disposal revokes a previously captured execution context.");
        await restarted.RecordGpuRecoveryAsync(fixture.Id, fixture.Baseline() with
        { VfOffsetsKhz = Enumerable.Repeat(20_000, 127).ToArray() }, CancellationToken.None);
        Assert.AreEqual(10_000, (await restarted.ReadAsync(CancellationToken.None)).Unresolved.Single().GpuRecovery!.VfOffsetsKhz[1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.RecordGpuRecoveryAsync(fixture.Id,
            fixture.Baseline() with { Adapter = Adapter with { BusId = 2 } }, CancellationToken.None));
        await restarted.SaveAsync(Pending with { NativeModeApplyState = ApplyState.Confirmed }, Identity, fixture.Id,
            false, CancellationToken.None, "user-a");
        Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
    }

    [TestMethod]
    public async Task Corrupt_recovery_records_are_rejected_and_original_file_is_retained()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var baseline = fixture.Baseline();
        foreach (var invalid in new[] { baseline with { SessionId = Guid.Empty }, baseline with { HardwareEpoch = 18 },
            baseline with { OwnerSid = "user-b" }, baseline with { VfOffsetsKhz = [0] },
            baseline with { Adapter = Adapter with { DeviceId = 0 } },
            baseline with { Pstate = baseline.Pstate with { StateCount = 17 } } })
        {
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var corrupted = saved with { Unresolved = [saved.Unresolved.Single() with { GpuRecovery = invalid }] };
            string text = JsonSerializer.Serialize(corrupted, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(fixture.File, text);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None));
            Assert.AreEqual(text, await File.ReadAllTextAsync(fixture.File));
        }
        await fixture.Seed();
        string original = await File.ReadAllTextAsync(fixture.File);
        var oversized = Pending with { Fields = new() { ["gpu:vfOffsetsKhz"] = new(fixture.Id, ControlOrigin.ManualLive,
            SettingRequest.Set(new string('x', 2 * 1024 * 1024)), null, ApplyState.Unknown) } };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Journal.SaveAsync(oversized, Identity,
            fixture.Id, true, CancellationToken.None, "user-a"));
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.File), "A write must never produce a journal that restart would reject for size.");
    }

    [TestMethod]
    public void Adapter_identity_reads_obey_entry_and_post_read_pause_without_normalizing_raw_ids()
    {
        bool readAllowed = false;
        int reads = 0;
        var api = new NvapiVfInterop(_ => (_, _) => 0, () => [(nint)1], () => false, () => readAllowed,
            adapterIdentityReader: _ => { reads++; return Adapter; });
        Assert.ThrowsExactly<IOException>(() => api.ReadAdapterIdentity((nint)1));
        Assert.AreEqual(0, reads);
        readAllowed = true;
        Assert.AreEqual(Adapter, api.ReadAdapterIdentity((nint)1));
        var paused = new NvapiVfInterop(_ => (_, _) => 0, () => [(nint)1], () => false, () => readAllowed,
            adapterIdentityReader: _ => { readAllowed = false; return Adapter; });
        Assert.ThrowsExactly<IOException>(() => paused.ReadAdapterIdentity((nint)1));
        Assert.IsFalse(api.WriteAttempted);
        Assert.IsFalse(paused.WriteAttempted);
    }

    [TestMethod]
    public async Task Every_gpu_transaction_persists_before_dispatch_and_restart_blocks_new_writes()
    {
        foreach (string kind in new[] { "vf", "core", "memory" })
        {
            using var fixture = new Fixture();
            await fixture.Seed();
            var bus = new GpuVfLifecycleTests.FakeGpu { PauseStage = "partial" };
            using var provider = Provider(bus, fixture.Journal);
            bus.OnPause = () => provider.SetBackgroundWorkPaused(true);
            var original = bus.Offsets.ToArray();
            bus.BeforeWrite = () =>
            {
                var stored = new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .Unresolved.Single().GpuRecovery;
                Assert.IsNotNull(stored, "The durable snapshot must exist before the native write begins.");
                CollectionAssert.AreEqual(original, stored.VfOffsetsKhz);
                Assert.AreEqual(0, stored.Pstate.CoreOffsetKhz);
                Assert.AreEqual(2, stored.Pstate.ClockCount);
            };
            using (fixture.Bind())
            {
                var result = Execute(provider, Command(kind, fixture.Id, original));
                Assert.AreEqual(HardwareMutation.Unknown, result.Mutation, kind);
            }
            var restarted = new ControlExecutionJournal(fixture.Root);
            Assert.IsNotNull((await restarted.ReadAsync(CancellationToken.None)).Unresolved.Single().GpuRecovery);
            var nextId = Guid.NewGuid();
            await restarted.SaveAsync(Pending, Identity, nextId, true, CancellationToken.None, "user-a");
            var nextBus = new GpuVfLifecycleTests.FakeGpu();
            using var nextProvider = Provider(nextBus, restarted);
            using (restarted.BindGpuOperation(new(nextId, Guid.NewGuid(), 17, "user-a")))
                Assert.AreEqual(HardwareMutation.NoMutation, Execute(nextProvider, Command(kind, nextId, nextBus.Offsets)).Mutation);
            Assert.AreEqual(0, nextBus.Writes, "A new operation must not overwrite unresolved GPU state from the prior process.");
        }
    }

    [TestMethod]
    public async Task Storage_owner_epoch_and_missing_admission_fail_before_any_native_write()
    {
        foreach (string failure in new[] { "storage", "owner", "epoch", "missingScope", "missingPending", "adapter" })
        {
            using var fixture = new Fixture();
            if (failure != "missingPending") await fixture.Seed();
            if (failure == "storage") { File.Move(fixture.File, fixture.File + ".captured"); Directory.CreateDirectory(fixture.File); }
            var bus = new GpuVfLifecycleTests.FakeGpu();
            if (failure == "adapter") bus.IdentityReader = _ => Adapter with { DeviceId = 0 };
            using var provider = Provider(bus, fixture.Journal);
            using var binding = failure == "missingScope" ? null : fixture.Journal.BindGpuOperation(new(fixture.Id,
                fixture.Session, failure == "epoch" ? 18 : 17, failure == "owner" ? "user-b" : "user-a"));
            var result = Execute(provider, Command("memory", fixture.Id, bus.Offsets));
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, failure);
            Assert.AreEqual("False", result.Error?.Details["writeAttempted"], failure);
            Assert.AreEqual(0, bus.Writes, failure);
            if (failure == "storage") Assert.AreEqual("gpuRecoveryPersistenceFailed", result.Error?.Details["reason"]);
        }
    }

    [TestMethod]
    public async Task Runtime_binds_current_session_epoch_and_owner_then_releases_scope_after_zero_write()
    {
        using var fixture = new Fixture();
        var provider = new ScopedProvider(fixture.Journal);
        var runtime = new HomeServiceRuntime(provider, journal: fixture.Journal);
        await runtime.InitializeAsync(CancellationToken.None);
        var command = new SetGpuMemoryOffsetCommand(fixture.Id, 0, 100_000, true);
        var result = await runtime.ExecuteAsync(command, CancellationToken.None, userSid: "user-a");
        Assert.IsNotNull(provider.Captured);
        Assert.AreEqual(command.OperationId, provider.Captured.OperationId);
        Assert.AreEqual(runtime.SessionId, provider.Captured.SessionId);
        Assert.AreEqual(runtime.Execution.HardwareEpoch, provider.Captured.HardwareEpoch);
        Assert.AreEqual("user-a", provider.Captured.OwnerSid);
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.IsNull(fixture.Journal.CurrentGpuOperation);
        await fixture.Seed();
        int reads = provider.Reads;
        foreach (HardwareCommand blockedCommand in new HardwareCommand[]
        {
            new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 100_000, true),
            new ApplyControlRequestCommand(Guid.NewGuid(), new(Guid.NewGuid(), runtime.Execution.Generation, ControlOrigin.PresetUse,
                ControlModeId.Office, PerformanceMode.Quiet, [new(ControlPageId.Gpu, true, new(ControlModeId.Office, 1), 1)])),
            new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet)
        })
        {
            var blocked = await runtime.ExecuteAsync(blockedCommand, CancellationToken.None, userSid: "user-a");
            Assert.AreEqual(CommandState.RecoveryRequired, blocked.State);
            Assert.AreEqual(HardwareMutation.NoMutation, blocked.Mutation);
        }
        Assert.AreEqual(reads, provider.Reads, "Known unresolved GPU state rejects before control baselines or full diagnosis.");
        await fixture.Journal.SaveAsync(Pending with
        {
            Fields = new() { ["Gpu.memoryOffsetKhz"] = new(fixture.Id, ControlOrigin.PresetUse, null, null, ApplyState.Unknown) }
        }, Identity, fixture.Id, true, CancellationToken.None, "user-a", nameof(ApplyControlRequestCommand));
        var legacy = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single();
        Assert.IsNull(legacy.GpuRecovery);
        Assert.AreEqual(0, legacy.State.Pages.Length);
        Assert.IsTrue(ControlExecutionJournal.HasPendingGpuRecovery(legacy), "Unknown GPU fields must block without a GPU page marker.");
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Journal.CheckGpuAdmissionAsync(
            new(Guid.NewGuid(), fixture.Session, 17, "user-a"), CancellationToken.None));
        var legacyBlocked = await runtime.ExecuteAsync(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 100_000, true),
            CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.RecoveryRequired, legacyBlocked.State);
        Assert.AreEqual(HardwareMutation.NoMutation, legacyBlocked.Mutation);
        Assert.AreEqual(reads, provider.Reads);
    }

    private static WindowsHomeHardwareProvider Provider(GpuVfLifecycleTests.FakeGpu bus, ControlExecutionJournal journal)
    {
        var provider = new WindowsHomeHardwareProvider(bus.Create, () => true, _ => 45, journal);
        typeof(WindowsHomeHardwareProvider).GetField("identity", Private)!.SetValue(provider, Identity);
        typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
            new CompatibilityDecision(CompatibilityMode.Writable,
                new CapabilitySnapshot([new CapabilityDescriptor("gpuVfCurve", CapabilityState.Available, null)]), null, []));
        return provider;
    }
    private static HardwareCommand Command(string kind, Guid id, int[] original) => kind switch
    {
        "core" => new SetGpuCoreOffsetCommand(id, 0, 10_000, true) { ExpectedVfOffsetsKhz = original },
        "memory" => new SetGpuMemoryOffsetCommand(id, 0, 100_000, true),
        _ => new SetGpuVfCurveCommand(id, original, original.Select((v, i) => i == 0 ? 0 : v + 10_000).ToArray(), true)
    };
    private static CommandResult Execute(WindowsHomeHardwareProvider provider, HardwareCommand command) =>
        (CommandResult)typeof(WindowsHomeHardwareProvider).GetMethod(command is SetGpuVfCurveCommand ? "ExecuteGpuVfLocked" :
            command is SetGpuCoreOffsetCommand ? "ExecuteGpuCoreOffsetLocked" : "ExecuteGpuMemoryOffsetLocked", Private)!
            .Invoke(provider, [command, CancellationToken.None])!;

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "xisura-gpu-journal-" + Guid.NewGuid().ToString("N"));
        public readonly Guid Id = Guid.NewGuid(), Session = Guid.NewGuid();
        public readonly ControlExecutionJournal Journal;
        public string File => Path.Combine(Root, "control-execution.json");
        public Fixture() => Journal = new(Root);
        public Task Seed() => Journal.SaveAsync(Pending, Identity, Id, true, CancellationToken.None, "user-a", nameof(SetGpuMemoryOffsetCommand));
        public IDisposable Bind() => Journal.BindGpuOperation(new(Id, Session, 17, "user-a"));
        public GpuRecoveryBaseline Baseline() => new(DateTimeOffset.UtcNow, Session, 17, "user-a", Identity, Adapter,
            Enumerable.Range(0, 127).Select(i => i == 0 ? 0 : 10_000).ToArray(), new(0, -2_000_000, 2_000_000, 0, 0, 1, 2));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class ScopedProvider(ControlExecutionJournal journal) : IHomeHardwareProvider
    {
        public ControlExecutionJournal.GpuOperation? Captured;
        public int Reads;
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token)
        {
            Reads++;
            return Task.FromResult(new HomeHardwareState(
            new([new("gpuMemoryOffset", CapabilityState.Available, null)]), DeviceSupportState.Ready, null, Identity, null)
        { Controls = new(PerformanceMode.Quiet, false, []) { GpuVf = new([], -200_000, 200_000, null) { MemoryOffsetKhz = 0 } } });
        }
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => throw new InvalidOperationException("unused");
        public async Task<HomeControlState> ReadControlsAsync(CancellationToken token) => (await DiagnoseAsync(token)).Controls;
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            Captured = journal.CurrentGpuOperation;
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.ValidationFailed, command.OperationId, false), false) { Mutation = HardwareMutation.NoMutation });
        }
    }
}
