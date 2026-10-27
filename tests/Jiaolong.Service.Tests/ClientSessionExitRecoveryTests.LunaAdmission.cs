using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Pending_cpu_or_lighting_blocks_new_writes_and_internal_recovery_still_allows_next_session()
    {
        foreach (string kind in new[] { "cpu", "lighting" })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(null);
            HardwareCommand pending = kind == "cpu"
                ? new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true)
                : new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 });
            if (kind == "cpu") fixture.Provider.FailNextCpuWrite = true;
            else fixture.Provider.FailNextLightingWrite = true;
            var failed = await fixture.Write(pending, handle);
            Assert.AreEqual(CommandState.RecoveryRequired, failed.State, kind);
            var journalPath = Path.Combine(fixture.Root, "control-execution.json");
            byte[] original = await File.ReadAllBytesAsync(journalPath);
            int writes = fixture.Provider.WriteCount;

            var blocked = await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle);
            Assert.AreEqual(ErrorCode.RollbackFailed, blocked.Error?.Code, kind);
            Assert.AreEqual(CommandState.RecoveryRequired, blocked.State, kind);
            Assert.AreEqual(HardwareMutation.NoMutation, blocked.Mutation, kind);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, kind);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(journalPath), kind);

            var replayRuntime = new HomeServiceRuntime(fixture.Provider, journal: new ControlExecutionJournal(fixture.Root));
            var duplicate = await replayRuntime.ExecuteAsync(pending with { ClientSessionId = null }, CancellationToken.None);
            Assert.AreEqual(ErrorCode.IdempotencyConflict, duplicate.Error?.Code, kind);
            Assert.AreEqual(CommandState.RecoveryRequired, duplicate.State, kind);
            Assert.AreEqual(HardwareMutation.NoMutation, duplicate.Mutation, kind);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, kind);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(journalPath), kind);

            Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State, kind);
            Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length, kind);
            var next = await fixture.Begin(null);
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
                CpuPlan(4300, -4), true), next)).State, kind);
        }
    }

    [TestMethod]
    public async Task Pending_cpu_allows_reads_and_catalog_publication_but_blocks_preset_and_automatic_intent()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        fixture.Provider.FailNextCpuWrite = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            CpuPlan(4600, -5), true), handle)).State);
        var journalPath = Path.Combine(fixture.Root, "control-execution.json");
        byte[] original = await File.ReadAllBytesAsync(journalPath);
        int writes = fixture.Provider.WriteCount;

        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = FanPreset(key, false);
        Assert.IsTrue(PresetValidation.Validate(preset).IsValid);
        var store = new AdaptiveAutomationStateStore();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
        typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store,
            new Jiaolong.Service.Storage.AtomicJsonStore(fixture.Root, "adaptive-test.json"));
        typeof(AdaptiveAutomationStateStore).GetField("configurationOwnerSid", flags)!.SetValue(store, fixture.Owner.UserSid);
        typeof(AdaptiveAutomationStateStore).GetField("configuration", flags)!.SetValue(store,
            new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
                new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
                new(key, key, key, key, key), []));
        var bridge = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
        var runtime = new HomeServiceRuntime(bridge, journal: new ControlExecutionJournal(fixture.Root));

        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
            CancellationToken.None, userSid: fixture.Owner.UserSid)).State);
        var presetId = Guid.NewGuid();
        var presetCommand = new ApplyControlRequestCommand(presetId, new(presetId, 0, ControlOrigin.PresetUse,
            ControlModeId.Gaming, PerformanceMode.Turbo, [new(ControlPageId.Fan, true, key, preset.Revision)]));
        var presetResult = await runtime.ExecuteAsync(presetCommand, CancellationToken.None, userSid: fixture.Owner.UserSid);
        Assert.AreEqual(ErrorCode.RollbackFailed, presetResult.Error?.Code);
        Assert.AreEqual(CommandState.RecoveryRequired, presetResult.State);
        Assert.AreEqual(HardwareMutation.NoMutation, presetResult.Mutation);

        typeof(AdaptiveAutomationStateStore).GetField("manualOverrideUntilUtc", flags)!.SetValue(store, null);
        var automaticId = Guid.NewGuid();
        var automaticCommand = new ApplyControlRequestCommand(automaticId, new(automaticId, 0, ControlOrigin.Adaptive,
            ControlModeId.Gaming, PerformanceMode.Turbo, [new(ControlPageId.Fan, true, key, preset.Revision)]));
        var automaticResult = await bridge.ExecuteAutomaticCommandAsync(runtime, automaticCommand, CancellationToken.None);
        Assert.AreEqual(ErrorCode.RollbackFailed, automaticResult.Error?.Code);
        Assert.AreEqual(CommandState.RecoveryRequired, automaticResult.State);
        Assert.AreEqual(HardwareMutation.NoMutation, automaticResult.Mutation);

        await runtime.GetStateAsync(CancellationToken.None);
        await runtime.ReadControlDiagnosticsAsync(CancellationToken.None, fixture.Owner.UserSid);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(journalPath));
    }

    [TestMethod]
    public async Task Queued_write_is_rechecked_inside_gate_after_prior_write_becomes_unresolved()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var firstId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.FailNextCpuWrite = true;
        fixture.Provider.BeforeWrite = async command =>
        {
            if (command is SetCpuTuningCommand cpu && cpu.OperationId == firstId)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };

        var firstTask = fixture.Write(new SetCpuTuningCommand(firstId, CpuPlan(4600, -5), true), handle);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var nextCommand = new SetStrongCoolingCommand(Guid.NewGuid(), true) { ClientSessionId = handle.SessionId };
            var nextTask = fixture.Write(nextCommand, handle);
            Task<CommandResult>? admitted = null;
            for (int attempt = 0; attempt < 500 && admitted is null; attempt++)
            {
                admitted = fixture.Runtime.Execution.FindOperation(nextCommand, fixture.Owner.UserSid);
                if (admitted is null) await Task.Delay(1);
            }
            Assert.IsNotNull(admitted, "The second request must pass its outer check and queue behind the held hardware gate.");
            Assert.IsFalse(admitted!.IsCompleted);
            release.TrySetResult();

            Assert.AreEqual(CommandState.RecoveryRequired, (await firstTask).State);
            var journalPath = Path.Combine(fixture.Root, "control-execution.json");
            byte[] pending = await File.ReadAllBytesAsync(journalPath);
            int writes = fixture.Provider.WriteCount;
            var blocked = await nextTask;
            Assert.AreEqual(ErrorCode.RollbackFailed, blocked.Error?.Code);
            Assert.AreEqual(CommandState.RecoveryRequired, blocked.State);
            Assert.AreEqual(HardwareMutation.NoMutation, blocked.Mutation);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            CollectionAssert.AreEqual(pending, await File.ReadAllBytesAsync(journalPath));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Pending_operation_blocks_mux_at_gate_and_preserves_same_id_conflict()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var pending = new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true);
        fixture.Provider.FailNextCpuWrite = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(pending, handle)).State);

        var mux = new SetMuxModeCommand(Guid.NewGuid(), MuxMode.Discrete, true)
            { ClientSessionId = handle.SessionId };
        var check = typeof(HomeServiceRuntime).GetMethod("CheckPersistedAdmissionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        async Task<CommandResult?> Check(HardwareCommand command, bool blockAnyPending) =>
            await (Task<CommandResult?>)check.Invoke(fixture.Runtime,
                [command, CancellationToken.None, blockAnyPending])!;

        Assert.IsNull(await Check(mux, blockAnyPending: false));
        var journalPath = Path.Combine(fixture.Root, "control-execution.json");
        byte[] original = await File.ReadAllBytesAsync(journalPath);
        int writes = fixture.Provider.WriteCount;

        var blocked = await Check(mux, blockAnyPending: true);
        Assert.AreEqual(ErrorCode.RollbackFailed, blocked?.Error?.Code);
        Assert.AreEqual(CommandState.RecoveryRequired, blocked?.State);
        Assert.AreEqual(HardwareMutation.NoMutation, blocked?.Mutation);

        var duplicate = await Check(mux with { OperationId = pending.OperationId }, blockAnyPending: false);
        Assert.AreEqual(ErrorCode.IdempotencyConflict, duplicate?.Error?.Code);
        Assert.AreEqual(CommandState.RecoveryRequired, duplicate?.State);
        Assert.AreEqual(HardwareMutation.NoMutation, duplicate?.Mutation);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(journalPath));
    }
}
