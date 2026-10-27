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
        Assert.IsNotNull(provider.Captured, $"GPU provider was not dispatched: {result.State}, {result.Error?.Code}");
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

    [TestMethod]
    public async Task Core_curve_memory_exit_sequence_keeps_first_native_baseline_across_provider_retry()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var bus = new GpuVfLifecycleTests.FakeGpu { CoreOffset = 10000, MemoryOffset = 1000,
            Offsets = Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 20000).ToArray() };
        int[] original = bus.Offsets.ToArray();
        using (var provider = Provider(bus, fixture.Journal))
        using (fixture.Bind())
        {
            Assert.AreEqual(CommandState.Applied, Execute(provider, new SetGpuCoreOffsetCommand(Guid.NewGuid(), 10000, 0, true)
                { ExpectedVfOffsetsKhz = original }).State);
            Assert.AreEqual(CommandState.Applied, Execute(provider, new SetGpuVfCurveCommand(Guid.NewGuid(), bus.Offsets.ToArray(), new int[127], true)).State);
        }
        using (var retry = Provider(bus, fixture.Journal))
        using (fixture.Bind())
            Assert.AreEqual(CommandState.Applied, Execute(retry, new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 1000, 0, true)).State);
        var saved = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().GpuRecovery!;
        CollectionAssert.AreEqual(original, saved.VfOffsetsKhz);
        Assert.AreEqual(10000, saved.Pstate.CoreOffsetKhz);
        Assert.AreEqual(1000, saved.Pstate.MemoryOffsetKhz);
        Assert.AreEqual(fixture.Session, saved.SessionId);
        Assert.AreEqual(0, bus.CoreOffset);
        Assert.AreEqual(0, bus.MemoryOffset);
        CollectionAssert.AreEqual(new int[127], bus.Offsets);
        await fixture.Journal.SaveAsync(Pending with { NativeModeApplyState = ApplyState.Confirmed }, Identity, fixture.Id, false,
            CancellationToken.None, "user-a", "EndClientControlSession");
        Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
    }

    [TestMethod]
    public async Task Native_recovery_reconciliation_requires_original_scope_adapter_and_exact_latest_snapshot()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var bus = new GpuVfLifecycleTests.FakeGpu { FailFirstWrite = true, FailRefreshAfterRollback = true };
        using var provider = Provider(bus, fixture.Journal);
        using var scope = fixture.Bind();
        Assert.AreEqual(CommandState.RecoveryRequired, Execute(provider, Command("core", Guid.NewGuid(), bus.Offsets.ToArray())).State);
        bus.FailFirstWrite = bus.FailRefreshAfterRollback = false;
        int writes = bus.Writes;
        var hardware = (IHomeHardwareProvider)provider;
        foreach (string change in new[] { "operation", "owner", "epoch", "session", "adapter", "adapterLate", "core", "memory", "curve", "header", "pause" })
        {
            using var wrongScope = change is "operation" or "owner" or "epoch" or "session" ? fixture.Journal.BindGpuOperation(new(
                change == "operation" ? Guid.NewGuid() : fixture.Id, change == "session" ? Guid.NewGuid() : fixture.Session,
                change == "epoch" ? 18 : 17, change == "owner" ? "other" : "user-a")) : null;
            if (change == "adapter") bus.IdentityReader = _ => Adapter with { BusId = 2 };
            int identityReads = 0;
            if (change == "adapterLate") bus.IdentityReader = _ => identityReads++ == 0 ? Adapter : Adapter with { BusId = 2 };
            if (change == "core") bus.CoreOffset = 1;
            if (change == "memory") bus.MemoryOffset = 1;
            if (change == "curve") bus.Offsets[7]++;
            if (change == "header") bus.StateCount = 2;
            if (change == "pause") provider.SetBackgroundWorkPaused(true);
            int reads = bus.Reads;
            Assert.IsFalse(await hardware.ReconcileGpuRecoveryAsync(CancellationToken.None), change);
            if (change is "operation" or "owner" or "epoch" or "session" or "pause") Assert.AreEqual(reads, bus.Reads);
            Assert.IsNotNull(typeof(WindowsHomeHardwareProvider).GetField("gpuPstateRecoverySnapshot", Private)!.GetValue(provider));
            provider.SetBackgroundWorkPaused(false);
            bus.IdentityReader = _ => Adapter;
            bus.CoreOffset = bus.MemoryOffset = 0;
            if (change == "curve") bus.Offsets[7]--;
            bus.StateCount = 1;
        }
        Assert.IsTrue(await hardware.ReconcileGpuRecoveryAsync(CancellationToken.None));
        Assert.AreEqual(writes, bus.Writes);
        Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("gpuPstateRecoverySnapshot", Private)!.GetValue(provider));
        Assert.IsFalse((bool)typeof(WindowsHomeHardwareProvider).GetField("gpuCoreRecoveryRequired", Private)!.GetValue(provider)!);
        Assert.AreEqual(1, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
    }

    [TestMethod]
    public async Task Native_reconciliation_uses_latest_step_without_restoring_first_parent_values()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var bus = new GpuVfLifecycleTests.FakeGpu { MemoryOffset = 1000 };
        using var provider = Provider(bus, fixture.Journal);
        using var scope = fixture.Bind();
        Assert.AreEqual(CommandState.Applied, Execute(provider, new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 1000, 0, true)).State);
        bus.Writes = 0;
        bus.FailFirstWrite = bus.FailRefreshAfterRollback = true;
        Assert.AreEqual(CommandState.RecoveryRequired, Execute(provider, Command("core", Guid.NewGuid(), bus.Offsets.ToArray())).State);
        bus.FailFirstWrite = bus.FailRefreshAfterRollback = false;
        int writes = bus.Writes;
        Assert.IsTrue(await ((IHomeHardwareProvider)provider).ReconcileGpuRecoveryAsync(CancellationToken.None));
        Assert.IsFalse((bool)typeof(WindowsHomeHardwareProvider).GetField("gpuCoreRecoveryRequired", Private)!.GetValue(provider)!);
        Assert.AreEqual(writes, bus.Writes);
        Assert.AreEqual(0, bus.MemoryOffset);
        Assert.AreEqual(1000, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().GpuRecovery!.Pstate.MemoryOffsetKhz);
    }

    [TestMethod]
    public async Task Every_native_gpu_step_persists_latest_before_and_target_before_any_write()
    {
        foreach (string kind in new[] { "core", "memory", "vf" })
        {
            using var fixture = new Fixture();
            await fixture.Seed();
            var bus = new GpuVfLifecycleTests.FakeGpu();
            using var provider = Provider(bus, fixture.Journal);
            using var scope = fixture.Bind();
            var command = Command(kind, Guid.NewGuid(), bus.Offsets.ToArray());
            bus.BeforeWrite = () =>
            {
                var attempt = new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None).GetAwaiter().GetResult().Unresolved.Single().GpuAttempt;
                Assert.IsNotNull(attempt);
                Assert.AreEqual(command.OperationId, attempt.StepId);
                Assert.AreEqual(kind, attempt.Kind);
                CollectionAssert.AreEqual(fixture.Baseline().VfOffsetsKhz, attempt.Before.VfOffsetsKhz);
                Assert.AreEqual(kind == "core" ? 10000 : 0, attempt.Pstate.CoreOffsetKhz);
                Assert.AreEqual(kind == "memory" ? 100000 : 0, attempt.Pstate.MemoryOffsetKhz);
                Assert.AreEqual(kind == "memory" ? 10000 : 20000, attempt.VfOffsetsKhz[1]);
            };
            Assert.AreEqual(CommandState.Applied, Execute(provider, command).State);
        }
    }

    [TestMethod]
    public async Task Latest_native_step_survives_parent_progress_and_keeps_first_parent_baseline()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var bus = new GpuVfLifecycleTests.FakeGpu { MemoryOffset = 1000 };
        using var provider = Provider(bus, fixture.Journal);
        using var scope = fixture.Bind();
        Assert.AreEqual(CommandState.Applied, Execute(provider, new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 1000, 0, true)).State);
        var command = Command("core", Guid.NewGuid(), bus.Offsets.ToArray());
        Assert.AreEqual(CommandState.Applied, Execute(provider, command).State);
        await fixture.Journal.SaveAsync(Pending, Identity, fixture.Id, true, CancellationToken.None, "user-a", "EndClientControlSession");
        var saved = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single();
        Assert.AreEqual(1000, saved.GpuRecovery!.Pstate.MemoryOffsetKhz);
        Assert.IsNotNull(saved.GpuAttempt);
        Assert.AreEqual(command.OperationId, saved.GpuAttempt.StepId);
        Assert.AreEqual(0, saved.GpuAttempt.Before.Pstate.MemoryOffsetKhz);
        Assert.AreEqual(10000, saved.GpuAttempt.Pstate.CoreOffsetKhz);
    }

    [TestMethod]
    public async Task Corrupt_latest_gpu_attempt_is_rejected_without_replacing_original_file()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var bus = new GpuVfLifecycleTests.FakeGpu();
        using var provider = Provider(bus, fixture.Journal);
        using var scope = fixture.Bind();
        Assert.AreEqual(CommandState.Applied, Execute(provider, Command("core", Guid.NewGuid(), bus.Offsets.ToArray())).State);
        var valid = await fixture.Journal.ReadAsync(CancellationToken.None);
        var saved = valid.Unresolved.Single();
        Assert.IsNotNull(saved.GpuAttempt);
        var attempt = saved.GpuAttempt;
        var changedFirstPoint = attempt.VfOffsetsKhz.ToArray(); changedFirstPoint[0] = 1;
        foreach (var invalid in new[] { attempt with { StepId = Guid.Empty }, attempt with { Kind = "unknown" },
            attempt with { Before = attempt.Before with { OwnerSid = "foreign" } },
            attempt with { Before = attempt.Before with { Adapter = Adapter with { BusId = 2 } } },
            attempt with { VfOffsetsKhz = [0] }, attempt with { VfOffsetsKhz = new int[127] },
            attempt with { VfOffsetsKhz = changedFirstPoint },
            attempt with { Pstate = attempt.Pstate with { CoreVoltageDeltaUv = 1 } } })
        {
            string text = JsonSerializer.Serialize(valid with { Unresolved = [saved with { GpuAttempt = invalid }] }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(fixture.File, text);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None));
            Assert.AreEqual(text, await File.ReadAllTextAsync(fixture.File));
        }
    }

    [TestMethod]
    public async Task Pending_native_recovery_restores_recognized_partial_states_under_original_parent()
    {
        foreach (string kind in new[] { "core", "memory", "vf" })
        {
            using var fixture = new Fixture();
            await fixture.Seed();
            var bus = new GpuVfLifecycleTests.FakeGpu { PauseStage = "partial" };
            using var provider = Provider(bus, fixture.Journal);
            bus.OnPause = () => provider.SetBackgroundWorkPaused(true);
            using var scope = fixture.Bind();
            int[] before = bus.Offsets.ToArray();
            Assert.AreEqual(CommandState.RecoveryRequired, Execute(provider, Command(kind, Guid.NewGuid(), before)).State);
            bus.Allowed = bus.ReadAllowed = true; bus.PauseStage = null;
            provider.SetBackgroundWorkPaused(false);
            var recovered = await ((IHomeHardwareProvider)provider).RecoverPendingGpuAsync(fixture.Id, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, recovered.State, recovered.Error?.Details.GetValueOrDefault("reason"));
            Assert.AreEqual(HardwareMutation.Changed, recovered.Mutation);
            Assert.AreEqual(0, bus.CoreOffset);
            Assert.AreEqual(0, bus.MemoryOffset);
            CollectionAssert.AreEqual(before, bus.Offsets);
            int writes = bus.Writes;
            Assert.AreEqual(HardwareMutation.NoMutation, (await ((IHomeHardwareProvider)provider).RecoverPendingGpuAsync(fixture.Id, CancellationToken.None)).Mutation);
            Assert.AreEqual(writes, bus.Writes);
            Assert.IsNotNull((await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().GpuAttempt);
            var latest = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().GpuAttempt!;
            Assert.IsFalse(await provider.VerifyPendingGpuTargetAsync(fixture.Id, CancellationToken.None));
            bus.CoreOffset = latest.Pstate.CoreOffsetKhz;
            bus.MemoryOffset = latest.Pstate.MemoryOffsetKhz;
            bus.Offsets = latest.VfOffsetsKhz.ToArray();
            Assert.IsTrue(await provider.VerifyPendingGpuTargetAsync(fixture.Id, CancellationToken.None));
            Assert.AreEqual(writes, bus.Writes, "Verified target checks must only read native values.");
            bus.IdentityReader = _ => latest.Before.Adapter with { DeviceId = latest.Before.Adapter.DeviceId + 1 };
            Assert.IsFalse(await provider.VerifyPendingGpuTargetAsync(fixture.Id, CancellationToken.None));
            Assert.AreEqual(writes, bus.Writes);
        }
    }

    [TestMethod]
    public async Task Pending_native_recovery_rejects_external_state_scope_and_missing_disk_record_without_writes()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var bus = new GpuVfLifecycleTests.FakeGpu { PauseStage = "partial" };
        bool acPower = true, latePower = false; int powerReads = 0; double temperature = 45;
        using var provider = Provider(bus, fixture.Journal, () => acPower && (!latePower || ++powerReads < 2), _ => temperature);
        bus.OnPause = () => provider.SetBackgroundWorkPaused(true);
        using var scope = fixture.Bind();
        Assert.AreEqual(CommandState.RecoveryRequired, Execute(provider, Command("core", Guid.NewGuid(), bus.Offsets.ToArray())).State);
        bus.Allowed = bus.ReadAllowed = true; bus.PauseStage = null;
        provider.SetBackgroundWorkPaused(false);
        string disk = await File.ReadAllTextAsync(fixture.File);
        int writes = bus.Writes;
        foreach (string change in new[] { "scope", "adapter", "core", "memory", "curve", "header", "missing", "power", "powerLate", "temperature", "temperatureNaN", "range" })
        {
            using var wrong = change == "scope" ? fixture.Journal.BindGpuOperation(new(Guid.NewGuid(), fixture.Session, 17, "user-a")) : null;
            if (change == "adapter") bus.IdentityReader = _ => Adapter with { BusId = 2 };
            if (change == "core") bus.CoreOffset = 12345;
            if (change == "memory") bus.MemoryOffset = 32123;
            if (change == "curve") bus.Offsets[7] = 12345;
            if (change == "header") bus.StateCount = 2;
            if (change == "missing") File.Delete(fixture.File);
            if (change == "power") acPower = false;
            if (change == "powerLate") { latePower = true; powerReads = 0; }
            if (change == "temperature") temperature = 75;
            if (change == "temperatureNaN") temperature = double.NaN;
            if (change == "range") bus.CoreMinimum = 1;
            var result = await ((IHomeHardwareProvider)provider).RecoverPendingGpuAsync(fixture.Id, CancellationToken.None);
            Assert.AreNotEqual(CommandState.Applied, result.State, change);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, change);
            Assert.AreEqual(writes, bus.Writes, change);
            bus.IdentityReader = _ => Adapter; bus.CoreOffset = 10000; bus.MemoryOffset = 0; bus.Offsets[7] = 0; bus.StateCount = 1;
            acPower = true; latePower = false; temperature = 45; bus.CoreMinimum = -200000;
            if (change == "missing") await File.WriteAllTextAsync(fixture.File, disk);
        }
    }

    [TestMethod]
    public async Task Interrupted_pending_recovery_retries_latest_before_without_undoing_earlier_memory_restore()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var bus = new GpuVfLifecycleTests.FakeGpu { MemoryOffset = 1000 };
        using var provider = Provider(bus, fixture.Journal);
        using var scope = fixture.Bind();
        Assert.AreEqual(CommandState.Applied, Execute(provider, new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 1000, 0, true)).State);
        int[] before = bus.Offsets.ToArray();
        bus.PauseStage = "partial"; bus.OnPause = () => provider.SetBackgroundWorkPaused(true);
        Assert.AreEqual(CommandState.RecoveryRequired, Execute(provider, Command("core", Guid.NewGuid(), before)).State);
        bus.Allowed = bus.ReadAllowed = true; provider.SetBackgroundWorkPaused(false);
        Assert.AreEqual(CommandState.RecoveryRequired, (await ((IHomeHardwareProvider)provider).RecoverPendingGpuAsync(fixture.Id, CancellationToken.None)).State);
        bus.Allowed = bus.ReadAllowed = true; bus.PauseStage = null; provider.SetBackgroundWorkPaused(false);
        Assert.AreEqual(CommandState.Applied, (await ((IHomeHardwareProvider)provider).RecoverPendingGpuAsync(fixture.Id, CancellationToken.None)).State);
        Assert.AreEqual(0, bus.MemoryOffset);
        Assert.AreEqual(0, bus.CoreOffset);
        CollectionAssert.AreEqual(before, bus.Offsets);
        var saved = (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single();
        Assert.AreEqual(1000, saved.GpuRecovery!.Pstate.MemoryOffsetKhz);
        Assert.AreEqual(0, saved.GpuAttempt!.Before.Pstate.MemoryOffsetKhz);
    }

    private static WindowsHomeHardwareProvider Provider(GpuVfLifecycleTests.FakeGpu bus, ControlExecutionJournal journal,
        Func<bool>? acPower = null, Func<CancellationToken, double?>? temperature = null)
    {
        var provider = new WindowsHomeHardwareProvider(bus.Create, acPower ?? (() => true), temperature ?? (_ => 45), journal);
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
            new([new("gpuMemoryOffset", CapabilityState.Available, null)])
            { Fields = [new("memoryOffsetKhz", CapabilityState.Available, true, true, -1_000_000, 3_000_000, 1,
                "kHz", "nvapi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)] },
            DeviceSupportState.Ready, null, Identity, null)
        { Controls = new(PerformanceMode.Quiet, false, []) { GpuVf = new(
            Enumerable.Range(0, 127).Select(index => new GpuVfNode(700 + index * 5, 1500, 0)).ToArray(), -200_000, 200_000, null)
            { MemoryOffsetKhz = 0, MemoryMinimumOffsetKhz = -1_000_000, MemoryMaximumOffsetKhz = 3_000_000 } } });
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
