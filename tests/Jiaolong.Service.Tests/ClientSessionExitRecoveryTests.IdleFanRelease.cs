using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(ClientControlSessionHandle Handle, Guid End)> SeedIdleFanReleaseExit(Fixture fixture, bool shutdown)
    {
        var handle = await fixture.Begin(null);
        var session = (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        string planPath = Path.Combine(fixture.Root, "client-session-recovery.json");
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!;
        var end = plan["operationId"]!.GetValue<Guid>();
        var expected = ControlPlanCompiler.Read(ControlPageId.Fan, fixture.Provider.Controls);
        plan["steps"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        {
            key = "fan", expected = SettingRequest.Set(expected), target = SettingRequest.Set("Auto"),
            status = "Failed", fanStrongCoolingTarget = false, fanFirmwareOnly = false
        }, ControlPlanCompiler.Json));
        File.WriteAllText(planPath, plan.ToJsonString());
        await new AtomicJsonStore(fixture.Root, "client-control-session.json").WriteAsync(
            session with { Phase = ClientControlSessionPhase.Closing }, CancellationToken.None);
        var operation = Guid.NewGuid();
        var fields = new[] { "fan.strategy", "fan.maximumRpm", "fan.fixedRpm", "fan.cpu", "fan.gpu" }.ToDictionary(key => key,
            key => new FieldExecutionState(operation, ControlOrigin.ManualLive,
                key == "fan.strategy" ? SettingRequest.Set("Auto") : null,
                key == "fan.strategy" ? SettingRequest.Set("Auto") : null, ApplyState.SubmittedUnverified)
            { ClientSessionId = handle.SessionId, WriteBefore = key == "fan.strategy" ? SettingRequest.Set("Auto") : null });
        var prior = await fixture.Journal.ReadAsync(CancellationToken.None);
        var endState = prior.Current! with { HardwareEpoch = handle.HardwareEpoch, Fields = fields, Pages = [] };
        var current = shutdown ? endState with { HardwareEpoch = handle.HardwareEpoch + 1,
            Pages = [new(ControlPageId.Fan, null, 0, ApplyState.Unknown, [], [], [], "RollbackFailed")] } : endState;
        var unresolved = new List<UnresolvedControlOperation>
        {
            new(end, DateTimeOffset.UtcNow, session.Identity, endState)
                { OwnerSid = session.OwnerSid, CommandKind = "EndClientControlSession" }
        };
        if (shutdown) unresolved.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, session.Identity, current)
            { OwnerSid = "service", CommandKind = nameof(ReleaseFanControlCommand) });
        var scope = new ControlJournalRecoveryScope(session.ServiceSessionId, fields.ToDictionary(pair => pair.Key,
            _ => new ControlFieldRecoveryScope(session.OwnerSid, handle.HardwareEpoch, Fixture.Scheme, 1)), null, 0, null);
        await new AtomicJsonStore(fixture.Root, "control-execution.json").WriteAsync(prior with
        { Current = current, RecoveryScope = scope, Unresolved = unresolved.ToArray() }, CancellationToken.None);
        // The affected machine has no mapped EC telemetry; success must come from the WAL and firmware evidence.
        fixture.Provider.FanReadbackAvailable = false;
        return (handle, end);
    }

    private static async Task AssertIdleFanReleaseExitRecovers()
    {
        foreach (bool shutdown in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedIdleFanReleaseExit(fixture, shutdown);
            int writes = fixture.Provider.WriteCount;
            fixture.Provider.FanRecovery = (_, _, _) => throw new AssertFailedException("No mutation must not dispatch fan recovery.");
            fixture.Provider.BeforeWrite = _ => throw new AssertFailedException("Original values already match; no write is authorized.");
            var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            var journal = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.AreEqual(0, journal.Unresolved.Length);
            Assert.AreEqual(0, journal.Current!.Fields.Count);
            Assert.IsNull(journal.FanEcRecovery);
            Assert.IsNull(journal.FanFirmwareRecovery);
            Assert.AreEqual(original.Handle.SessionId, (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Handle.SessionId);
            Assert.AreEqual(ClientControlSessionPhase.Completed, (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
            Assert.AreEqual(original.End, plan.RootElement.GetProperty("operationId").GetGuid());
            Assert.IsTrue(plan.RootElement.GetProperty("steps").EnumerateArray().All(step => step.GetProperty("status").GetString() == "Verified"));
        }
    }

    private static async Task AssertIdleFanReleaseExitRejectsUnprovenEvidence()
    {
        foreach (string fault in new[] { "firmware-unknown", "firmware-on", "live-plan", "owner", "epoch", "target", "same-session-wal", "unarchived-wal" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedIdleFanReleaseExit(fixture, shutdown: true);
            string path = Path.Combine(fixture.Root, "control-execution.json");
            var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            if (fault == "owner") saved = saved with { Unresolved = saved.Unresolved.Select(item => item.CommandKind == nameof(ReleaseFanControlCommand)
                ? item with { OwnerSid = "foreign" } : item).ToArray() };
            if (fault == "epoch") saved = saved with { Current = saved.Current! with { HardwareEpoch = original.Handle.HardwareEpoch + 2 } };
            if (fault is "same-session-wal" or "unarchived-wal")
            {
                var session = (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!;
                var scope = new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch,
                    fault == "same-session-wal" ? session.Handle.SessionId : Guid.NewGuid(), session.OwnerSid, session.Identity);
                saved = saved with { FanFirmwareRecovery = new(scope, 1, 1, 1, 1, true) };
            }
            await new AtomicJsonStore(fixture.Root, "control-execution.json").WriteAsync(saved, CancellationToken.None);
            if (fault == "firmware-unknown") fixture.Provider.StrongCoolingReadbackAvailable = false;
            if (fault == "firmware-on") fixture.Provider.FirmwareStrongCooling = true;
            if (fault == "live-plan") fixture.Provider.ControlStateTransform = controls => controls with
                { ActiveFanControlPlan = new([new(30, 30), new(100, 100)]) };
            if (fault == "target")
            {
                string planPath = Path.Combine(fixture.Root, "client-session-recovery.json");
                var plan = JsonNode.Parse(File.ReadAllText(planPath))!;
                plan["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "fan")!["fanStrongCoolingTarget"] = true;
                File.WriteAllText(planPath, plan.ToJsonString());
            }
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            fixture.Provider.BeforeWrite = _ => throw new AssertFailedException("Unproven recovery must preserve hardware.");
            fixture.Provider.FanRecovery = (_, _, _) => throw new AssertFailedException("Unproven recovery must preserve hardware.");
            var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result!.State, fault);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, fault);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, fault);
            AssertOriginalRecords(fixture, records);
        }
    }

    private static async Task AssertIdleFanRestartAcknowledgementPreservesCurrent()
    {
        foreach (bool released in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedIdleFanReleaseExit(fixture, shutdown: true);
            string capturePath = Path.Combine(fixture.Root, "client-control-session.json");
            string journalPath = Path.Combine(fixture.Root, "control-execution.json");
            string planPath = Path.Combine(fixture.Root, "client-session-recovery.json");
            var captureBytes = File.ReadAllBytes(capturePath);
            var journalBytes = File.ReadAllBytes(journalPath);
            var planBytes = File.ReadAllBytes(planPath);
            var boot = DateTimeOffset.UtcNow;
            var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
            HomeServiceRuntime NewRuntime() => new(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true,
                readBootTime: _ => Task.FromResult<DateTimeOffset?>(boot), readPowerContinuity: ReadRestartPower, clientProcessProbe: probe);
            fixture.Provider.SetExternalAcFrequency(4200);
            fixture.Provider.SetExternalCurve(-7);
            fixture.Provider.SetExternalLight(Fixture.OriginalLight with { Red = 42 });
            int writes = fixture.Provider.WriteCount, stops = fixture.Provider.LightingDynamicsStops;
            fixture.Provider.BeforeWrite = _ => throw new AssertFailedException("Acknowledgement must not write hardware.");
            fixture.Provider.BeforeStopLightingDynamics = _ => throw new AssertFailedException("Acknowledgement must not stop or write lighting.");
            var result = await NewRuntime().AcknowledgeIdleFanRestartAsync(original.Handle.SessionId, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State, JsonSerializer.Serialize(result.Error));
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(stops, fixture.Provider.LightingDynamicsStops);
            Assert.AreEqual(4200, fixture.Provider.Cpu.AcFrequency());
            Assert.AreEqual(-7, fixture.Provider.Cpu.NegativeCurveOptimizer);
            Assert.AreEqual((byte)42, fixture.Provider.Light.Red);
            using var plan = JsonDocument.Parse(File.ReadAllBytes(planPath));
            var proof = plan.RootElement.GetProperty("idleFanRestart");
            CollectionAssert.AreEqual(captureBytes, proof.GetProperty("capture").GetProperty("bytes").GetBytesFromBase64());
            CollectionAssert.AreEqual(journalBytes, proof.GetProperty("journal").GetProperty("bytes").GetBytesFromBase64());
            CollectionAssert.AreEqual(planBytes, proof.GetProperty("exitPlan").GetProperty("bytes").GetBytesFromBase64());
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "completed-client-sessions", original.Handle.SessionId.ToString("N") + ".json")));
            // Simulate interruption immediately before or after journal release; the original bytes stay in the decision.
            File.WriteAllBytes(capturePath, captureBytes);
            if (!released) File.WriteAllBytes(journalPath, journalBytes);
            result = (await NewRuntime().RecoverExitedClientSessionAsync(probe, CancellationToken.None))!;
            Assert.AreEqual(CommandState.Applied, result.State, JsonSerializer.Serialize(result.Error));
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            var runtime = NewRuntime();
            var nextOwner = fixture.Owner with { ProcessCreationFileTimeUtc = DateTimeOffset.UtcNow.ToFileTime() };
            var next = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), nextOwner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, next.State, JsonSerializer.Serialize(next.Error));
            var nextSession = (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!;
            Assert.AreEqual(4200, nextSession.Baseline.CpuTuning!.AcFrequency());
            Assert.AreEqual(-7, nextSession.Baseline.CpuTuning!.NegativeCurveOptimizer);
            Assert.AreEqual((byte)42, nextSession.Baseline.KeyboardLighting!.Red);
        }
    }

    private static async Task AssertIdleFanRestartAcknowledgementRejectsUnproven()
    {
        foreach (string fault in new[] { "same-boot", "running", "wrong-session", "firmware", "owner", "same-session-wal", "journal-changed" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var original = await SeedIdleFanReleaseExit(fixture, shutdown: true);
            var probe = new ProcessProbe(fixture.Owner) { State = fault == "running" ? ClientProcessState.Running : ClientProcessState.Exited };
            var boot = fault == "same-boot" ? RestartBoot : DateTimeOffset.UtcNow;
            if (fault == "firmware") fixture.Provider.FirmwareStrongCooling = true;
            var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            if (fault == "owner") saved = saved with { RecoveryScope = saved.RecoveryScope! with
                { FanOwnerClientSessionId = original.Handle.SessionId } };
            if (fault == "same-session-wal")
            {
                var session = (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!;
                saved = saved with { FanFirmwareRecovery = new(new(session.ServiceSessionId, session.Handle.HardwareEpoch,
                    session.Handle.SessionId, session.OwnerSid, session.Identity), 1, 1, 1, 1, true) };
            }
            await new AtomicJsonStore(fixture.Root, "control-execution.json").WriteAsync(saved, CancellationToken.None);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true,
                readBootTime: _ => Task.FromResult<DateTimeOffset?>(boot), clientProcessProbe: probe);
            var records = OriginalRecords(fixture);
            if (fault == "journal-changed") fixture.Provider.ReadBaselineOverride = controls =>
            {
                File.AppendAllText(Path.Combine(fixture.Root, "control-execution.json"), " ");
                return controls;
            };
            int writes = fixture.Provider.WriteCount;
            var result = await runtime.AcknowledgeIdleFanRestartAsync(
                fault == "wrong-session" ? Guid.NewGuid() : original.Handle.SessionId, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result.State, fault);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, fault);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, fault);
            if (fault != "journal-changed") AssertOriginalRecords(fixture, records);
            else
            {
                Assert.AreEqual(records["client-session-recovery.json"], File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
                Assert.AreEqual(records["client-control-session.json"], File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
            }
        }
    }
}
