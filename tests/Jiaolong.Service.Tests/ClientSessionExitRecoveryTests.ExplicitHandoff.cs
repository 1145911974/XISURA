using System.Diagnostics;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static ClientIdentity LiveHandoffOwner()
    {
        using var process = Process.GetCurrentProcess();
        return ClientIdentity.LocalAdministrator("S-1-5-21-1", 1) with
        {
            ProcessId = process.Id, ProcessCreationFileTimeUtc = process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            ConnectionId = Guid.NewGuid()
        };
    }

    [TestMethod]
    public async Task Explicit_handoff_over_real_client_pipes_closes_the_original_live_capture_without_restoring_edit_authority()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower, owner: LiveHandoffOwner());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string name = "Xisura-explicit-handoff-" + Guid.NewGuid().ToString("N");
        await using var oldHost = new LifecyclePipeHost(fixture.Runtime, fixture.Owner, name);
        await using var client = new ControlCenterClient(name);
        var lifecycle = ManagedPipeLifecycle(client, client, -30);
        await lifecycle.StartAsync(deadline.Token);
        foreach (HardwareCommand command in new HardwareCommand[]
        {
            new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true),
            new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true),
            new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 })
        }) Assert.AreEqual(CommandState.Applied, (await lifecycle.SendAsync(command, client.SendCommandAsync, deadline.Token)).State);
        var original = ReadCapturedSession(fixture);
        Guid id = original.GetProperty("handle").GetProperty("sessionId").GetGuid();
        await oldHost.DisposeAsync();
        var runtime = RestartForPartialCpuEnd(fixture);
        await using var host = new LifecyclePipeHost(runtime, fixture.Owner, name);
        while (client.IsConnected) await Task.Delay(10, deadline.Token);
        await client.GetHomeStateAsync(deadline.Token);
        Assert.IsFalse(client.IsControlServiceCurrent);
        int writes = fixture.Provider.WriteCount;
        var records = OriginalRecords(fixture);
        Assert.IsNull(await runtime.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner), deadline.Token));
        AssertOriginalRecords(fixture, records);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SendAsync(new SetLidLogoCommand(Guid.NewGuid(), false),
            client.SendCommandAsync, deadline.Token));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var ended = await lifecycle.EndAsync(deadline.Token);
        Assert.AreEqual(CommandState.Applied, ended?.State, ended?.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        var completed = ReadCapturedSession(fixture);
        Assert.AreEqual("Completed", completed.GetProperty("phase").GetString());
        foreach (string key in new[] { "handle", "serviceSessionId", "processId", "processCreationFileTimeUtc" })
            Assert.IsTrue(JsonElement.DeepEquals(original.GetProperty(key), completed.GetProperty(key)), key);
        writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.Applied, (await client.EndClientSessionAsync(id, deadline.Token)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.IsFalse(client.IsControlServiceCurrent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BeginClientSessionAsync(deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConfigureClientSessionAsync(new(id, null), deadline.Token));
    }

    private static HomeServiceRuntime ExplicitRestart(Fixture fixture, IClientProcessProbe probe)
    {
        var journal = new ControlExecutionJournal(fixture.Root);
        fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
        return new(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower, clientProcessProbe: probe);
    }

    [TestMethod]
    public async Task Explicit_handoff_rejects_foreign_requests_uncertain_process_and_changed_original_scope_without_writes()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetLidLogoCommand(Guid.NewGuid(), false), handle);
        var probe = new ProcessProbe(fixture.Owner);
        var runtime = ExplicitRestart(fixture, probe);
        var original = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        foreach (var owner in new[]
        {
            fixture.Owner with { UserSid = "S-1-5-21-2" }, fixture.Owner with { SessionId = 2 },
            fixture.Owner with { ProcessId = 43 }, fixture.Owner with { ProcessCreationFileTimeUtc = fixture.Owner.ProcessCreationFileTimeUtc - 1 }
        }) Assert.AreEqual(CommandState.Rejected,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, owner, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Rejected,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        foreach (var state in new[] { ClientProcessState.Unknown, ClientProcessState.Exited })
        {
            probe.State = state;
            Assert.AreEqual(CommandState.RecoveryRequired,
                (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        }
        AssertOriginalRecords(fixture, original);
        probe.State = ClientProcessState.Running;
        string path = Path.Combine(fixture.Root, "control-execution.json");
        var changed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        changed["recoveryScope"]!["serviceSessionId"] = Guid.NewGuid().ToString();
        File.WriteAllText(path, changed.ToJsonString());
        original = OriginalRecords(fixture);
        var refused = await ExplicitRestart(fixture, probe).EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, refused.State);
        Assert.AreEqual("OriginalRecordsInvalid", refused.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        AssertOriginalRecords(fixture, original);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    private sealed class HandoffProcessProbe(ClientIdentity owner) : IClientProcessProbe
    {
        public int Calls { get; private set; }
        public bool FailAtWrite { get; set; } = true;
        public ClientProcessState Observe(int processId, long creationFileTimeUtc)
        {
            Assert.AreEqual(owner.ProcessId, processId);
            Assert.AreEqual(owner.ProcessCreationFileTimeUtc, creationFileTimeUtc);
            return ++Calls >= 3 && FailAtWrite ? ClientProcessState.Unknown : ClientProcessState.Running;
        }
    }

    [TestMethod]
    public async Task Explicit_handoff_rechecks_live_process_before_dispatch_and_retries_the_original_exit_plan()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(-30);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        var original = ReadCapturedSession(fixture);
        var probe = new HandoffProcessProbe(fixture.Owner);
        var runtime = ExplicitRestart(fixture, probe);
        int writes = fixture.Provider.WriteCount;
        var failed = await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, failed.State);
        Assert.IsTrue(probe.Calls >= 3);
        Assert.AreEqual(writes, fixture.Provider.WriteCount, "Process uncertainty at the dispatch gate must prevent the first write.");
        Assert.AreEqual("Closing", ReadCapturedSession(fixture).GetProperty("phase").GetString());
        string plan = Path.Combine(fixture.Root, "client-session-recovery.json");
        var parent = JsonDocument.Parse(File.ReadAllText(plan)).RootElement.GetProperty("operationId").GetGuid();
        probe.FailAtWrite = false;
        var retried = await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, retried.State, retried.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(parent, JsonDocument.Parse(File.ReadAllText(plan)).RootElement.GetProperty("operationId").GetGuid());
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.IsTrue(JsonElement.DeepEquals(original.GetProperty("handle"), ReadCapturedSession(fixture).GetProperty("handle")));
        Assert.IsFalse(runtime.CanRunClientAutomation(fixture.Owner.UserSid));
        writes = fixture.Provider.WriteCount;
        var edit = await runtime.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false)
            { ClientSessionId = handle.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        Assert.AreEqual(CommandState.RecoveryRequired, edit.State);
        Assert.AreEqual(HardwareMutation.NoMutation, edit.Mutation);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Explicit_handoff_failed_restore_can_continue_from_the_original_parent_after_confirmed_process_exit()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        var probe = new ProcessProbe(fixture.Owner);
        var runtime = ExplicitRestart(fixture, probe);
        fixture.Provider.FailMemoryRestore = true;
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        var saved = await runtime.ReadControlJournalAsync(CancellationToken.None);
        Guid parent = saved!.Unresolved.Single().OperationId;
        int writes = fixture.Provider.WriteCount;
        Assert.IsNull(await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        fixture.Provider.FailMemoryRestore = false;
        probe.State = ClientProcessState.Exited;
        Assert.AreEqual(CommandState.Applied, (await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))?.State);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual("Completed", ReadCapturedSession(fixture).GetProperty("phase").GetString());
        var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json"))).RootElement;
        Assert.AreEqual(parent, plan.GetProperty("operationId").GetGuid());
        writes = fixture.Provider.WriteCount;
        Assert.IsNull(await runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }
}
