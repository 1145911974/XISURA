using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Threading.Channels;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AutomaticRestoreTests
{
    private readonly RecordingPathProvider paths = new();
    private readonly List<HardwareCommand> sent = [];
    private static readonly CancellationToken Token = CancellationToken.None;

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(paths.LocalAppDataRoot)) Directory.Delete(paths.LocalAppDataRoot, true);
    }

    [TestMethod]
    public async Task Applied_snapshot_survives_reopen_without_changing_three_saved_presets()
    {
        var presets = new ControlPresetStore(paths);
        for (int slot = 1; slot <= 3; slot++)
            await presets.SaveAsync(new(1, ControlPageId.Performance, new(ControlModeId.Office, slot),
                $"slot {slot}", JsonSerializer.SerializeToElement(new { frequency = 3000 + slot * 100 }), DateTimeOffset.UtcNow), Token);
        var command = Cpu(4200, null);
        await new AppliedConfigurationRestore(paths).RecordAppliedAsync(command, Applied(command), Token);
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.AreEqual(4200, ((SetCpuTuningCommand)sent.Single()).Plan.MaxFrequencyMhz);
        for (int slot = 1; slot <= 3; slot++)
        {
            var saved = await presets.LoadAsync(ControlPageId.Performance, new(ControlModeId.Office, slot), Token);
            Assert.AreEqual(3000 + slot * 100, saved!.Payload.GetProperty("frequency").GetInt32());
        }
    }

    [TestMethod]
    public async Task Only_matching_successful_nontransient_commands_are_recorded()
    {
        var restore = new AppliedConfigurationRestore(paths);
        var good = Cpu(4100, null);
        await restore.RecordAppliedAsync(good, Applied(good), Token);
        var replacement = Cpu(4500, null);
        foreach (var result in new[] { Applied(replacement) with { State = CommandState.Rejected },
            Applied(replacement) with { RequiredAction = RequiredUserAction.Restart },
            Applied(replacement) with { IsReplay = true }, Applied(replacement) with { OperationId = Guid.NewGuid() } })
            await restore.RecordAppliedAsync(replacement, result, Token);
        foreach (HardwareCommand transient in new HardwareCommand[] {
            new SetGpuFrequencyLimitCommand(Guid.NewGuid(), 2400, true),
            new SetGpuPowerLimitCommand(Guid.NewGuid(), 80, true),
            new SetGpuVoltageBoostCommand(Guid.NewGuid(), 0, 1, true),
            new SetCpuTuningCommand(Guid.NewGuid(), new(75, null, null, null, null, null, null, null), true),
            new SetStrongCoolingCommand(Guid.NewGuid(), true),
            new SetKeyboardLightingCommand(Guid.NewGuid(), new("Fixed", 100) { Red = 0, Green = 255, Blue = 165 }) { Preview = true },
            new RestoreKeyboardLightingPreviewCommand(Guid.NewGuid()),
            new SetMuxModeCommand(Guid.NewGuid(), MuxMode.Discrete, true),
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, false) })
            await restore.RecordAppliedAsync(transient, Applied(transient), Token);
        await Replay(restore);
        Assert.HasCount(1, sent);
        Assert.AreEqual(4100, ((SetCpuTuningCommand)sent[0]).Plan.MaxFrequencyMhz);
        Assert.IsNull(((SetCpuTuningCommand)sent[0]).Plan.NegativeCurveOptimizer);
        var coCommands = new[]
        {
            Cpu(4100, -5),
            Cpu(4100, null) with { Plan = new(null, null, null, null, null, null, null, null)
                { Advanced = new(CurveOptimizerAll: -5) } },
            Cpu(4100, null) with { Plan = new(null, null, null, null, null, null, null, null)
                { Advanced = new(PerCoreCurveOptimizer: new Dictionary<int, int> { [0] = -5 }) } }
        };
        foreach (var co in coCommands)
        {
            sent.Clear();
            await restore.RecordAppliedAsync(co, Applied(co), Token);
            await Replay(restore);
            Assert.HasCount(1, sent);
            Assert.AreEqual(4100, ((SetCpuTuningCommand)sent[0]).Plan.MaxFrequencyMhz);
        }
        foreach (var co in coCommands)
        {
            sent.Clear();
            await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new
            {
                version = 1,
                commands = new Dictionary<string, HardwareCommand> { ["cpu"] = co },
                blockedGroups = new[] { "cpu" },
                pendingGroup = "cpu"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var report = await Replay(new AppliedConfigurationRestore(paths));
            Assert.HasCount(0, sent);
            Assert.IsNull(report.Warning);
        }
    }

    [TestMethod]
    public async Task Bios_selection_drops_old_offset_and_mode_change_drops_old_tuning()
    {
        var restore = new AppliedConfigurationRestore(paths);
        foreach (var command in new[] { Cpu(4000, null), Cpu(4400, null) })
            await restore.RecordAppliedAsync(command, Applied(command), Token);
        await Replay(restore);
        Assert.IsNull(((SetCpuTuningCommand)sent.Single()).Plan.NegativeCurveOptimizer);
        var mode = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet);
        await restore.RecordAppliedAsync(mode, Applied(mode), Token);
        sent.Clear();
        await Replay(restore);
        Assert.IsInstanceOfType<SetPerformanceModeCommand>(sent.Single());
    }

    [TestMethod]
    public async Task Every_restore_orders_mode_before_tuning_and_uses_new_operation_ids()
    {
        var restore = new AppliedConfigurationRestore(paths);
        HardwareCommand[] commands = [new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced),
            new SetLidLogoCommand(Guid.NewGuid(), true), Cpu(4300, null)];
        foreach (var command in commands) await restore.RecordAppliedAsync(command, Applied(command), Token);
        await Replay(restore);
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.HasCount(6, sent);
        Assert.IsInstanceOfType<SetPerformanceModeCommand>(sent[0]);
        Assert.IsInstanceOfType<SetCpuTuningCommand>(sent[1]);
        Assert.IsInstanceOfType<SetLidLogoCommand>(sent[2]);
        Assert.AreEqual(6, sent.Select(c => c.OperationId).Distinct().Count());
        Assert.IsFalse(sent.Any(c => commands.Any(original => original.OperationId == c.OperationId)));
    }

    [TestMethod]
    public async Task Failed_group_stays_blocked_across_restart_until_successful_manual_apply()
    {
        var restore = new AppliedConfigurationRestore(paths);
        var command = Cpu(4400, null);
        await restore.RecordAppliedAsync(command, Applied(command), Token);
        var result = await restore.RestoreAsync((c, _) => Task.FromResult(Applied(c) with { State = CommandState.RolledBack }),
            () => true, false, Token);
        Assert.IsNotNull(result.Warning);
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.HasCount(0, sent);
        await restore.RecordAppliedAsync(command, Applied(command), Token);
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.HasCount(1, sent);
    }

    [TestMethod]
    public async Task Interrupted_write_is_journaled_before_send_and_not_replayed()
    {
        var restore = new AppliedConfigurationRestore(paths);
        var command = Cpu(4400, null);
        await restore.RecordAppliedAsync(command, Applied(command), Token);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await restore.RestoreAsync(async (_, ct) =>
            {
                using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath));
                Assert.AreEqual("cpu", journal.RootElement.GetProperty("pendingGroup").GetString());
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Applied(command);
            }, () => true, false, cancellation.Token);
            Assert.Fail("Cancellation must propagate.");
        }
        catch (OperationCanceledException) { }
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.HasCount(0, sent);
    }

    [TestMethod]
    public async Task Corrupt_or_future_state_fails_closed_and_preserves_original_bytes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        foreach (var content in new[] { "{broken", "{\"version\":999,\"commands\":{}}", "null" })
        {
            await File.WriteAllTextAsync(StatePath, content);
            var restore = new AppliedConfigurationRestore(paths);
            var report = await Replay(restore);
            Assert.IsNotNull(report.Warning);
            Assert.HasCount(0, sent);
            var command = Cpu(4400, null);
            await Assert.ThrowsAsync<InvalidDataException>(() => restore.RecordAppliedAsync(command, Applied(command), Token));
            Assert.AreEqual(content, await File.ReadAllTextAsync(StatePath));
        }
    }

    [TestMethod]
    public async Task Adaptive_and_newer_manual_requests_prevent_stale_tuning_replay()
    {
        var restore = new AppliedConfigurationRestore(paths);
        foreach (HardwareCommand command in new HardwareCommand[] {
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), Cpu(4700, null),
            new SetLidLogoCommand(Guid.NewGuid(), true) })
            await restore.RecordAppliedAsync(command, Applied(command), Token);
        await restore.RestoreAsync(Send, () => true, true, Token);
        Assert.HasCount(2, sent);
        Assert.IsInstanceOfType<SetPerformanceModeCommand>(sent[0]);
        Assert.IsInstanceOfType<SetLidLogoCommand>(sent[1]);
        sent.Clear();
        await restore.RestoreAsync(Send, () => false, false, Token);
        Assert.HasCount(0, sent);
        await restore.RestoreAsync(Send, () => sent.Count == 0, false, Token);
        Assert.IsInstanceOfType<SetPerformanceModeCommand>(sent.Single());
    }

    [TestMethod]
    public async Task Unavailable_mode_defers_dependent_tuning_without_permanently_blocking_it()
    {
        var restore = new AppliedConfigurationRestore(paths);
        foreach (HardwareCommand command in new HardwareCommand[] {
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), Cpu(4700, null),
            new SetLidLogoCommand(Guid.NewGuid(), true) })
            await restore.RecordAppliedAsync(command, Applied(command), Token);
        var deferred = await restore.RestoreAsync(Send, () => true, false, Token,
            command => command is not SetPerformanceModeCommand);
        Assert.AreEqual(2, deferred.Skipped);
        Assert.IsInstanceOfType<SetLidLogoCommand>(sent.Single());
        sent.Clear();
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.HasCount(3, sent);
    }

    [TestMethod]
    public async Task Observed_mode_preserves_same_mode_tuning_and_replaces_external_or_adaptive_mode()
    {
        var restore = new AppliedConfigurationRestore(paths);
        var mode = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced);
        await restore.RecordAppliedAsync(mode, Applied(mode), Token);
        var cpu = Cpu(4200, null);
        await restore.RecordAppliedAsync(cpu, Applied(cpu), Token);
        await restore.RecordObservedModeAsync(PerformanceMode.Balanced, Token);
        await Replay(restore);
        Assert.HasCount(2, sent);
        sent.Clear();
        await restore.RecordObservedModeAsync(PerformanceMode.Quiet, Token);
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.AreEqual(PerformanceMode.Quiet, ((SetPerformanceModeCommand)sent.Single()).Mode);
        sent.Clear();
        await restore.RecordObservedModeAsync(PerformanceMode.Turbo, Token, () => false);
        await Replay(restore);
        Assert.AreEqual(PerformanceMode.Quiet, ((SetPerformanceModeCommand)sent.Single()).Mode);
        sent.Clear();
        await restore.RecordObservedModeAsync(PerformanceMode.Turbo, Token);
        await restore.RestoreAsync(Send, () => true, true, Token);
        Assert.AreEqual(PerformanceMode.Turbo, ((SetPerformanceModeCommand)sent.Single()).Mode);
    }

    private string StatePath => Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "last-applied-controls.json");

    [TestMethod]
    public async Task Lighting_preview_preserves_formal_configuration_and_is_never_replayed()
    {
        var restore = new AppliedConfigurationRestore(paths);
        var formal = new SetKeyboardLightingCommand(Guid.NewGuid(), new("Fixed", 100)
            { Red = 0, Green = 255, Blue = 165 });
        await restore.RecordAppliedAsync(formal, Applied(formal), Token);
        var original = await File.ReadAllBytesAsync(StatePath);
        var preview = formal with { OperationId = Guid.NewGuid(), Preview = true,
            Plan = formal.Plan with { Red = 255, Green = 0 } };
        await restore.RecordAppliedAsync(preview, Applied(preview), Token);
        var release = new RestoreKeyboardLightingPreviewCommand(Guid.NewGuid());
        await restore.RecordAppliedAsync(release, Applied(release), Token);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(StatePath));
        await Replay(new AppliedConfigurationRestore(paths));
        Assert.AreEqual(formal.Plan, ((SetKeyboardLightingCommand)sent.Single()).Plan);

        // A preview written by an older client must also fail closed on startup.
        var journal = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(StatePath))!;
        journal["commands"]!["lighting"]!["preview"] = true;
        await File.WriteAllTextAsync(StatePath, journal.ToJsonString());
        var stale = await File.ReadAllBytesAsync(StatePath);
        sent.Clear();
        Assert.IsNotNull((await Replay(new AppliedConfigurationRestore(paths))).Warning);
        Assert.HasCount(0, sent);
        CollectionAssert.AreEqual(stale, await File.ReadAllBytesAsync(StatePath));
    }

    [TestMethod]
    public async Task Session_records_real_reply_and_restores_on_start_and_only_one_wake_event()
    {
        await using var server = new RestorePipeService();
        var command = new SetLidLogoCommand(Guid.NewGuid(), true);
        await using (var session = new HomeControlSession(new ControlCenterClient(server.Name),
            automaticRestore: new AppliedConfigurationRestore(paths)))
        {
            var result = await session.ExecuteAsync(command, Token);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.AreEqual(command.OperationId, (await server.Next()).OperationId);
        }
        await using (var session = new HomeControlSession(new ControlCenterClient(server.Name),
            automaticRestore: new AppliedConfigurationRestore(paths)))
        {
            await session.StartAsync(Token);
            var startup = await server.Next();
            Assert.IsInstanceOfType<SetLidLogoCommand>(startup);
            Assert.AreNotEqual(command.OperationId, startup.OperationId);
            session.HandlePowerEvent(4);
            session.HandlePowerEvent(18);
            session.HandlePowerEvent(7);
            session.HandlePowerEvent(18);
            var wake = await server.Next();
            Assert.AreNotEqual(startup.OperationId, wake.OperationId);
            await Task.Delay(1300);
            Assert.IsFalse(server.Commands.Reader.TryRead(out _), "Duplicate wake notifications must not replay twice.");
        }
    }
    private Task<AutomaticRestoreReport> Replay(AppliedConfigurationRestore restore) =>
        restore.RestoreAsync(Send, () => true, false, Token);
    private Task<CommandResult> Send(HardwareCommand command, CancellationToken ct)
    {
        sent.Add(command);
        return Task.FromResult(Applied(command));
    }
    private static SetCpuTuningCommand Cpu(int mhz, int? curve) => new(Guid.NewGuid(),
        new CpuTuningPlan(null, null, null, mhz, null, null, null, curve), true);
    private static CommandResult Applied(HardwareCommand command) =>
        new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);

    // Only the out-of-process hardware boundary is simulated; client, session and disk store are real.
    private sealed class RestorePipeService : IAsyncDisposable
    {
        public string Name { get; } = "Jiaolong.RestoreTest." + Guid.NewGuid().ToString("N");
        public Channel<HardwareCommand> Commands { get; } = Channel.CreateUnbounded<HardwareCommand>();
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task serving;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public RestorePipeService() => serving = Serve();
        public async Task<HardwareCommand> Next() =>
            await Commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        private async Task Serve()
        {
            var ct = lifetime.Token;
            while (!ct.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                    while (!ct.IsCancellationRequested)
                    {
                        var prefix = new byte[4];
                        await pipe.ReadExactlyAsync(prefix, ct);
                        var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
                        await pipe.ReadExactlyAsync(bytes, ct);
                        var request = JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.MessageEnvelope)!;
                        MessageEnvelope response;
                        if (request is HelloEnvelope hello)
                            response = new HelloAckEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, hello.MessageId, "test");
                        else if (request is RequestEnvelope message)
                        {
                            if (message.Operation == "subscribeTelemetry") continue;
                            JsonElement payload;
                            if (message.Operation == "executeCommand")
                            {
                                var command = message.Payload.Deserialize(ProtocolJsonContext.Default.HardwareCommand)!;
                                payload = JsonSerializer.SerializeToElement(Applied(command), JsonOptions);
                                await Commands.Writer.WriteAsync(command, ct);
                            }
                            else
                            {
                                var state = new HomeStateSnapshot(new CapabilitySnapshot([
                                    new("quickSetting:lidLogo", CapabilityState.Available, null)]) { SupportState = DeviceSupportState.Ready },
                                    null, new HomeControlState(PerformanceMode.Balanced, false, []));
                                payload = JsonSerializer.SerializeToElement(state, JsonOptions);
                            }
                            response = new ResponseEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow,
                                message.MessageId, message.OperationId, ResponseStatus.Success, payload, null);
                        }
                        else continue;
                        var output = JsonSerializer.SerializeToUtf8Bytes(response, ProtocolJsonContext.Default.MessageEnvelope);
                        BinaryPrimitives.WriteInt32LittleEndian(prefix, output.Length);
                        await pipe.WriteAsync(prefix, ct);
                        await pipe.WriteAsync(output, ct);
                        await pipe.FlushAsync(ct);
                    }
                }
                catch (EndOfStreamException) { }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            await serving;
            lifetime.Dispose();
        }
    }
}
