using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Restarted_turbo_session_rejects_old_branch_lease_and_restores_original_session_scope_after_exit()
    {
        using var fixture = TurboRestartFixture();
        var originalFan = fixture.Provider.Fan;
        var handle = await fixture.Begin(null);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Extreme);
        var applied = await fixture.Write(branch, handle);
        AssertTurboRestartOwned(fixture, handle, branch, applied);
        await CaptureTurboFanAsync(fixture, handle, originalFan);
        var original = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        var journal = new ControlExecutionJournal(fixture.Root);
        var restarted = TurboRestartRuntime(fixture, journal);
        var refused = await restarted.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId)
            { ClientSessionId = handle.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId,
            fixture.Owner.UserSid, fixture.Owner);
        Assert.AreEqual(CommandState.Rejected, refused.State);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, refused.Error?.Code);
        Assert.AreEqual(HardwareMutation.NoMutation, refused.Mutation);
        Assert.IsNull(await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Running }, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, original);

        int scopedWrites = 0;
        fixture.Provider.BeforeWrite = async command =>
        {
            scopedWrites++;
            var pending = await journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(fixture.Runtime.SessionId, pending.RecoveryScope!.ServiceSessionId);
            Assert.AreEqual(handle.HardwareEpoch, pending.Current!.HardwareEpoch);
            Assert.IsTrue(pending.Current.Fields.Values.All(field => field.ClientSessionId == handle.SessionId));
            using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
            Assert.AreEqual("Closing", capture.RootElement.GetProperty("phase").GetString());
            Assert.AreEqual(fixture.Runtime.SessionId, capture.RootElement.GetProperty("serviceSessionId").GetGuid());
        };
        var restored = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(restored);
        Assert.AreEqual(CommandState.Applied, restored.State, JsonSerializer.Serialize(restored));
        Assert.IsTrue(scopedWrites > 0);
        await AssertTurboSessionRestoredAsync(fixture, handle, journal);
        fixture.Provider.BeforeWrite = null;
        writes = fixture.Provider.WriteCount;
        Assert.IsNull(await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None));
        var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner with { ProcessId = 43 }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreNotEqual(handle.SessionId, next.ClientSession!.SessionId);
        Assert.AreNotEqual(handle.HardwareEpoch, next.ClientSession.HardwareEpoch);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "completed-client-sessions", handle.SessionId.ToString("N") + ".json")));
    }

    [TestMethod]
    public async Task Turbo_session_partial_exit_resumes_original_fan_step_in_another_service_without_replaying_cpu_or_mode()
    {
        using var fixture = TurboRestartFixture();
        var originalFan = fixture.Provider.Fan;
        var handle = await fixture.Begin(null);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Extreme);
        AssertTurboRestartOwned(fixture, handle, branch, await fixture.Write(branch, handle));
        await CaptureTurboFanAsync(fixture, handle, originalFan);
        var journal = new ControlExecutionJournal(fixture.Root);
        var first = TurboRestartRuntime(fixture, journal);
        fixture.Provider.FailFanRestore = true;
        var partial = await first.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(partial);
        Assert.AreEqual(CommandState.RecoveryRequired, partial.State, JsonSerializer.Serialize(partial));
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.DcFrequency());
        Assert.AreEqual(PerformanceMode.Balanced, fixture.Provider.Mode);
        Assert.AreEqual(true, fixture.Provider.Controls.StrongCooling);
        var pending = await journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(fixture.Runtime.SessionId, pending.RecoveryScope!.ServiceSessionId);
        Assert.IsTrue(pending.Current!.Fields.ContainsKey("fan.strongCooling"));
        using (var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json"))))
            Assert.AreEqual("Closing", capture.RootElement.GetProperty("phase").GetString());
        string planPath = Path.Combine(fixture.Root, "client-session-recovery.json");
        using var originalPlan = JsonDocument.Parse(File.ReadAllBytes(planPath));
        fixture.Provider.FailFanRestore = false;
        var resumedJournal = new ControlExecutionJournal(fixture.Root);
        var second = TurboRestartRuntime(fixture, resumedJournal);
        var resumedWrites = new List<HardwareCommand>();
        int fanRecoveryCalls = 0;
        int planChecks = 0;
        var recoverFan = fixture.Provider.FanRecovery!;
        fixture.Provider.FanRecovery = (id, scope, token) =>
        {
            fanRecoveryCalls++;
            Assert.AreEqual(fixture.Runtime.SessionId, scope.ServiceSessionId);
            Assert.AreEqual(handle.HardwareEpoch, scope.HardwareEpoch);
            Assert.AreEqual(handle.SessionId, scope.ClientSessionId);
            AssertOriginalPlan();
            return recoverFan(id, scope, token);
        };
        fixture.Provider.BeforeWrite = command =>
        {
            resumedWrites.Add(command);
            AssertOriginalPlan();
            return Task.CompletedTask;
        };
        void AssertOriginalPlan()
        {
            planChecks++;
            using var currentPlan = JsonDocument.Parse(File.ReadAllBytes(planPath));
            foreach (string property in new[] { "sessionId", "operationId", "windowsScopeSessionId" })
                Assert.IsTrue(JsonElement.DeepEquals(originalPlan.RootElement.GetProperty(property), currentPlan.RootElement.GetProperty(property)), property);
            var originalSteps = originalPlan.RootElement.GetProperty("steps").EnumerateArray().ToArray();
            var currentSteps = currentPlan.RootElement.GetProperty("steps").EnumerateArray().ToArray();
            Assert.AreEqual(originalSteps.Length, currentSteps.Length);
            for (int i = 0; i < originalSteps.Length; i++)
                foreach (string property in new[] { "key", "expected", "target", "expectedDetails", "targetDetails" })
                {
                    bool exists = originalSteps[i].TryGetProperty(property, out var expected);
                    Assert.AreEqual(exists, currentSteps[i].TryGetProperty(property, out var actual), property);
                    if (exists) Assert.IsTrue(JsonElement.DeepEquals(expected, actual), property);
                }
        }
        int writesBeforeResume = fixture.Provider.WriteCount;
        var restored = await second.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(restored);
        Assert.AreEqual(CommandState.Applied, restored.State, JsonSerializer.Serialize(restored));
        Assert.AreEqual(1, fanRecoveryCalls);
        Assert.IsTrue(planChecks > 0);
        Assert.AreEqual(writesBeforeResume, fixture.Provider.WriteCount);
        Assert.IsFalse(resumedWrites.Any(command => command is SetCpuTuningCommand or SetCpuTuningBatchCommand or SetPerformanceModeCommand));
        await AssertTurboSessionRestoredAsync(fixture, handle, resumedJournal);
    }

    private static Fixture TurboRestartFixture()
    {
        var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.Identity = new("board", "bios", "AMD Ryzen 9 7745HX", "gpu");
        fixture.Provider.CapabilitySupportState = DeviceSupportState.Ready;
        fixture.Provider.SetExternalMode(PerformanceMode.Balanced);
        fixture.Provider.JournalEveryModeStep = true;
        BindTurboModeJournal(fixture, fixture.Journal);
        return fixture;
    }

    private static void BindTurboModeJournal(Fixture fixture, ControlExecutionJournal journal)
    {
        fixture.Provider.ReadNativeModeScope = () => journal.CurrentNativeModeOperation;
        fixture.Provider.RecordModeWrite = (command, before, observed) => journal.RecordNativeModeAsync(
            command.OperationId, fixture.Provider.Identity, before, command.Mode, observed, CancellationToken.None);
    }

    private static HomeServiceRuntime TurboRestartRuntime(Fixture fixture, ControlExecutionJournal journal)
    {
        BindTurboModeJournal(fixture, journal);
        fixture.Provider.FanRecovery = async (id, scope, token) =>
        {
            Assert.AreEqual(new ControlExecutionJournal.FanRecoveryOperation(id, scope), journal.CurrentFanRecoveryOperation);
            var saved = await journal.ReadAsync(token);
            var checkpoint = saved.FanEcRecovery!;
            Assert.AreEqual(scope, checkpoint.Scope);
            if (fixture.Provider.FailFanRestore)
                return new(id, CommandState.Rejected, null, RequiredUserAction.None,
                    ServiceError.Create(ErrorCode.HardwareWriteFailed, id, false), false) { Mutation = HardwareMutation.NoMutation };
            fixture.Provider.CompleteFanRecovery(false);
            await journal.RecordFanEcCheckpointAsync(scope, checkpoint.Checkpoint with
                { Expected = new(checkpoint.Checkpoint.Baseline), Pending = new(), Released = true }, token);
            return new(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed };
        };
        return new(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
    }

    private static Task CaptureTurboFanAsync(Fixture fixture, ClientControlSessionHandle handle, FanEcControlState original)
    {
        // Model the provider's completed fan transaction; B field ownership comes from the actual branch command.
        static Dictionary<ushort, byte> Values(FanEcControlState fan) => new()
        { [0x1060] = fan.Initialization, [0x0B20] = fan.Control, [0xC83C] = fan.CpuTarget, [0xC83D] = fan.GpuTarget };
        var scope = new FanEcRecoveryScope(fixture.Runtime.SessionId, handle.HardwareEpoch, handle.SessionId,
            fixture.Owner.UserSid, fixture.Provider.Identity);
        return fixture.Journal.RecordFanEcCheckpointAsync(scope,
            new(Values(original), Values(fixture.Provider.Fan), new(), false), CancellationToken.None);
    }

    private static void AssertTurboRestartOwned(Fixture fixture, ClientControlSessionHandle handle,
        ApplyTurboBranchCommand branch, CommandResult result)
    {
        Assert.AreEqual(CommandState.Applied, result.State, JsonSerializer.Serialize(result));
        Assert.AreEqual(3800, fixture.Provider.Cpu.DcFrequency());
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.AreEqual(true, fixture.Provider.Controls.StrongCooling);
        foreach (string key in new[] { "performance.dcMaxFrequencyMhz", "fan.strongCooling" })
        {
            var field = result.ExecutionState!.Fields[key];
            Assert.AreEqual(branch.OperationId, field.OwnerOperationId, key);
            Assert.AreEqual(ControlOrigin.TurboBranch, field.Origin, key);
            Assert.AreEqual(handle.SessionId, field.ClientSessionId, key);
            Assert.AreEqual(ApplyState.Confirmed, field.WriteState, key);
        }
    }

    private static async Task AssertTurboSessionRestoredAsync(Fixture fixture, ClientControlSessionHandle handle, ControlExecutionJournal journal)
    {
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.DcFrequency());
        Assert.AreEqual(PerformanceMode.Balanced, fixture.Provider.Mode);
        Assert.AreEqual(false, fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.Control);
        var saved = await journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, saved.Current!.Fields.Count);
        Assert.AreEqual(0, saved.Unresolved.Length);
        Assert.IsTrue(saved.FanEcRecovery!.Checkpoint.Released);
        Assert.AreEqual(0, saved.FanEcRecovery.Checkpoint.Pending.Count);
        Assert.AreEqual(fixture.Runtime.SessionId, saved.RecoveryScope!.ServiceSessionId);
        Assert.AreEqual(handle.HardwareEpoch, saved.Current.HardwareEpoch);
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual("Completed", capture.RootElement.GetProperty("phase").GetString());
    }
}
