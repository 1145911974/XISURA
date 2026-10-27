using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static readonly DateTimeOffset RestartBoot = DateTimeOffset.UtcNow.AddHours(-2);
    private static readonly PowerContinuityEvidence RestartPower = new(200, 100);
    private static Task<DateTimeOffset?> ReadRestartBoot(CancellationToken token) => Task.FromResult<DateTimeOffset?>(RestartBoot);
    private static Task<PowerContinuityEvidence?> ReadRestartPower(CancellationToken token) => Task.FromResult<PowerContinuityEvidence?>(RestartPower);
    private static HomeServiceRuntime Restarted(Fixture fixture,
        Func<CancellationToken, Task<DateTimeOffset?>>? boot = null,
        Func<CancellationToken, Task<PowerContinuityEvidence?>>? power = null)
    {
        var journal = new ControlExecutionJournal(fixture.Root);
        fixture.BindGpuJournal(journal);
        fixture.BindModeJournal(journal);
        return new(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: boot ?? ReadRestartBoot, readPowerContinuity: power ?? ReadRestartPower);
    }

    private static Dictionary<string, string> OriginalRecords(Fixture fixture) => Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(fixture.Root, path), File.ReadAllText, StringComparer.Ordinal);

    private static void AssertOriginalRecords(Fixture fixture, Dictionary<string, string> expected)
    {
        var actual = OriginalRecords(fixture);
        Assert.AreEqual(expected.Count, actual.Count);
        foreach (var pair in expected) Assert.AreEqual(pair.Value, actual[pair.Key], pair.Key);
    }

    private static void AssertPriorRecovery(CommandResult? result, string reason)
    {
        Assert.IsNotNull(result, "A restarted service must discover pending records without requiring a new client Begin.");
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.AreEqual(reason, result.Error!.Details["clientSessionRecovery"]);
    }

    [TestMethod]
    public async Task Restarted_worker_restores_original_exited_session_without_begin_or_rewriting_capture_identity()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(-30);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        int writes = fixture.Provider.WriteCount;
        var restarted = Restarted(fixture);
        Assert.AreNotEqual(fixture.Runtime.SessionId, restarted.SessionId);
        var result = await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.IsFalse(restarted.CanRunClientAutomation(fixture.Owner.UserSid));
        int settledWrites = fixture.Provider.WriteCount;
        var refused = await restarted.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false)
            { ClientSessionId = handle.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        Assert.AreEqual(CommandState.RecoveryRequired, refused.State);
        Assert.AreEqual(HardwareMutation.NoMutation, refused.Mutation);
        Assert.AreEqual(settledWrites, fixture.Provider.WriteCount);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.IsTrue(fixture.Provider.WriteCount > writes);
        var completed = ReadCapturedSession(fixture);
        Assert.AreEqual("Completed", completed.GetProperty("phase").GetString());
        Assert.AreEqual(fixture.Runtime.SessionId, completed.GetProperty("serviceSessionId").GetGuid());
        Assert.AreEqual(handle.HardwareEpoch, completed.GetProperty("handle").GetProperty("hardwareEpoch").GetInt64());
        Assert.AreEqual(handle.SessionId, completed.GetProperty("handle").GetProperty("sessionId").GetGuid());
        var settled = OriginalRecords(fixture);
        writes = fixture.Provider.WriteCount;
        Assert.IsNull(await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, settled);
    }

    [TestMethod]
    public async Task Restarted_worker_waits_for_original_process_or_requires_confirmed_exit_before_hardware_reads()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        var original = OriginalRecords(fixture);
        int keys = fixture.Provider.RecoveryKeyReads, diagnoses = fixture.Provider.DiagnoseReads;
        var restarted = Restarted(fixture);
        var probe = new ProcessProbe(fixture.Owner);
        Assert.IsNull(await restarted.RecoverExitedClientSessionAsync(probe, CancellationToken.None));
        probe.State = ClientProcessState.Unknown;
        AssertPriorRecovery(await restarted.RecoverExitedClientSessionAsync(probe, CancellationToken.None), "ClientProcessUnknown");
        Assert.AreEqual(keys, fixture.Provider.RecoveryKeyReads);
        Assert.AreEqual(diagnoses, fixture.Provider.DiagnoseReads);
        AssertOriginalRecords(fixture, original);
    }

    [TestMethod]
    public async Task Restarted_worker_rejects_missing_changed_or_unstable_boot_evidence_without_replacing_capture()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        var original = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        foreach (DateTimeOffset?[] values in new DateTimeOffset?[][] { [null], [RestartBoot.AddMinutes(-1)], [RestartBoot, RestartBoot.AddMinutes(1)] })
        {
            int reads = 0;
            var restarted = Restarted(fixture, _ => Task.FromResult(values[Math.Min(reads++, values.Length - 1)]));
            AssertPriorRecovery(await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None), "BootEvidenceMissingOrChanged");
            AssertOriginalRecords(fixture, original);
        }
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        using var legacy = new Fixture();
        var old = await legacy.Begin(null);
        await legacy.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), old);
        var legacyRecords = OriginalRecords(legacy);
        AssertPriorRecovery(await Restarted(legacy).RecoverExitedClientSessionAsync(new ProcessProbe(legacy.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None), "BootEvidenceMissingOrChanged");
        AssertOriginalRecords(legacy, legacyRecords);
    }

    [TestMethod]
    public async Task Restarted_worker_preserves_missing_corrupt_and_changed_original_records()
    {
        foreach (string damage in new[] { "capture-missing", "capture-corrupt", "journal-missing", "journal-corrupt", "scope-missing", "journal-changed", "current-fields-missing" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
            var restarted = Restarted(fixture);
            if (damage == "journal-changed") await restarted.ReadControlJournalAsync(CancellationToken.None);
            string capture = Path.Combine(fixture.Root, "client-control-session.json"), journalPath = Path.Combine(fixture.Root, "control-execution.json");
            if (damage == "capture-missing") File.Delete(capture);
            if (damage == "capture-corrupt") File.WriteAllText(capture, "{}");
            if (damage == "journal-missing") File.Delete(journalPath);
            if (damage == "journal-corrupt") File.WriteAllText(journalPath, "{}");
            if (damage is "scope-missing" or "journal-changed")
            {
                var json = JsonNode.Parse(File.ReadAllText(journalPath))!;
                if (damage == "scope-missing") json.AsObject().Remove("recoveryScope");
                else json["current"]!["generation"] = json["current"]!["generation"]!.GetValue<long>() + 1;
                File.WriteAllText(journalPath, json.ToJsonString());
            }
            if (damage == "current-fields-missing")
            {
                var json = JsonNode.Parse(File.ReadAllText(journalPath))!;
                json.AsObject().Remove("recoveryScope");
                json["current"]!["fields"] = null;
                File.WriteAllText(journalPath, json.ToJsonString());
            }
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            string reason = damage switch
            {
                "capture-missing" => "OriginalRecordsInvalid", "journal-missing" => "OriginalJournalMissing",
                "scope-missing" or "current-fields-missing" => "OriginalRecordsInvalid",
                "journal-changed" => "OriginalRecordsChanged", _ => "OriginalRecordsInvalid"
            };
            AssertPriorRecovery(await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None), reason);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }

    [TestMethod]
    public async Task Restarted_worker_rejects_another_device_without_mutating_original_records()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        var original = OriginalRecords(fixture);
        var foreign = new Provider(-50, new string('B', 64));
        var restarted = new HomeServiceRuntime(foreign, journal: new(fixture.Root), requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        AssertPriorRecovery(await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None), "DeviceIdentityMismatch");
        Assert.AreEqual(0, foreign.WriteCount);
        AssertOriginalRecords(fixture, original);
    }

    [TestMethod]
    public async Task Restarted_worker_detects_original_journal_changed_during_boot_recheck()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        string path = Path.Combine(fixture.Root, "control-execution.json"), capture = File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json"));
        string? changed = null;
        int reads = 0, writes = fixture.Provider.WriteCount;
        var restarted = Restarted(fixture, _ =>
        {
            if (++reads == 2)
            {
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                json["current"]!["generation"] = json["current"]!["generation"]!.GetValue<long>() + 1;
                File.WriteAllText(path, changed = json.ToJsonString());
            }
            return Task.FromResult<DateTimeOffset?>(RestartBoot);
        });
        AssertPriorRecovery(await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None), "OriginalRecordsChanged");
        Assert.IsNotNull(changed);
        Assert.AreEqual(changed, File.ReadAllText(path));
        Assert.AreEqual(capture, File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Restarted_worker_recovers_original_gpu_parent_and_preserves_unknown_external_changes()
    {
        foreach (bool conflict in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(-30);
            fixture.Provider.FailNextGpuWrite = true;
            var operation = Guid.NewGuid();
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetGpuMemoryOffsetCommand(operation, 0, 1000, true), handle)).State);
            fixture.Provider.RecoverOnRequest = true;
            if (conflict) fixture.Provider.ReadBaselineOverride = controls => controls with
                { CpuTuning = controls.CpuTuning! with { PerCoreCurveOptimizer = controls.CpuTuning.PerCoreCurveOptimizer!
                    .ToDictionary(pair => pair.Key, pair => pair.Key == 0 ? -6 : pair.Value) } };
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var result = await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            if (conflict)
            {
                AssertPriorRecovery(result, "LiveStateConflict:performance.negativeCurveOptimizer");
                Assert.AreEqual(0, fixture.Provider.GpuRecoveries);
                Assert.AreEqual(writes, fixture.Provider.WriteCount);
                AssertOriginalRecords(fixture, original);
                Assert.AreEqual(operation, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            }
            else
            {
                Assert.AreEqual(CommandState.Applied, result!.State);
                Assert.AreEqual(operation, fixture.Provider.RecoveredOperationId);
                Assert.AreEqual(1, fixture.Provider.GpuRecoveries);
                Assert.IsTrue(fixture.Provider.WriteCount > writes);
                Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
                CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
                Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
                Assert.AreEqual(ClientControlSessionPhase.Completed,
                    (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
            }
        }
    }

    [TestMethod]
    public async Task Restarted_worker_leaves_empty_and_completed_sessions_idle()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        Assert.IsNull(await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None));
        Assert.AreEqual(0, fixture.Provider.WriteCount);
        Assert.AreEqual(0, fixture.Provider.DiagnoseReads);
        Assert.AreEqual(0, OriginalRecords(fixture).Count);
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        var original = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        Assert.IsNull(await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, original);
    }
}
