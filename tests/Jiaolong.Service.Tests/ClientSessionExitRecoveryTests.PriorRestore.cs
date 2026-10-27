using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Prior_restore_reuses_exit_executor_under_original_scope_and_next_begin_uses_new_service_identity()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(-30);
        foreach (HardwareCommand command in new HardwareCommand[]
        {
            new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true),
            new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true),
            new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12, LogoEnabled = false }),
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced)
        }) Assert.AreEqual(CommandState.Applied, (await fixture.Write(command, handle)).State);
        string originalCapture = File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json"));
        var journal = new ControlExecutionJournal(fixture.Root);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
        fixture.Provider.ReadNativeModeScope = () => journal.CurrentNativeModeOperation;
        int scopedWrites = 0;
        fixture.Provider.BeforeWrite = async command =>
        {
            scopedWrites++;
            var pending = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.AreEqual(fixture.Runtime.SessionId, pending.RecoveryScope!.ServiceSessionId);
            Assert.AreEqual(handle.HardwareEpoch, pending.Current!.HardwareEpoch);
            Assert.IsTrue(pending.Current.Fields.Values.All(field => field.ClientSessionId == handle.SessionId));
            using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
            Assert.AreEqual("Closing", capture.RootElement.GetProperty("phase").GetString());
            Assert.AreEqual(fixture.Runtime.SessionId, capture.RootElement.GetProperty("serviceSessionId").GetGuid());
            Assert.AreEqual(handle.HardwareEpoch, capture.RootElement.GetProperty("handle").GetProperty("hardwareEpoch").GetInt64());
        };
        var restored = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(restored);
        Assert.AreEqual(CommandState.Applied, restored.State, restored.Error?.ToString());
        Assert.IsTrue(scopedWrites > 0);
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        var completed = await journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, completed.Current!.Fields.Count);
        Assert.AreEqual(0, completed.Unresolved.Length);
        Assert.AreEqual(fixture.Runtime.SessionId, completed.RecoveryScope!.ServiceSessionId);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual("Completed", saved.RootElement.GetProperty("phase").GetString());
        using var original = JsonDocument.Parse(originalCapture);
        foreach (var property in original.RootElement.EnumerateObject().Where(property => property.Name != "phase"))
            Assert.IsTrue(JsonElement.DeepEquals(property.Value, saved.RootElement.GetProperty(property.Name)), property.Name);
        fixture.Provider.BeforeWrite = null;
        int writes = fixture.Provider.WriteCount;
        Assert.IsNull(await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner with { ProcessId = 43 }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreNotEqual(handle.SessionId, next.ClientSession!.SessionId);
        Assert.AreNotEqual(handle.HardwareEpoch, next.ClientSession.HardwareEpoch);
        using var nextSaved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual(restarted.SessionId, nextSaved.RootElement.GetProperty("serviceSessionId").GetGuid());
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "completed-client-sessions", handle.SessionId.ToString("N") + ".json")));
    }

    [TestMethod]
    public async Task Prior_restore_failed_step_retains_original_scope_plan_and_closing_capture()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(-30);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 }), handle);
        fixture.Provider.FailMemoryRestore = true;
        var restarted = Restarted(fixture);
        var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(fixture.Runtime.SessionId, saved.RecoveryScope!.ServiceSessionId);
        Assert.AreEqual(handle.HardwareEpoch, saved.Current!.HardwareEpoch);
        Assert.IsTrue(saved.Current.Fields.ContainsKey("gpu.memoryOffsetKhz"));
        Assert.AreEqual(1, saved.Unresolved.Length);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual("Closing", capture.RootElement.GetProperty("phase").GetString());
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        var oldCommand = await restarted.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false)
            { ClientSessionId = handle.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        Assert.AreEqual(CommandState.RecoveryRequired, oldCommand.State);
        Assert.AreEqual(HardwareMutation.NoMutation, oldCommand.Mutation);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Prior_restore_never_writes_for_live_process_or_conflicting_evidence()
    {
        foreach (string condition in new[] { "running", "unknown", "external", "power" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
            if (condition == "external") fixture.Provider.SetExternalVfOffset(17, 12345);
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var restarted = Restarted(fixture, power: condition == "power"
                ? _ => Task.FromResult<PowerContinuityEvidence?>(new(400, 300)) : ReadRestartPower);
            var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = condition == "running" ? ClientProcessState.Running : condition == "unknown" ? ClientProcessState.Unknown : ClientProcessState.Exited }, CancellationToken.None);
            if (condition == "running") Assert.IsNull(result);
            else { Assert.IsNotNull(result); Assert.AreEqual(CommandState.RecoveryRequired, result.State); }
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }

    [TestMethod]
    public async Task Prior_restore_stops_after_power_evidence_changes_and_never_reports_no_mutation_after_a_write()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 }), handle);
        var current = RestartPower;
        var restarted = Restarted(fixture, power: _ => Task.FromResult<PowerContinuityEvidence?>(current));
        var restoredWrites = new List<string>();
        fixture.Provider.BeforeWrite = command =>
        {
            restoredWrites.Add(command.GetType().Name);
            if (command is SetGpuMemoryOffsetCommand { OffsetKhz: 0 }) current = new(400, 300);
            return Task.CompletedTask;
        };
        var result = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
        Assert.AreEqual("PriorRestoreInterrupted", result.Error!.Details["clientSessionRecovery"]);
        CollectionAssert.AreEqual(new[] { "SetGpuVfCurveCommand", "SetGpuMemoryOffsetCommand" }, restoredWrites);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual((byte?)12, fixture.Provider.Light.Red);
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(fixture.Runtime.SessionId, saved.RecoveryScope!.ServiceSessionId);
        Assert.IsTrue(saved.Unresolved.Length > 0);
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual("Closing", capture.RootElement.GetProperty("phase").GetString());
    }
}
