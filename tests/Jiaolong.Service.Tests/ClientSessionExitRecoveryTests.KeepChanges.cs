using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Contracts.Errors;
using System.Text.Json;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Keep_exit_preserves_values_stops_automation_and_reopens_without_replay()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(-30);
        using var capabilityProvider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var productionFields = (FieldCapability[])typeof(WindowsHomeHardwareProvider).GetMethod("CreateFieldCapabilities", flags)!
            .Invoke(capabilityProvider, [new CapabilityDescriptor[] { new("keyboardLighting", CapabilityState.Available, null),
                new("quickSetting:lidLogo", CapabilityState.Available, null) }, null])!;
        var runtimeFields = fixture.Runtime.State.Capabilities.Fields;
        for (int index = 0; index < runtimeFields.Length; index++)
            if (runtimeFields[index].Key is "effect" or "brightness" or "hex" or "speed" or "logo")
                runtimeFields[index] = productionFields.Single(field => field.Key == runtimeFields[index].Key)
                    with { HardwareEpoch = fixture.Runtime.Execution.HardwareEpoch };
        await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), handle);
        await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        var logo = await fixture.Write(new SetLidLogoCommand(Guid.NewGuid(), false), handle);
        Assert.AreEqual(ApplyState.Confirmed, logo.ExecutionState!.Fields["lighting.logo"].WriteState);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12, LogoEnabled = false }), handle);
        var lightingJournal = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.IsTrue(lightingJournal.Current!.Fields.Where(pair => pair.Key.StartsWith("lighting."))
            .All(pair => pair.Value.WriteState == ApplyState.Confirmed));
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        int writes = fixture.Provider.WriteCount;
        string journalFile = Path.Combine(fixture.Root, "control-execution.json");
        byte[] originalJournal = File.ReadAllBytes(journalFile);
        fixture.Provider.BeforeStopLightingDynamics = async token =>
        {
            using var decision = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
            Assert.IsTrue(decision.RootElement.GetProperty("keepChanges").GetBoolean());
            Assert.AreEqual(ClientControlSessionPhase.Closing,
                (await new ClientControlSessionStore(fixture.Root).ReadAsync(token))!.Phase);
            CollectionAssert.AreEqual(originalJournal, File.ReadAllBytes(journalFile));
            throw new InvalidDataException("lightingStopReadbackMismatch");
        };
        var result = await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId,
            fixture.Owner, CancellationToken.None, keepChanges: true);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(1, fixture.Provider.LightingDynamicsStops);
        Assert.AreEqual(ClientControlSessionPhase.Closing,
            (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
        CollectionAssert.AreEqual(originalJournal, File.ReadAllBytes(journalFile));
        fixture.Provider.BeforeStopLightingDynamics = null;
        result = await fixture.End(handle);
        Assert.AreEqual(CommandState.Applied, result.State, JsonSerializer.Serialize(result.Error));
        Assert.AreEqual(2, fixture.Provider.LightingDynamicsStops);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-5, fixture.Provider.Cpu.PerCoreCurveOptimizer![0]);
        Assert.AreEqual((byte)12, fixture.Provider.Light.Red);
        Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(PerformanceMode.Quiet, fixture.Provider.Mode);
        Assert.IsFalse(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        var journal = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, journal.Unresolved.Length);
        Assert.AreEqual(0, journal.Current!.Fields.Count);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
        var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreNotEqual(handle.SessionId, next.ClientSession!.SessionId);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var capture = await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(4600, capture!.Baseline.CpuTuning!.AcFrequency());
        using var archive = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "completed-client-sessions", handle.SessionId.ToString("N") + ".json")));
        Assert.IsTrue(archive.RootElement.GetProperty("recovery").GetProperty("keepChanges").GetBoolean());
        Assert.AreEqual(CommandState.Applied, (await restarted.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(next.ClientSession.SessionId, null), fixture.Owner, CancellationToken.None)).State);
        await restarted.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4300, -4), true)
            { ClientSessionId = next.ClientSession.SessionId }, CancellationToken.None,
            fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        Assert.AreEqual(CommandState.Applied, (await restarted.EndClientControlSessionAsync(Guid.NewGuid(),
            next.ClientSession.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-5, fixture.Provider.Cpu.PerCoreCurveOptimizer![0]);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
    }

    [TestMethod]
    public async Task Keep_exit_rejects_foreign_process_and_preserves_unresolved_evidence()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Rejected, (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
            handle.SessionId, fixture.Owner with { ProcessId = 999 }, CancellationToken.None, keepChanges: true)).State);
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        await fixture.Journal.SaveAsync(saved.Current!, fixture.Provider.Identity, Guid.NewGuid(), true, CancellationToken.None);
        var original = File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json"));
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
            handle.SessionId, fixture.Owner, CancellationToken.None, keepChanges: true)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json")));
        using var stale = new Fixture();
        var old = await stale.Begin(-30);
        await stale.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), old);
        int oldWrites = stale.Provider.WriteCount;
        var restarted = new HomeServiceRuntime(stale.Provider, journal: new(stale.Root), requireClientSessions: true);
        Assert.AreEqual(CommandState.RecoveryRequired, (await restarted.EndClientControlSessionAsync(Guid.NewGuid(),
            old.SessionId, stale.Owner, CancellationToken.None, keepChanges: true)).State);
        Assert.AreEqual(oldWrites, stale.Provider.WriteCount, "Keep must never silently fall back to restoration after service restart.");
        Assert.AreEqual(4600, stale.Provider.Cpu.AcFrequency());
    }

    [TestMethod]
    public async Task Keep_exit_resumes_durable_decision_before_or_after_journal_release_without_hardware_writes()
    {
        await AssertIdleFanRestartAcknowledgementPreservesCurrent();
        foreach (bool released in new[] { false, true })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(-30);
            await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
            Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
                handle.SessionId, fixture.Owner, CancellationToken.None, keepChanges: true)).State);
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
            File.WriteAllText(Path.Combine(fixture.Root, "client-control-session.json"), plan.RootElement.GetProperty("retainedSession").GetRawText());
            if (!released) File.WriteAllText(Path.Combine(fixture.Root, "control-execution.json"), plan.RootElement.GetProperty("retainedJournal").GetRawText());
            int writes = fixture.Provider.WriteCount;
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            var result = await restarted.RecoverExitedClientSessionAsync(probe, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, JsonSerializer.Serialize(result.Error));
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
            Assert.IsFalse(restarted.CanRunClientAutomation(fixture.Owner.UserSid));
            var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, next.State);
        }
    }

    [TestMethod]
    public async Task Keep_exit_ipc_validates_optional_boolean_and_old_payload()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(-30);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        var dispatcher = new RequestDispatcher(homeRuntime: fixture.Runtime);
        RequestEnvelope Request(JsonElement payload) => new(new ProtocolVersion(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow,
            Guid.NewGuid(), "endClientSession", DateTimeOffset.UtcNow.AddSeconds(20), payload);
        foreach (var json in new[] { "{\"keepChanges\":true}",
            $"{{\"sessionId\":\"{handle.SessionId}\",\"keepChanges\":\"true\"}}",
            $"{{\"sessionId\":\"{handle.SessionId}\",\"keepChanges\":true,\"keepChanges\":false}}" })
            Assert.AreEqual(ErrorCode.InvalidFrame, (await dispatcher.DispatchAsync(Request(JsonSerializer.Deserialize<JsonElement>(json)),
                fixture.Owner, CancellationToken.None)).Error!.Code);
        int writes = fixture.Provider.WriteCount;
        var response = await dispatcher.DispatchAsync(Request(JsonSerializer.SerializeToElement(new { sessionId = handle.SessionId, keepChanges = true })),
            fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, response.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
    }

    [TestMethod]
    public async Task Keep_exit_cannot_replace_started_restore_or_clear_changed_durable_records()
    {
        await AssertIdleFanRestartAcknowledgementRejectsUnproven();
        using (var fixture = new Fixture())
        {
            var handle = await fixture.Begin(-30);
            await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
            fixture.Provider.FailMemoryRestore = true;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
            byte[] original = File.ReadAllBytes(Path.Combine(fixture.Root, "client-session-recovery.json"));
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
                handle.SessionId, fixture.Owner, CancellationToken.None, keepChanges: true)).State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(fixture.Root, "client-session-recovery.json")));
        }
        using (var fixture = new Fixture())
        {
            var handle = await fixture.Begin(-30);
            await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
            Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
                handle.SessionId, fixture.Owner, CancellationToken.None, keepChanges: true)).State);
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
            File.WriteAllText(Path.Combine(fixture.Root, "client-control-session.json"), plan.RootElement.GetProperty("retainedSession").GetRawText());
            var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            var altered = new ControlExecutionJournal(fixture.Root);
            await altered.SaveAsync(saved.Current!, fixture.Provider.Identity, Guid.NewGuid(), true, CancellationToken.None);
            byte[] original = File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json"));
            int writes = fixture.Provider.WriteCount;
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
            var result = await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json")));
        }
    }

    [TestMethod]
    public async Task Keep_exit_rejects_malformed_decision_without_erasing_records()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(),
            handle.SessionId, fixture.Owner, CancellationToken.None, keepChanges: true)).State);
        var file = Path.Combine(fixture.Root, "client-session-recovery.json");
        string original = File.ReadAllText(file);
        foreach (string member in new[] { "handle", "recoveryScope", "fields" })
        {
            var plan = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            File.WriteAllText(Path.Combine(fixture.Root, "client-control-session.json"), plan["retainedSession"]!.ToJsonString());
            if (member == "handle") plan["retainedSession"]![member] = null;
            else if (member == "fields") plan["retainedJournal"]!["current"]![member] = null;
            else plan["retainedJournal"]![member] = null;
            File.WriteAllText(file, plan.ToJsonString());
            byte[] before = File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json"));
            int writes = fixture.Provider.WriteCount;
            var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
            var result = await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json")));
        }
    }
}
