using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<ClientControlSessionHandle> LegacyEmptyCapture(Fixture fixture, int? curve = null)
    {
        fixture.Provider.SetExternalVfOffset(60, 0);
        var first = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(first)).State);
        string path = Path.Combine(fixture.Root, "control-execution.json");
        byte[] oldJournal = File.ReadAllBytes(path);
        var capturer = ExplicitRestart(fixture, new ProcessProbe(fixture.Owner));
        var next = await capturer.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreEqual(CommandState.Applied, (await capturer.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(next.ClientSession!.SessionId, curve is int value ? new ExitDeviceDefaults(1, next.ClientSession.DeviceKey, value) : null),
            fixture.Owner, CancellationToken.None)).State);
        // Reproduce the old capture bug in the isolated fixture; never migrate real authority files in a test.
        File.WriteAllBytes(path, oldJournal);
        return next.ClientSession;
    }

    [TestMethod]
    public async Task Legacy_empty_completion_preserves_old_journal_and_supports_the_next_full_session()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await LegacyEmptyCapture(fixture);
        var original = ReadCapturedSession(fixture);
        string path = Path.Combine(fixture.Root, "control-execution.json");
        byte[] journal = File.ReadAllBytes(path);
        var runtime = ExplicitRestart(fixture, new ProcessProbe(fixture.Owner));
        int writes = fixture.Provider.WriteCount;
        var result = await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        CollectionAssert.AreEqual(journal, File.ReadAllBytes(path));
        var completed = ReadCapturedSession(fixture);
        Assert.AreEqual("Completed", completed.GetProperty("phase").GetString());
        Assert.IsTrue(JsonElement.DeepEquals(original.GetProperty("handle"), completed.GetProperty("handle")));
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")))!;
        Assert.IsNotNull(plan["legacyEmptyProof"]);
        Assert.AreEqual(CommandState.Applied,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        var next = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State, next.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreNotEqual(handle.SessionId, next.ClientSession!.SessionId);
        Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(next.ClientSession.SessionId, null), fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false)
            { ClientSessionId = next.ClientSession.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner)).State);
        Assert.AreEqual(CommandState.Applied,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), next.ClientSession.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    private sealed class LegacyClosingProbe(Fixture fixture) : IClientProcessProbe
    {
        public bool Interrupt { get; set; } = true;
        public ClientProcessState State { get; set; } = ClientProcessState.Running;
        public ClientProcessState Observe(int processId, long creationFileTimeUtc)
        {
            Assert.AreEqual(fixture.Owner.ProcessId, processId);
            Assert.AreEqual(fixture.Owner.ProcessCreationFileTimeUtc, creationFileTimeUtc);
            return Interrupt && ReadCapturedSession(fixture).GetProperty("phase").GetString() == "Closing"
                ? ClientProcessState.Unknown : State;
        }
    }

    [TestMethod]
    public async Task Legacy_empty_completion_resumes_the_same_no_mutation_parent_after_interruption_and_confirmed_exit()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await LegacyEmptyCapture(fixture);
        var probe = new LegacyClosingProbe(fixture);
        var runtime = ExplicitRestart(fixture, probe);
        string path = Path.Combine(fixture.Root, "control-execution.json");
        byte[] journal = File.ReadAllBytes(path);
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual("Closing", ReadCapturedSession(fixture).GetProperty("phase").GetString());
        string plan = File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json"));
        Assert.IsNotNull(JsonNode.Parse(plan)!["legacyEmptyProof"]);
        probe.Interrupt = false;
        probe.State = ClientProcessState.Exited;
        runtime = ExplicitRestart(fixture, probe);
        Assert.AreEqual(CommandState.Applied, (await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))?.State);
        Assert.AreEqual(plan, File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")), "Keep the exact original no-mutation parent.");
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        CollectionAssert.AreEqual(journal, File.ReadAllBytes(path));
        Assert.IsNull(await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Legacy_empty_completion_refuses_unbound_history_ownership_or_unmet_targets_without_changing_records()
    {
        foreach (string change in new[] { "archive", "scope", "pending", "gpu", "curve", "boost", "preview", "process" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await LegacyEmptyCapture(fixture, change == "curve" ? -30 : null);
            string path = Path.Combine(fixture.Root, "control-execution.json");
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            if (change == "scope") saved["recoveryScope"]!["serviceSessionId"] = Guid.NewGuid().ToString();
            if (change == "pending") saved["current"]!["windowsScopeSessionId"] = handle.SessionId.ToString();
            if (change is "scope" or "pending") File.WriteAllText(path, saved.ToJsonString());
            if (change == "archive")
            {
                string archive = Directory.GetFiles(Path.Combine(fixture.Root, "completed-client-sessions"), "*.json").Single();
                File.AppendAllText(archive, " ");
            }
            if (change == "gpu") fixture.Provider.SetExternalVfOffset(17, 12345);
            if (change == "boost") fixture.Provider.SetExternalBoost(1, 2);
            if (change == "preview") fixture.Provider.ReadBaselineOverride = baseline => baseline with { KeyboardLightingPreviewActive = true };
            var probe = new ProcessProbe(fixture.Owner) { State = change == "process" ? ClientProcessState.Unknown : ClientProcessState.Running };
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var result = await ExplicitRestart(fixture, probe).EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result.State, change);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, change);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, change);
            AssertOriginalRecords(fixture, records);
        }
    }

    [TestMethod]
    public async Task Legacy_empty_completion_revalidates_completed_proofs_before_receipt_or_new_capture()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await LegacyEmptyCapture(fixture);
        var runtime = ExplicitRestart(fixture, new ProcessProbe(fixture.Owner));
        Assert.AreEqual(CommandState.Applied,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        string path = Path.Combine(fixture.Root, "client-session-recovery.json");
        string original = File.ReadAllText(path);
        var changed = JsonNode.Parse(original)!;
        changed["legacyEmptyProof"]!["captureSha256"] = new string('0', 64);
        File.WriteAllText(path, changed.ToJsonString());
        var records = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        AssertOriginalRecords(fixture, records);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        File.WriteAllText(path, original);
        Assert.AreEqual(CommandState.Applied,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        string journalPath = Path.Combine(fixture.Root, "control-execution.json");
        File.AppendAllText(journalPath, " ");
        records = OriginalRecords(fixture);
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        AssertOriginalRecords(fixture, records);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Legacy_empty_completion_retries_published_active_plans_and_mutating_recovery_rejects_the_marker()
    {
        foreach (bool normalScope in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await LegacyEmptyCapture(fixture);
            string capturePath = Path.Combine(fixture.Root, "client-control-session.json");
            string active = File.ReadAllText(capturePath);
            var probe = new LegacyClosingProbe(fixture);
            Assert.AreEqual(CommandState.RecoveryRequired, (await ExplicitRestart(fixture, probe)
                .EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
            // Crash after plan publication but before Closing: replay that isolated durable state.
            File.WriteAllText(capturePath, active);
            string planPath = Path.Combine(fixture.Root, "client-session-recovery.json");
            string originalPlan = File.ReadAllText(planPath);
            if (normalScope)
            {
                string path = Path.Combine(fixture.Root, "control-execution.json");
                var saved = JsonNode.Parse(File.ReadAllText(path))!;
                saved["recoveryScope"]!["serviceSessionId"] = JsonNode.Parse(active)!["serviceSessionId"]!.DeepClone();
                saved["current"]!["hardwareEpoch"] = handle.HardwareEpoch;
                File.WriteAllText(path, saved.ToJsonString());
            }
            probe.Interrupt = false;
            int writes = fixture.Provider.WriteCount;
            var records = OriginalRecords(fixture);
            var result = await ExplicitRestart(fixture, probe).EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None);
            Assert.AreEqual(normalScope ? CommandState.RecoveryRequired : CommandState.Applied, result.State);
            if (normalScope) AssertOriginalRecords(fixture, records);
            Assert.AreEqual(originalPlan, File.ReadAllText(planPath));
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
        }
    }

    [TestMethod]
    public async Task Legacy_empty_completion_rechecks_live_values_and_power_after_closing_before_marking_completed()
    {
        foreach (string change in new[] { "vf", "boost", "power" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await LegacyEmptyCapture(fixture);
            bool Closing() => ReadCapturedSession(fixture).GetProperty("phase").GetString() == "Closing";
            fixture.Provider.ReadBaselineOverride = baseline =>
            {
                if (Closing())
                {
                    if (change == "vf") fixture.Provider.SetExternalVfOffset(17, 12345);
                    if (change == "boost") fixture.Provider.SetExternalBoost(1, 2);
                }
                return baseline;
            };
            var journal = new ControlExecutionJournal(fixture.Root);
            var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot,
                readPowerContinuity: token => Task.FromResult<PowerContinuityEvidence?>(change == "power" && Closing() ? new(201, 101) : RestartPower),
                clientProcessProbe: new ProcessProbe(fixture.Owner));
            int writes = fixture.Provider.WriteCount;
            byte[] original = File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json"));
            Assert.AreEqual(CommandState.RecoveryRequired,
                (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State, change);
            Assert.AreEqual("Closing", ReadCapturedSession(fixture).GetProperty("phase").GetString(), change);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, change);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(fixture.Root, "control-execution.json")));
        }
    }

    [TestMethod]
    public async Task Legacy_empty_completion_requires_exact_archived_fan_history_and_accepts_already_satisfied_device_defaults()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await LegacyEmptyCapture(fixture, -30);
        fixture.Provider.SetExternalCurve(-30);
        string path = Path.Combine(fixture.Root, "control-execution.json");
        string original = File.ReadAllText(path);
        var changed = JsonNode.Parse(original)!;
        var oldPlan = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")))!;
        Guid oldId = oldPlan["sessionId"]!.GetValue<Guid>();
        var archive = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "completed-client-sessions", oldId.ToString("N") + ".json")))!;
        var session = archive["session"]!.Deserialize<ClientControlSessionSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var fan = new FanEcRecoverySnapshot(new(session.ServiceSessionId, session.Handle.HardwareEpoch, oldId, session.OwnerSid, session.Identity),
            new([], [], [], false) { Unchanged = true });
        changed["fanEcRecovery"] = JsonSerializer.SerializeToNode(fan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(path, changed.ToJsonString());
        var records = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await ExplicitRestart(fixture, new ProcessProbe(fixture.Owner))
            .EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        AssertOriginalRecords(fixture, records);
        File.WriteAllText(path, original);
        var result = await ExplicitRestart(fixture, new ProcessProbe(fixture.Owner))
            .EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
    }
}
