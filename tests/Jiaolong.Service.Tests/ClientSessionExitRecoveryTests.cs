using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using System.Text.Json;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed partial class ClientSessionExitRecoveryTests
{
    private const int OriginalFrequency = 4700;
    private const int OriginalCurve = -50;

    [TestMethod]
    public async Task End_restores_session_fields_and_device_curve_default_once()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: -30);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with
        { Red = 12, Green = 34, Blue = 56, LogoEnabled = false }), handle);
        await fixture.Write(new SetLidLogoCommand(Guid.NewGuid(), false), handle);
        await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), handle);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);

        var ended = await fixture.End(handle);
        Assert.AreEqual(CommandState.Applied, ended.State);
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.DcFrequency());
        CollectionAssert.AreEqual(Enumerable.Repeat(-30, 8).ToArray(), fixture.Provider.Cpu.PerCoreCurveOptimizer!.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
        CollectionAssert.AreEqual(Enumerable.Repeat(0, 127).ToArray(), fixture.Provider.Offsets());
        Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.AreEqual(2, fixture.Provider.NativeModeScopes.Count);
        Assert.IsTrue(fixture.Provider.NativeModeScopes.All(scope => scope.ClientSessionId == handle.SessionId &&
            scope.ServiceSessionId == fixture.Runtime.SessionId && scope.HardwareEpoch == handle.HardwareEpoch && scope.OwnerSid == fixture.Owner.UserSid));
        Assert.AreEqual((byte)0, fixture.Provider.Fan.Control);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.CpuTarget);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.GpuTarget);
        var journal = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
        Assert.AreEqual(0, journal!.Current!.Fields.Count);
        Assert.AreEqual(0, journal.Unresolved.Length);

        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State,
            File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task End_preserves_external_change_and_restores_other_owned_fields()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: -30);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        fixture.Provider.SetExternalAcFrequency(4500);

        var ended = await fixture.End(handle);
        Assert.AreEqual(CommandState.RecoveryRequired, ended.State);
        Assert.AreEqual(4500, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.DcFrequency());
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        var journal = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
        Assert.IsTrue(journal!.Current!.Fields.ContainsKey("performance.acMaxFrequencyMhz"));
        Assert.IsTrue(journal.Unresolved.Length > 0);

        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(4500, fixture.Provider.Cpu.AcFrequency());
    }

    [TestMethod]
    public async Task End_keeps_failed_restore_evidence_and_retries_after_provider_recovers()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: -30);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 1 }), handle);
        fixture.Provider.FailMemoryRestore = true;

        var failed = await fixture.End(handle);
        Assert.AreEqual(CommandState.RecoveryRequired, failed.State);
        Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        var retained = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
        Assert.IsTrue(retained!.Current!.Fields.ContainsKey("gpu.memoryOffsetKhz"));
        Assert.IsTrue(retained.Unresolved.Length > 0);

        fixture.Provider.FailMemoryRestore = false;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State,
            File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        var completed = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
        Assert.AreEqual(0, completed!.Current!.Fields.Count);
        Assert.AreEqual(0, completed.Unresolved.Length);
    }

    [TestMethod]
    public async Task End_rejects_foreign_identity_session_and_stale_epoch_without_writes()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: null);
        int writes = fixture.Provider.WriteCount;

        Assert.AreEqual(CommandState.Rejected,
            (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Rejected,
            (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId,
                fixture.Owner with { ProcessId = fixture.Owner.ProcessId + 1 }, CancellationToken.None)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);

        await fixture.Runtime.ReinitializeAsync(CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);

        using var completed = new Fixture();
        var completedHandle = await completed.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await completed.End(completedHandle)).State);
        writes = completed.Provider.WriteCount;
        await completed.Runtime.ReinitializeAsync(CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await completed.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), completedHandle.SessionId, completed.Owner, CancellationToken.None)).State);
        Assert.AreEqual(writes, completed.Provider.WriteCount);
    }

    [TestMethod]
    public async Task End_without_matching_device_curve_override_restores_captured_curve_not_global_default()
    {
        using var fixture = new Fixture(curve: -17, deviceKey: new string('B', 64));
        var handle = await fixture.Begin(configureCurve: null);
        Assert.AreEqual(new string('B', 64), handle.DeviceKey);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(null, -4), true), handle);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);

        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        CollectionAssert.AreEqual(Enumerable.Repeat(-17, 8).ToArray(), fixture.Provider.Cpu.PerCoreCurveOptimizer!.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
    }

    [TestMethod]
    public async Task End_applies_configured_curve_default_even_without_a_cpu_write()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: -30);

        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        CollectionAssert.AreEqual(Enumerable.Repeat(-30, 8).ToArray(), fixture.Provider.Cpu.PerCoreCurveOptimizer!.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
        CollectionAssert.AreEqual(Enumerable.Repeat(0, 127).ToArray(), fixture.Provider.Offsets());
    }

    [TestMethod]
    public async Task End_resets_positive_core_offset_and_memory_offset_together()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: null);
        await fixture.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 15000, true)
            { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);

        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        CollectionAssert.AreEqual(Enumerable.Repeat(0, 127).ToArray(), fixture.Provider.Offsets());
    }

    [TestMethod]
    public async Task End_requires_recovery_when_client_session_snapshot_is_missing()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        File.Delete(Path.Combine(fixture.Root, "client-control-session.json"));
        int writes = fixture.Provider.WriteCount;

        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
    }

    [TestMethod]
    public async Task End_preserves_external_vf_change_but_restores_independent_memory_offset()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: null);
        await fixture.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 15000, true)
            { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        fixture.Provider.SetExternalVfOffset(60, 88000);

        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(15000, fixture.Provider.Gpu.CoreOffsetKhz);
        Assert.AreEqual(88000, fixture.Provider.Offsets()[60]);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
    }

    [TestMethod]
    public async Task Completed_session_is_archived_and_next_process_captures_a_fresh_baseline()
    {
        using var fixture = new Fixture();
        var first = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(first)).State);
        int writes = fixture.Provider.WriteCount;
        fixture.Provider.SetExternalAcFrequency(4400);
        fixture.Provider.Identity = new("board", "bios-v35", "cpu", "gpu");
        fixture.Provider.DeviceKey = new string('B', 64);
        var nextRuntime = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
        var nextOwner = fixture.Owner with { ProcessId = 43 };
        var next = await nextRuntime.BeginClientControlSessionAsync(Guid.NewGuid(), nextOwner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreNotEqual(first.SessionId, next.ClientSession!.SessionId);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        string archive = Path.Combine(fixture.Root, "completed-client-sessions", first.SessionId.ToString("N") + ".json");
        byte[] archived = await File.ReadAllBytesAsync(archive);
        var capture = await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(fixture.Provider.Identity, capture!.Identity);
        Assert.AreEqual(fixture.Provider.DeviceKey, capture.Handle.DeviceKey);
        Assert.AreEqual(4400, capture.Baseline.CpuTuning!.AcFrequency());
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archived)), capture.PreviousCompletedArchiveSha256);
        Assert.AreEqual(CommandState.Applied, (await nextRuntime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(next.ClientSession.SessionId, null), nextOwner, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Applied, (await nextRuntime.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(),
            CpuPlan(4300, -4), true) { ClientSessionId = next.ClientSession.SessionId }, CancellationToken.None,
            nextOwner.ConnectionId, nextOwner.UserSid, nextOwner)).State);
        Assert.AreEqual(CommandState.Applied, (await nextRuntime.EndClientControlSessionAsync(Guid.NewGuid(),
            next.ClientSession.SessionId, nextOwner, CancellationToken.None)).State);
        Assert.AreEqual(4400, fixture.Provider.Cpu.AcFrequency());
        CollectionAssert.AreEqual(archived, await File.ReadAllBytesAsync(archive));
    }

    [TestMethod]
    public async Task Completed_session_can_restart_service_but_missing_completion_evidence_blocks_begin()
    {
        using var fixture = new Fixture();
        var first = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(first)).State);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
        var recoveryPath = Path.Combine(fixture.Root, "client-session-recovery.json");
        string completed = await File.ReadAllTextAsync(recoveryPath);
        await File.WriteAllTextAsync(recoveryPath, "{}");
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await restarted.BeginClientControlSessionAsync(Guid.NewGuid(),
            fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual("{}", await File.ReadAllTextAsync(recoveryPath));
        await File.WriteAllTextAsync(recoveryPath, completed);
        fixture.Provider.Identity = new("board", "bios-v35", "cpu", "gpu");
        fixture.Provider.DeviceKey = new string('B', 64);
        restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
        var next = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(CommandState.Applied, (await restarted.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(next.ClientSession!.SessionId, null), fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Applied, (await restarted.EndClientControlSessionAsync(Guid.NewGuid(),
            next.ClientSession.SessionId, fixture.Owner, CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task End_ipc_accepts_only_session_id_and_verified_process()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var dispatcher = new RequestDispatcher(homeRuntime: fixture.Runtime);
        RequestEnvelope Request(object payload, ushort minor = 1) => new(new(1, minor), Guid.NewGuid(), DateTimeOffset.UtcNow,
            Guid.NewGuid(), "endClientSession", DateTimeOffset.UtcNow.AddSeconds(20), JsonSerializer.SerializeToElement(payload));
        int writes = fixture.Provider.WriteCount;
        foreach (var request in new[] { Request(new { sessionId = handle.SessionId }, 0), Request(new { sessionId = "bad" }),
            Request(new { sessionId = handle.SessionId, curveOptimizerAll = -30 }) })
            Assert.AreEqual(ErrorCode.InvalidFrame, (await dispatcher.DispatchAsync(request, fixture.Owner, CancellationToken.None)).Error!.Code);
        var foreign = await dispatcher.DispatchAsync(Request(new { sessionId = handle.SessionId }),
            fixture.Owner with { ProcessId = 99 }, CancellationToken.None);
        Assert.AreEqual(CommandState.Rejected, foreign.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var response = await dispatcher.DispatchAsync(Request(new { sessionId = handle.SessionId }), fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, response.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!.State);
    }

    [TestMethod]
    public async Task Process_guard_requires_confirmed_exit_and_recovers_with_original_identity_once()
    {
        using var fixture = new Fixture();
        Assert.IsFalse(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
        var handle = await fixture.Begin(-30);
        Assert.IsTrue(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
        Assert.IsFalse(fixture.Runtime.CanRunClientAutomation("S-1-5-21-other"));
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        Assert.IsNull(await fixture.Runtime.ReleaseClientFanAsync(fixture.Owner.ConnectionId));
        int writes = fixture.Provider.WriteCount;
        var probe = new ProcessProbe(fixture.Owner);
        Assert.IsNull(await fixture.Runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None));
        probe.State = ClientProcessState.Unknown;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        probe.State = ClientProcessState.Exited;
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.IsFalse(fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual(1, fixture.Provider.FanReleases);
        Assert.IsFalse(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
        writes = fixture.Provider.WriteCount;
        Assert.IsNull(await fixture.Runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Process_guard_retains_failed_recovery_and_stale_epoch_does_not_write()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        fixture.Provider.FailMemoryRestore = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.IsFalse(fixture.Runtime.CanRunClientAutomation(fixture.Owner.UserSid));
        fixture.Provider.FailMemoryRestore = false;
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);

        var next = await fixture.Begin(null);
        await fixture.Runtime.ReinitializeAsync(CancellationToken.None);
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.RecoverExitedClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    private sealed class ProcessProbe(ClientIdentity identity) : IClientProcessProbe
    {
        public ClientProcessState State { get; set; } = ClientProcessState.Running;
        public ClientProcessState Observe(int processId, long creationFileTimeUtc)
        {
            Assert.AreEqual(identity.ProcessId, processId);
            Assert.AreEqual(identity.ProcessCreationFileTimeUtc, creationFileTimeUtc);
            return State;
        }
    }

    [TestMethod]
    public void Windows_process_probe_distinguishes_live_identity_reused_pid_and_invalid_input()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        long birth = current.StartTime.ToUniversalTime().ToFileTimeUtc();
        var probe = new WindowsClientProcessProbe();
        Assert.AreEqual(ClientProcessState.Running, probe.Observe(current.Id, birth));
        Assert.AreEqual(ClientProcessState.Exited, probe.Observe(current.Id, birth + 1));
        Assert.AreEqual(ClientProcessState.Unknown, probe.Observe(0, birth));
        Assert.AreEqual(ClientProcessState.Unknown, probe.Observe(current.Id, 0));
    }

    [TestMethod]
    public async Task End_clears_preview_lease_even_when_visible_lighting_already_equals_baseline()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        fixture.Provider.PreviewActive = true;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.IsFalse(fixture.Provider.PreviewActive);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    [TestMethod]
    public async Task Automation_worker_does_not_probe_hardware_after_session_freeze()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(),
            handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        var store = new AdaptiveAutomationStateStore();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
        typeof(AdaptiveAutomationStateStore).GetField("configurationOwnerSid", flags)!.SetValue(store, fixture.Owner.UserSid);
        var key = PresetKey.Create(ControlModeId.Office, 1);
        var configuration = new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
            new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
            new(key, key, key, key, key), []);
        typeof(AdaptiveAutomationStateStore).GetField("configuration", flags)!.SetValue(store, configuration);
        var bridge = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
        using var worker = new AdaptiveAutomationWorker(store, fixture.Runtime, bridge,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveAutomationWorker>.Instance);
        int reads = fixture.Provider.TelemetryReads, writes = fixture.Provider.WriteCount;
        await (Task)typeof(AdaptiveAutomationWorker).GetMethod("TickAsync", flags)!.Invoke(worker, [CancellationToken.None])!;
        Assert.AreEqual(reads, fixture.Provider.TelemetryReads);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    private static CpuTuningPlan CpuPlan(int? acFrequency, int curve) =>
        new(null, null, null, null, null, null, Fixture.Scheme, null)
        {
            AcMaxFrequencyMhz = acFrequency,
            DcMaxFrequencyMhz = acFrequency,
            Advanced = new(PerCoreCurveOptimizer: Enumerable.Range(0, 8).ToDictionary(core => core, _ => curve))
        };

    [TestMethod]
    public async Task Windows_scopes_restore_first_baselines_after_repeated_scheme_visits_and_batch_writes()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var originalA = fixture.Provider.ReadScheme(Fixture.Scheme);
        var originalB = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        CpuTuningPlan Plan(Guid scheme, int ac, int dc) => new(null, null, null, null, null, null, scheme, null)
            { AcMaxFrequencyMhz = ac, DcMaxFrequencyMhz = dc, BoostPolicy = new(0, 6),
              AcMinActiveCoresPercent = 90, DcMinActiveCoresPercent = 10 };
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), Plan(Fixture.OtherScheme, 3800, 3200), true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningBatchCommand(Guid.NewGuid(),
            [Plan(Fixture.Scheme, 4600, 4500), Plan(Fixture.OtherScheme, 3700, 3100)], true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(Fixture.Scheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
        Assert.AreEqual(originalA, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(originalB, fixture.Provider.ReadScheme(Fixture.OtherScheme));
        Assert.IsNull((await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.WindowsScopeSessionId);
        string scopePath = Path.Combine(fixture.Root, "client-windows-" + handle.SessionId.ToString("N") + ".json");
        string proof = await File.ReadAllTextAsync(scopePath);
        File.Delete(scopePath);
        var nextOwner = fixture.Owner with { ProcessId = 43 };
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), nextOwner, CancellationToken.None)).State);
        await File.WriteAllTextAsync(scopePath, proof);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), nextOwner, CancellationToken.None)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Windows_scopes_preserve_external_inactive_index_and_keep_retry_evidence()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var plan = new CpuTuningPlan(null, null, null, null, null, null, Fixture.OtherScheme, null)
            { AcMaxFrequencyMhz = 3800, DcMaxFrequencyMhz = 3200 };
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), plan, true), handle)).State);
        fixture.Provider.SetActiveScheme(Fixture.Scheme);
        fixture.Provider.ExternalSchemeIndex(Fixture.OtherScheme, WindowsCpuSetting.AcMaxFrequencyMhz, 3600);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        var other = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        Assert.AreEqual(3600, other.AcMaxFrequencyMhz);
        Assert.AreEqual(3300, other.DcMaxFrequencyMhz);
        Assert.AreEqual(Fixture.Scheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
        Assert.AreEqual(handle.SessionId, (await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.WindowsScopeSessionId);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(3600, fixture.Provider.ReadScheme(Fixture.OtherScheme).AcMaxFrequencyMhz);
    }

    [TestMethod]
    public async Task Windows_scope_storage_failure_prevents_dispatch_and_missing_evidence_blocks_exit()
    {
        using (var fixture = new Fixture())
        {
            var handle = await fixture.Begin(null);
            Directory.CreateDirectory(Path.Combine(fixture.Root, "client-windows-" + handle.SessionId.ToString("N") + ".json"));
            int writes = fixture.Provider.WriteCount;
            var result = await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
            Assert.AreNotEqual(CommandState.Applied, result.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
        }
        using (var fixture = new Fixture())
        {
            var handle = await fixture.Begin(null);
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle)).State);
            string path = Path.Combine(fixture.Root, "client-windows-" + handle.SessionId.ToString("N") + ".json");
            string saved = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, saved.Replace(fixture.Owner.UserSid, "S-1-5-21-foreign", StringComparison.Ordinal));
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            File.Delete(path);
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        }
    }

    [TestMethod]
    public async Task Windows_scopes_capture_compiled_presets_and_drop_proven_no_mutation_ownership()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        fixture.Provider.RejectCpuWrites = true;
        Assert.AreNotEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle)).State);
        fixture.Provider.RejectCpuWrites = false;
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root,
            "client-windows-" + handle.SessionId.ToString("N") + ".json"))))
            Assert.IsTrue(saved.RootElement.GetProperty("schemes").EnumerateObject().All(scheme =>
                !scheme.Value.GetProperty("writes").EnumerateObject().Any()));
        var original = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key, "scope",
            JsonSerializer.SerializeToElement(new { temperatureLimitC = 85, splWatts = 55, spptWatts = 65,
                acMaxFrequencyMhz = 3800, dcMaxFrequencyMhz = 3200, isBoostEnabled = true, windowsPowerSchemeId = Fixture.OtherScheme,
                acMinActiveCoresPercent = (int?)null, dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null,
                advancedCpuTuning = (AdvancedCpuTuningPlan?)null, useOfficialCpuPolicy = true }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["windowsPowerSchemeId"] = SettingRequest.Set(Fixture.OtherScheme),
                ["acMaxFrequencyMhz"] = SettingRequest.Set(3800), ["dcMaxFrequencyMhz"] = SettingRequest.Set(3200) } };
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), handle)).State);
        var id = Guid.NewGuid();
        var applied = await fixture.Write(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
            ControlModeId.Gaming, PerformanceMode.Turbo, [new(ControlPageId.Performance, true, key, 1)])), handle);
        Assert.AreEqual(3800, fixture.Provider.ReadScheme(Fixture.OtherScheme).AcMaxFrequencyMhz, applied.Error?.Code.ToString());
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(original, fixture.Provider.ReadScheme(Fixture.OtherScheme));
        Assert.AreEqual(Fixture.Scheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
    }

    [TestMethod]
    public async Task Quick_aliases_share_session_ownership_and_restore_lighting_and_fan()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.LidLogo, false), handle)).State);
        Assert.IsFalse(fixture.Provider.Light.LogoEnabled);
        Assert.IsTrue((await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.Fields.ContainsKey("lighting.logo"));
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.StrongCooling, true), handle)).State);
        Assert.IsTrue(fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.IsFalse(fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.Control);
        Assert.AreEqual(0, (await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.Fields.Count);
    }

    [TestMethod]
    public async Task Quick_aliases_keep_identity_idempotency_and_freeze_guards()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var id = Guid.NewGuid();
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetQuickSettingCommand(id, QuickSettingKind.LidLogo, false), handle)).State);
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetLidLogoCommand(id, false), handle)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var foreign = fixture.Owner with { ProcessCreationFileTimeUtc = fixture.Owner.ProcessCreationFileTimeUtc - 1 };
        Assert.AreEqual(CommandState.Rejected, (await fixture.Runtime.ExecuteAsync(new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.LidLogo, true)
            { ClientSessionId = handle.SessionId }, CancellationToken.None, foreign.ConnectionId, foreign.UserSid, foreign)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Rejected, (await fixture.Write(new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.StrongCooling, true), handle)).State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
    }

    [TestMethod]
    public async Task Quick_logo_restore_preserves_external_light_change()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.LidLogo, false), handle)).State);
        var external = fixture.Provider.Light with { Red = 1, Green = 2, Blue = 3 };
        await fixture.Provider.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), external), CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(external, fixture.Provider.Light);
        Assert.IsTrue((await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.Fields.ContainsKey("lighting.logo"));
    }

    [TestMethod]
    public async Task Pbo_scalar_restores_its_session_baseline_without_windows_scope_changes()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var original = fixture.Provider.ReadScheme(Fixture.Scheme);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            new(null, null, null, null, null, null, null, null) { Advanced = new(PboScalar: 10) }, true), handle)).State);
        Assert.AreEqual(10, fixture.Provider.Cpu.PboScalar);
        Assert.IsNull((await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.WindowsScopeSessionId);
        var other = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        fixture.Provider.SetActiveScheme(Fixture.OtherScheme);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(1, fixture.Provider.Cpu.PboScalar);
        Assert.AreEqual(original, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(other, fixture.Provider.ReadScheme(Fixture.OtherScheme));
        Assert.AreEqual(Fixture.OtherScheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
    }

    [TestMethod]
    public async Task Session_fan_lease_survives_pipe_disconnect_and_peer_connection()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        int writes = fixture.Provider.WriteCount;
        Assert.IsNull(await fixture.Runtime.ReleaseClientFanAsync(fixture.Owner.ConnectionId));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.IsTrue(fixture.Provider.Controls.StrongCooling);
        var peer = fixture.Owner with { ConnectionId = Guid.NewGuid() };
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true)
            { ClientSessionId = handle.SessionId }, CancellationToken.None, peer.ConnectionId, peer.UserSid, peer)).State);
        writes = fixture.Provider.WriteCount;
        Assert.IsNull(await fixture.Runtime.ReleaseClientFanAsync(peer.ConnectionId));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.IsFalse(fixture.Provider.Controls.StrongCooling);
        Assert.AreEqual(1, fixture.Provider.FanReleases);
    }

    [TestMethod]
    public async Task End_without_fan_ownership_preserves_external_ec_and_accepts_missing_ec_readback()
    {
        foreach (bool readable in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Provider.FanReadbackAvailable = readable;
            fixture.Provider.SetExternalFan(0x0A, 37, 41);
            var handle = await fixture.Begin(null);
            var initial = fixture.Provider.Fan;
            Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
            Assert.AreEqual(0, fixture.Provider.FanReleases);
            Assert.AreEqual(initial.Control, fixture.Provider.Fan.Control);
            Assert.AreEqual(initial.CpuTarget, fixture.Provider.Fan.CpuTarget);
            Assert.AreEqual(initial.GpuTarget, fixture.Provider.Fan.GpuTarget);
        }
    }

    [TestMethod]
    public async Task Session_end_and_pipe_cleanup_release_owned_fan_once()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        var end = fixture.End(handle);
        var cleanup = fixture.Runtime.ReleaseClientFanAsync(fixture.Owner.ConnectionId);
        await Task.WhenAll((Task)end, cleanup);
        Assert.AreEqual(CommandState.Applied, end.Result.State);
        Assert.IsNull(cleanup.Result);
        Assert.AreEqual(1, fixture.Provider.FanReleases);
        await fixture.End(handle);
        Assert.IsNull(await fixture.Runtime.ReleaseClientFanAsync(fixture.Owner.ConnectionId));
        Assert.AreEqual(1, fixture.Provider.FanReleases);
        Assert.AreEqual(0, (await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.Fields.Count);
    }

    [TestMethod]
    public async Task End_restores_original_firmware_cooling_flag_after_enabling_or_disabling_and_retry()
    {
        foreach (bool enabled in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Provider.FirmwareStrongCooling = true;
            var handle = await fixture.Begin(null);
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), enabled), handle)).State);
            Assert.IsFalse(fixture.Provider.FirmwareStrongCooling);
            fixture.Provider.FailFanRestore = true;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
            Assert.IsTrue((await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Unresolved.Length > 0);
            fixture.Provider.FailFanRestore = false;
            Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
            Assert.IsTrue(fixture.Provider.FirmwareStrongCooling);
            Assert.AreEqual((byte)0, fixture.Provider.Fan.Control);
            Assert.AreEqual((byte)0, fixture.Provider.Fan.CpuTarget);
            Assert.AreEqual((byte)0, fixture.Provider.Fan.GpuTarget);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
        }
    }

    [TestMethod]
    public async Task End_retains_owned_fan_when_startup_firmware_cooling_is_unknown()
    {
        using var fixture = new Fixture();
        fixture.Provider.StrongCoolingReadbackAvailable = false;
        var handle = await fixture.Begin(null);
        fixture.Provider.StrongCoolingReadbackAvailable = true;
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(0, fixture.Provider.FanReleases);
        Assert.IsTrue(fixture.Provider.Controls.StrongCooling);
        Assert.IsTrue((await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.Fields.ContainsKey("fan.strongCooling"));
    }

    [TestMethod]
    public async Task Failed_core_restore_defers_dependent_curve_but_restores_memory_and_retries()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10000, true)
            { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle)).State);
        int[] before = fixture.Provider.Offsets();
        fixture.Provider.FailCoreRestore = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(10000, fixture.Provider.Gpu.CoreOffsetKhz);
        CollectionAssert.AreEqual(before, fixture.Provider.Offsets());
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        fixture.Provider.FailCoreRestore = false;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
    }

    [TestMethod]
    public async Task Core_restore_with_lost_ack_reconciles_saved_curve_checkpoint_on_retry()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10000, true)
            { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle)).State);
        fixture.Provider.UnconfirmedCoreRestore = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
        CollectionAssert.AreEqual(fixture.Provider.OriginalOffsets, fixture.Provider.Offsets());
        fixture.Provider.UnconfirmedCoreRestore = false;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
    }

    [TestMethod]
    public async Task Core_checkpoint_recovery_preserves_external_curve_change_without_new_writes()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10000, true)
            { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle)).State);
        fixture.Provider.UnconfirmedCoreRestore = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        fixture.Provider.UnconfirmedCoreRestore = false;
        fixture.Provider.SetExternalVfOffset(17, 12345);
        int[] external = fixture.Provider.Offsets();
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        CollectionAssert.AreEqual(external, fixture.Provider.Offsets());
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.IsTrue((await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Unresolved.Length > 0);
    }

    [TestMethod]
    public async Task End_keeps_pending_provider_recovery_even_when_gpu_targets_already_match()
    {
        using var fixture = new Fixture();
        for (int index = 0; index < 127; index++) fixture.Provider.SetExternalVfOffset(index, 0);
        var handle = await fixture.Begin(null);
        fixture.Provider.GpuRecoveryReady = false;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        var pending = (await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Unresolved.Single();
        Assert.AreEqual(pending.OperationId, fixture.Provider.RecoveryScopeId);
        Assert.AreEqual(0, fixture.Provider.WriteCount);
        fixture.Provider.GpuRecoveryReady = true;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(pending.OperationId, fixture.Provider.RecoveryScopeId);
        Assert.AreEqual(0, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task End_invokes_pending_gpu_recovery_under_original_parent_without_inventing_hardware_writes()
    {
        using var fixture = new Fixture();
        for (int index = 0; index < 127; index++) fixture.Provider.SetExternalVfOffset(index, 0);
        var handle = await fixture.Begin(null);
        fixture.Provider.GpuRecoveryReady = false;
        fixture.Provider.RecoverOnRequest = true;
        var result = await fixture.End(handle);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.AreEqual(1, fixture.Provider.GpuRecoveries);
        Assert.IsNotNull(fixture.Provider.RecoveryScopeId);
        Assert.AreEqual(0, fixture.Provider.WriteCount);
        using var missing = new Fixture();
        var pending = await missing.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await missing.Write(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10000, true)
            { ExpectedVfOffsetsKhz = missing.Provider.Offsets() }, pending)).State);
        string journalPath = Path.Combine(missing.Root, "control-execution.json");
        File.Delete(journalPath);
        int writes = missing.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await missing.End(pending)).State);
        Assert.AreEqual(writes, missing.Provider.WriteCount);
        Assert.AreEqual(0, missing.Provider.GpuRecoveries);
        Assert.IsFalse(File.Exists(journalPath), "Missing recovery evidence must not be silently recreated from cache.");
    }

    [TestMethod]
    public async Task Manual_gpu_failure_is_recovered_under_original_operation_before_exit_plan()
    {
        foreach (string kind in new[] { "core", "memory", "vf" })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(null);
            var id = Guid.NewGuid();
            HardwareCommand command = kind switch
            {
                "core" => new SetGpuCoreOffsetCommand(id, 0, 10000, true) { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() },
                "memory" => new SetGpuMemoryOffsetCommand(id, 0, 1000, true),
                _ => new SetGpuVfCurveCommand(id, fixture.Provider.Offsets(), Enumerable.Repeat(10000, 127).Select((value, index) => index == 0 ? 0 : value).ToArray(), true)
            };
            fixture.Provider.FailNextGpuWrite = true;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(command, handle)).State);
            fixture.Provider.RecoverOnRequest = true;
            var ended = await fixture.End(handle);
            Assert.AreEqual(CommandState.Applied, ended.State, kind + " recoveries=" + fixture.Provider.GpuRecoveries);
            Assert.AreEqual(id, fixture.Provider.RecoveredOperationId);
            CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
            Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
            Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
            Assert.AreEqual(0, (await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Manual_gpu_recovery_failure_retains_original_record_then_retry_preserves_prior_owned_values()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle)).State);
        var id = Guid.NewGuid();
        fixture.Provider.FailNextGpuWrite = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetGpuCoreOffsetCommand(id, 0, 10000, true)
            { ExpectedVfOffsetsKhz = fixture.Provider.Offsets() }, handle)).State);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(id, (await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Unresolved.Single().OperationId);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
        fixture.Provider.RecoverOnRequest = true;
        var ended = await fixture.End(handle);
        Assert.AreEqual(CommandState.Applied, ended.State, "recoveries=" + fixture.Provider.GpuRecoveries);
        Assert.AreEqual(1000, fixture.Provider.RecoveredMemoryBeforeExit);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
    }

    [TestMethod]
    public async Task Unresolved_compound_or_foreign_gpu_ownership_is_never_cleared_as_direct_gpu_failure()
    {
        foreach (bool compound in new[] { false, true })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(null);
            var id = Guid.NewGuid();
            fixture.Provider.FailNextGpuWrite = true;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetGpuMemoryOffsetCommand(id, 0, 1000, true), handle)).State);
            var current = (await fixture.Journal.ReadAsync(CancellationToken.None)).Current!;
            if (!compound) current = current with { Fields = current.Fields.ToDictionary(pair => pair.Key, pair => pair.Value with { ClientSessionId = Guid.NewGuid() }) };
            await fixture.Journal.SaveAsync(current, new("board", "bios", "cpu", "gpu"), id, true, CancellationToken.None,
                fixture.Owner.UserSid, compound ? nameof(ApplyControlRequestCommand) : nameof(SetGpuMemoryOffsetCommand));
            fixture.Provider.RecoverOnRequest = true;
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
            Assert.AreEqual(0, fixture.Provider.GpuRecoveries);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(id, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        }
    }

    [TestMethod]
    public async Task Confirmed_mode_rollback_does_not_leave_unknown_mode_or_block_exit()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        fixture.Provider.RollbackNextMode = true;
        var result = await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), handle);
        Assert.AreEqual(CommandState.RolledBack, result.State);
        var saved = (await fixture.Journal.ReadAsync(CancellationToken.None))!;
        Assert.AreEqual(0, saved.Unresolved.Length);
        Assert.AreEqual(ApplyState.Failed, saved.Current!.NativeModeApplyState);
        Assert.AreEqual(PerformanceMode.Turbo, saved.Current.NativeMode);
        Assert.IsNull(saved.Current.NativeModeOwnerClientSessionId);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        using var unknown = new Fixture();
        var unknownHandle = await unknown.Begin(null);
        unknown.Provider.RollbackNextMode = true;
        unknown.Provider.RollbackEvidenceAvailable = false;
        Assert.AreEqual(CommandState.RolledBack, (await unknown.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), unknownHandle)).State);
        Assert.AreEqual(1, (await unknown.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        int writes = unknown.Provider.WriteCount;
        Assert.AreEqual(CommandState.RecoveryRequired, (await unknown.End(unknownHandle)).State);
        Assert.AreEqual(writes, unknown.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Unconfirmed_direct_mode_resolves_original_record_after_matching_fresh_state_then_exits()
    {
        foreach (bool applied in new[] { false, true })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(null);
            fixture.Provider.UnconfirmedNextMode = true;
            fixture.Provider.ModeFailureApplies = applied;
            var id = Guid.NewGuid();
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetPerformanceModeCommand(id, PerformanceMode.Balanced), handle)).State);
            Assert.AreEqual(id, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
            Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
            Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
            Assert.AreEqual(0, fixture.Provider.GpuRecoveries);
        }
        using var external = new Fixture();
        var externalHandle = await external.Begin(null);
        external.Provider.SetExternalMode(PerformanceMode.Quiet);
        external.Provider.UnconfirmedNextMode = true;
        external.Provider.ModeFailureApplies = false;
        Assert.AreEqual(CommandState.RecoveryRequired, (await external.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), externalHandle)).State);
        Assert.AreEqual(CommandState.Applied, (await external.End(externalHandle)).State);
        Assert.AreEqual(PerformanceMode.Quiet, external.Provider.Mode, "An unchanged pre-command external mode must not be claimed by this session.");
    }

    [TestMethod]
    public async Task Unconfirmed_mode_keeps_external_mode_parameter_changes_and_stale_or_unstable_reads()
    {
        foreach (string change in new[] { "mode", "cpu", "gpu", "light", "stale", "unstable", "compound", "unknown" })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(null);
            var id = Guid.NewGuid();
            fixture.Provider.UnconfirmedNextMode = true;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetPerformanceModeCommand(id, PerformanceMode.Balanced), handle)).State);
            if (change == "mode") fixture.Provider.SetExternalMode(PerformanceMode.Quiet);
            if (change == "cpu") fixture.Provider.SetExternalAcFrequency(4500);
            if (change == "gpu") fixture.Provider.SetExternalVfOffset(17, 12345);
            if (change == "light") await fixture.Provider.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 1 }), CancellationToken.None);
            if (change == "stale") fixture.Provider.ModeEvidenceAge = TimeSpan.FromMinutes(1);
            if (change == "unknown") fixture.Provider.NativeEvidenceAvailable = false;
            if (change == "unstable") fixture.Provider.ChangeModeOnSecondEvidence = true;
            if (change == "compound")
                await fixture.Journal.SaveAsync((await fixture.Journal.ReadAsync(CancellationToken.None)).Current!, new("board", "bios", "cpu", "gpu"),
                    id, true, CancellationToken.None, fixture.Owner.UserSid, nameof(ApplyControlRequestCommand));
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State, change);
            Assert.AreEqual(writes, fixture.Provider.WriteCount, change);
            Assert.AreEqual(id, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
        }
    }

    [TestMethod]
    public async Task Unconfirmed_mode_recovery_preserves_prior_cpu_gpu_and_light_ownership_until_exit()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 5 }), handle)).State);
        fixture.Provider.UnconfirmedNextMode = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(OriginalFrequency, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    [TestMethod]
    public async Task Journal_persists_restart_ownership_scopes_with_original_session_capture()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        string capturePath = Path.Combine(fixture.Root, "client-control-session.json");
        string capture = await File.ReadAllTextAsync(capturePath);
        int checkpoints = 0;
        fixture.Provider.BeforeWrite = async command =>
        {
            var pending = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            var checkpoint = JsonSerializer.SerializeToElement(pending, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.AreEqual(fixture.Runtime.SessionId, checkpoint.GetProperty("recoveryScope").GetProperty("serviceSessionId").GetGuid());
            Assert.IsTrue(pending.Unresolved.Any(item => item.OperationId == command.OperationId));
            if (command is SetCpuTuningCommand)
                Assert.AreEqual(ApplyState.Unknown, pending.Current!.Fields["performance.acMaxFrequencyMhz"].WriteState);
            checkpoints++;
        };
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), handle);
        await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle);
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsTrue(json.TryGetProperty("recoveryScope", out var scope), "Restart recovery requires durable field, mode and fan provenance.");
        Assert.AreEqual(fixture.Runtime.SessionId, scope.GetProperty("serviceSessionId").GetGuid());
        var field = scope.GetProperty("fields").GetProperty("performance.acMaxFrequencyMhz");
        Assert.AreEqual(fixture.Owner.UserSid, field.GetProperty("ownerSid").GetString());
        Assert.AreEqual(handle.HardwareEpoch, field.GetProperty("hardwareEpoch").GetInt64());
        Assert.AreEqual(Fixture.Scheme, field.GetProperty("windowsPowerSchemeId").GetGuid());
        Assert.IsTrue(field.GetProperty("generation").GetInt64() > 0);
        Assert.AreEqual("balanced", scope.GetProperty("nativeModeTarget").GetString());
        Assert.AreEqual(handle.HardwareEpoch, scope.GetProperty("nativeModeEpoch").GetInt64());
        Assert.AreEqual(handle.SessionId, scope.GetProperty("fanOwnerClientSessionId").GetGuid());
        Assert.AreEqual(saved.Current!.Fields.Count, scope.GetProperty("fields").EnumerateObject().Count());
        Assert.AreEqual(capture, await File.ReadAllTextAsync(capturePath));
        Assert.AreEqual(3, checkpoints, "Every simulated physical dispatch must follow the original parent checkpoint.");
    }

    [TestMethod]
    public async Task Journal_retains_restart_scopes_on_failed_exit_and_clears_them_only_after_verified_exit()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle);
        fixture.Provider.FailMemoryRestore = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsTrue(json.TryGetProperty("recoveryScope", out var scope));
        Assert.AreEqual(saved.Current!.Fields.Count, scope.GetProperty("fields").EnumerateObject().Count());
        Assert.AreEqual(handle.SessionId, scope.GetProperty("fanOwnerClientSessionId").GetGuid());
        fixture.Provider.FailMemoryRestore = false;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        json = JsonSerializer.SerializeToElement(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        scope = json.GetProperty("recoveryScope");
        Assert.AreEqual(0, scope.GetProperty("fields").EnumerateObject().Count());
        Assert.AreEqual(JsonValueKind.Null, scope.GetProperty("fanOwnerClientSessionId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, scope.GetProperty("nativeModeTarget").ValueKind);
        Assert.AreEqual(0, scope.GetProperty("nativeModeEpoch").GetInt64());
        Assert.AreEqual(0, saved.Unresolved.Length);
    }

    [TestMethod]
    public async Task Invalid_restart_ownership_is_rejected_without_replacing_original_journal()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
        string path = Path.Combine(fixture.Root, "control-execution.json");
        string original = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(original);
        Assert.IsTrue(document.RootElement.TryGetProperty("recoveryScope", out _));
        foreach (string defect in new[] { "service", "missingField", "fieldEpoch" })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            var scope = json["recoveryScope"]!;
            if (defect == "service") scope["serviceSessionId"] = Guid.Empty;
            else if (defect == "missingField") scope["fields"]!.AsObject().Remove("performance.acMaxFrequencyMhz");
            else scope["fields"]!["performance.acMaxFrequencyMhz"]!["hardwareEpoch"] = 0;
            string corrupt = json.ToJsonString();
            await File.WriteAllTextAsync(path, corrupt);
            await Assert.ThrowsAsync<InvalidDataException>(() => new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None));
            Assert.AreEqual(corrupt, await File.ReadAllTextAsync(path));
        }
    }

    [TestMethod]
    public async Task End_requires_terminal_fan_checkpoint_and_next_session_preserves_exact_archive_before_replacing_scope()
    {
        using var fixture = new Fixture();
        fixture.Provider.FirmwareStrongCooling = true;
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        var identity = (await fixture.Journal.ReadAsync(CancellationToken.None)).Identity!;
        var scope = new FanEcRecoveryScope(fixture.Runtime.SessionId, handle.HardwareEpoch, handle.SessionId, fixture.Owner.UserSid!, identity);
        var baseline = new Dictionary<ushort, byte> { [0x1060] = 1, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 };
        var pending = new FanEcRecoveryCheckpoint(new(baseline), new(baseline) { [0x1060] = 0x81, [0xC83C] = 68, [0xC83D] = 68 }, new(), false);
        await fixture.Journal.RecordFanEcCheckpointAsync(scope, pending, CancellationToken.None);
        await fixture.Journal.RecordFanFirmwareAsync(scope, 1, 0, null, false, CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.CpuTarget);
        Assert.AreEqual((byte)0, fixture.Provider.Fan.GpuTarget);
        var released = new FanEcRecoveryCheckpoint(new(baseline), new(baseline), new(), true);
        await fixture.Journal.RecordFanEcCheckpointAsync(scope, released, CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        await fixture.Journal.RecordFanFirmwareAsync(scope, 1, 1, null, true, CancellationToken.None);
        await fixture.Journal.RecordFanFirmwareAsync(scope, 1, 1, 1, true, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        var nextOwner = fixture.Owner with { ProcessId = 43, ProcessCreationFileTimeUtc = fixture.Owner.ProcessCreationFileTimeUtc + 1 };
        var next = await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), nextOwner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, next.State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(next.ClientSession!.SessionId, null), nextOwner, CancellationToken.None)).State);
        string archive = Path.Combine(fixture.Root, "completed-client-sessions", handle.SessionId.ToString("N") + ".json");
        string original = await File.ReadAllTextAsync(archive);
        var command = new SetStrongCoolingCommand(Guid.NewGuid(), true) { ClientSessionId = next.ClientSession.SessionId };
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ExecuteAsync(command, CancellationToken.None,
            nextOwner.ConnectionId, nextOwner.UserSid, nextOwner)).State);
        foreach (string defect in new[] { "release", "missing", "session", "device" })
        {
            var broken = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            if (defect == "release") broken["fanEcRecovery"]!["checkpoint"]!["released"] = false;
            if (defect == "missing") broken["fanEcRecovery"] = null;
            if (defect == "session") broken["recovery"]!["sessionId"] = 1;
            if (defect == "device") broken["session"]!["handle"]!["deviceKey"] = new string('B', 64);
            await File.WriteAllTextAsync(archive, broken.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.CreateFanEcCheckpointWriterAsync(identity, CancellationToken.None));
            Assert.AreEqual(handle.SessionId, (await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery!.Scope.ClientSessionId);
        }
        await File.WriteAllTextAsync(archive, original);
        var writer = await fixture.Journal.CreateFanEcCheckpointWriterAsync(identity, CancellationToken.None);
        writer(released with { Released = false, Unchanged = true });
        Assert.AreEqual(next.ClientSession.SessionId, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanEcRecovery!.Scope.ClientSessionId);
        var nextScope = scope with { ClientSessionId = next.ClientSession.SessionId };
        var wrongFirmware = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        wrongFirmware["fanFirmwareRecovery"]!["observed"] = 0;
        await File.WriteAllTextAsync(archive, wrongFirmware.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Journal.RecordFanFirmwareAsync(nextScope, 1, 0, null, false, CancellationToken.None));
        Assert.AreEqual(handle.SessionId, (await fixture.Journal.ReadAsync(CancellationToken.None)).FanFirmwareRecovery!.Scope.ClientSessionId);
        await File.WriteAllTextAsync(archive, original);
        await fixture.Journal.RecordFanFirmwareAsync(nextScope, 1, 0, null, false, CancellationToken.None);
        await fixture.Journal.RecordFanFirmwareAsync(nextScope, 1, 0, 0, false, CancellationToken.None);
        Assert.AreEqual(next.ClientSession.SessionId, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).FanFirmwareRecovery!.Scope.ClientSessionId);
        Assert.AreEqual(original, await File.ReadAllTextAsync(archive));
    }

    [TestMethod]
    public async Task Ordinary_fan_plan_replaces_own_strong_cooling_target_and_exit_does_not_report_an_external_conflict()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetFanControlCommand(Guid.NewGuid(), new([new(30, 30), new(100, 100)])
            { Strategy = "Fixed", FixedRpm = 4000 }, true), handle)).State);
        Assert.AreEqual(false, fixture.Provider.Controls.StrongCooling);
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.IsFalse(saved.Current!.Fields["fan.strongCooling"].LastWritten!.Value!.Value.GetBoolean());
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Current!.Fields.Count);
    }

    [TestMethod]
    public async Task Compiled_manual_and_automatic_fan_presets_keep_parent_and_session_authority_through_exit()
    {
        foreach (bool automatic in new[] { false, true })
        {
            using var fixture = new Fixture();
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var store = new AdaptiveAutomationStateStore();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
            typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store, new Jiaolong.Service.Storage.AtomicJsonStore(fixture.Root, "adaptive-test.json"));
            typeof(AdaptiveAutomationStateStore).GetField("configurationOwnerSid", flags)!.SetValue(store, fixture.Owner.UserSid);
            typeof(AdaptiveAutomationStateStore).GetField("configuration", flags)!.SetValue(store,
                new AdaptiveAutomationConfiguration(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
                    new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []), new(key, key, key, key, key), []));
            var bridge = new AdaptiveAutomationHardwareProvider(fixture.Provider, store);
            var runtime = new HomeServiceRuntime(bridge, journal: fixture.Journal, requireClientSessions: true);
            var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            var handle = begun.ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, null), fixture.Owner, CancellationToken.None)).State);
            var preset = FanPreset(key, false);
            Assert.IsTrue(PresetValidation.Validate(preset).IsValid, string.Join(";", PresetValidation.Validate(preset).Errors));
            var publication = new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]);
            var published = await runtime.ExecuteAsync(publication with { ClientSessionId = automatic ? null : handle.SessionId }, CancellationToken.None,
                fixture.Owner.ConnectionId, automatic ? HomeServiceRuntime.AutomaticCatalogOwner(fixture.Owner.UserSid) : fixture.Owner.UserSid,
                automatic ? null : fixture.Owner);
            Assert.AreEqual(CommandState.Applied, published.State);
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true)
                { ClientSessionId = handle.SessionId }, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner)).State);
            if (automatic)
            {
                var configuration = (AdaptiveAutomationConfiguration)typeof(AdaptiveAutomationStateStore).GetField("configuration", flags)!.GetValue(store)!;
                typeof(AdaptiveAutomationStateStore).GetField("configuration", flags)!.SetValue(store, configuration with { Enabled = true });
                typeof(AdaptiveAutomationStateStore).GetField("manualOverrideUntilUtc", flags)!.SetValue(store, null);
            }
            var id = Guid.NewGuid();
            int admittedFanWrites = 0;
            fixture.Provider.BeforeWrite = async command =>
            {
                if (command is not (SetStrongCoolingCommand or SetFanControlCommand)) return;
                var captured = await fixture.Journal.ValidateFanCaptureAsync((await fixture.Journal.ReadAsync(CancellationToken.None)).Identity!, CancellationToken.None);
                Assert.AreEqual(handle.SessionId, captured.Handle.SessionId);
                var pending = await fixture.Journal.ReadAsync(CancellationToken.None);
                Assert.IsTrue(pending.Unresolved.Any(item => item.OperationId == id));
                Assert.IsTrue(pending.Current!.Fields.Where(pair => pair.Key.StartsWith("fan.")).All(pair =>
                    pair.Value.ClientSessionId == handle.SessionId && pair.Value.OwnerOperationId == id));
                await fixture.Journal.CreateFanEcCheckpointWriterAsync(captured.Identity, CancellationToken.None);
                admittedFanWrites++;
            };
            var request = new ApplyControlRequestCommand(id, new(id, 0, automatic ? ControlOrigin.Adaptive : ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Turbo, [new(ControlPageId.Fan, true, key, 1)])) { ClientSessionId = handle.SessionId };
            var applied = automatic ? await bridge.ExecuteAutomaticCommandAsync(runtime, request, CancellationToken.None)
                : await runtime.ExecuteAsync(request, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
            Assert.AreEqual(CommandState.Applied, applied.State, applied.Error?.Code.ToString());
            Assert.AreEqual(1, admittedFanWrites);
            Assert.AreEqual(4000, fixture.Provider.Controls.ActiveFanControlPlan!.FixedRpm);
            Assert.AreEqual(false, fixture.Provider.Controls.StrongCooling);
            Assert.AreEqual(CommandState.Applied, (await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId, fixture.Owner, CancellationToken.None)).State);
            Assert.IsNull(fixture.Provider.Controls.ActiveFanControlPlan);
            Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Current!.Fields.Count);
        }
    }

    [TestMethod]
    public async Task Conflicting_strong_cooling_and_fan_plan_are_rejected_before_any_mode_or_ec_write()
    {
        foreach (bool alreadyEnabled in new[] { false, true })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(null);
            if (alreadyEnabled) Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
            int writes = fixture.Provider.WriteCount;
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var preset = FanPreset(key, true);
            Assert.IsTrue(PresetValidation.Validate(preset).IsValid, string.Join(";", PresetValidation.Validate(preset).Errors));
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), handle)).State);
            var id = Guid.NewGuid();
            var result = await fixture.Write(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Quiet, [new(ControlPageId.Fan, true, key, 1)])), handle);
            Assert.AreEqual(CommandState.Rejected, result.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        }
    }

    private static PagePresetEnvelope FanPreset(PresetKey key, bool strongCooling) =>
        new(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Fan, key, "fan", JsonSerializer.SerializeToElement(new
        { profile = 0, isShared = true, cpu = new[] { new { temperature = 30, targetPercent = 30 }, new { temperature = 100, targetPercent = 100 } },
          gpu = new[] { new { temperature = 30, targetPercent = 30 }, new { temperature = 100, targetPercent = 100 } }, shared = new[] { new { temperature = 30, targetPercent = 30 }, new { temperature = 100, targetPercent = 100 } },
          strategy = "Fixed", fixedRpm = 4000, strongCooling }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["strongCooling"] = strongCooling ? SettingRequest.Set(true) : SettingRequest.Keep,
            ["strategy"] = SettingRequest.Set("Fixed"), ["fixedRpm"] = SettingRequest.Set(4000),
            ["shared"] = SettingRequest.Set(new[] { new { temperature = 30, targetPercent = 30 }, new { temperature = 100, targetPercent = 100 } }) } };

    private sealed class Fixture : IDisposable
    {
        public static readonly Guid Scheme = Guid.Parse("ab4a14c8-9838-4f42-8b18-000000000001");
        public static readonly Guid OtherScheme = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly KeyboardLightingPlan OriginalLight = new("Fixed", 100)
            { BrightnessLevel = 3, Red = 204, Green = 255, Blue = 0, LogoEnabled = true };
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "JiaolongClientSessionExitRecovery", Guid.NewGuid().ToString("N"));
        public Provider Provider { get; }
        public HomeServiceRuntime Runtime { get; }
        public ControlExecutionJournal Journal { get; }
        public ClientIdentity Owner { get; } = ClientIdentity.LocalAdministrator("S-1-5-21-1", 1) with
        { ProcessId = 42, ProcessCreationFileTimeUtc = DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc(), ConnectionId = Guid.NewGuid() };

        public Fixture(int curve = OriginalCurve, string? deviceKey = null,
            Func<CancellationToken, Task<DateTimeOffset?>>? readBootTime = null,
            Func<CancellationToken, Task<PowerContinuityEvidence?>>? readPowerContinuity = null,
            ClientIdentity? owner = null)
        {
            if (owner is not null) Owner = owner;
            Provider = new(curve, deviceKey ?? new string('A', 64));
            var journal = Journal = new ControlExecutionJournal(Root);
            BindGpuJournal(journal);
            BindModeJournal(journal);
            Runtime = new(Provider, journal: journal, requireClientSessions: true,
                readBootTime: readBootTime, readPowerContinuity: readPowerContinuity);
        }

        public void BindGpuJournal(ControlExecutionJournal journal)
        {
            Provider.ReadRecoveryScope = () => journal.CurrentGpuOperation?.OperationId;
            Provider.RecordFailedGpu = async (command, before) =>
            {
                var scope = journal.CurrentGpuOperation!;
                var baseline = new GpuRecoveryBaseline(DateTimeOffset.UtcNow, scope.SessionId, scope.HardwareEpoch, scope.OwnerSid,
                    new("board", "bios", "cpu", "gpu"), new(0x28E010DE, 1, 1, 1, 1, 1), before.Nodes.Select(node => node.OffsetKhz).ToArray(),
                    new(before.MemoryOffsetKhz!.Value, -1000000, 3000000, before.CoreOffsetKhz!.Value, 0, 1, 2));
                var target = Provider.Gpu;
                var attempt = new GpuRecoveryAttempt(command.OperationId, command is SetGpuCoreOffsetCommand ? "core" :
                    command is SetGpuMemoryOffsetCommand ? "memory" : "vf", baseline, target.Nodes.Select(node => node.OffsetKhz).ToArray(),
                    baseline.Pstate with { CoreOffsetKhz = target.CoreOffsetKhz!.Value, MemoryOffsetKhz = target.MemoryOffsetKhz!.Value });
                await journal.RecordGpuRecoveryAsync(scope.OperationId, baseline, CancellationToken.None, attempt);
                Provider.PendingGpuBefore = before;
                Provider.PendingGpuTarget = target;
            };
        }

        public void BindModeJournal(ControlExecutionJournal journal)
        {
            Provider.ReadNativeModeScope = () => journal.CurrentNativeModeOperation;
            Provider.RecordModeWrite = (command, before, observed) => journal.RecordNativeModeAsync(command.OperationId, new("board", "bios", "cpu", "gpu"),
                before, command.Mode, observed, CancellationToken.None);
        }

        public async Task<ClientControlSessionHandle> Begin(int? configureCurve)
        {
            var result = await Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State);
            var handle = result.ClientSession!;
            var configuration = configureCurve is int curve
                ? new ExitDeviceDefaults(1, handle.DeviceKey, curve) : null;
            Assert.AreEqual(CommandState.Applied, (await Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                new(handle.SessionId, configuration), Owner, CancellationToken.None)).State);
            return handle;
        }

        public Task<CommandResult> Write(HardwareCommand command, ClientControlSessionHandle handle) => Runtime.ExecuteAsync(
            command with { ClientSessionId = handle.SessionId }, CancellationToken.None, Owner.ConnectionId, Owner.UserSid, Owner);

        public Task<CommandResult> End(ClientControlSessionHandle handle) => Runtime.EndClientControlSessionAsync(
            Guid.NewGuid(), handle.SessionId, Owner, CancellationToken.None);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Provider : IHomeHardwareProvider
    {
        public Func<HardwareCommand, Task>? BeforeWrite { get; set; }
        public Func<CancellationToken, Task>? BeforeStopLightingDynamics { get; set; }
        public int LightingDynamicsStops { get; private set; }
        public async Task StopClientLightingDynamicsAsync(CancellationToken token)
        {
            LightingDynamicsStops++;
            if (BeforeStopLightingDynamics is { } before) await before(token);
        }
        public Func<HomeControlState, HomeControlState>? ControlStateTransform { get; set; }
        public Func<Guid, FanEcRecoveryScope, CancellationToken, Task<CommandResult>>? FanRecovery { get; set; }
        public Task<CommandResult> RecoverSessionFanAsync(Guid id, FanEcRecoveryScope scope, CancellationToken token) =>
            FanRecovery?.Invoke(id, scope, token) ?? Task.FromResult(Result(id, CommandState.Rejected, HardwareMutation.NoMutation));
        public void CompleteFanRecovery(bool firmware)
        { fanControl = fanCpuTarget = fanGpuTarget = 0; strongCooling = false; fanPlan = null; FirmwareStrongCooling = firmware; savedFirmwareCooling = null; FanReleases++; }
        public HardwareIdentity Identity { get; set; } = new("board", "bios", "cpu", "gpu");
        private GpuVfNode[] nodes;
        private int coreOffset;
        private int memoryOffset;
        private sealed class SchemeState(int ac, int dc, int acBoost, int dcBoost, int acParking, int dcParking)
        {
            public int Ac = ac, Dc = dc, AcBoost = acBoost, DcBoost = dcBoost, AcParking = acParking, DcParking = dcParking;
        }
        private readonly Dictionary<Guid, SchemeState> schemes = new()
        {
            [Fixture.Scheme] = new(OriginalFrequency, OriginalFrequency, 2, 2, 50, 50),
            [Fixture.OtherScheme] = new(3900, 3300, 3, 4, 75, 25)
        };
        private Guid activeScheme = Fixture.Scheme;
        private Dictionary<int, int> curves;
        public string DeviceKey { get; set; }
        private PerformanceMode mode = PerformanceMode.Turbo;
        private KeyboardLightingPlan light = Fixture.OriginalLight;
        private byte fanControl;
        private byte fanCpuTarget;
        private byte fanGpuTarget;
        private bool strongCooling;
        private FanControlPlan? fanPlan;
        private bool? savedFirmwareCooling;
        private int pboScalar = 1;
        public bool DirectLimitsEnabled { get; set; }
        public double DirectPpt { get; set; } = 120.125;
        public int DirectTemperature { get; set; } = 99;
        public int DirectTdc { get; set; } = 108000;
        public int DirectEdc { get; set; } = 160000;
        public string? FailDirectRestoreOnce { get; set; }
        public string? UnverifiedDirectRestoreOnce { get; set; }
        public bool DirectCapabilitiesVerified { get; set; } = true;
        public Task<CommandResult> RestoreCpuScalarAsync(Guid id, string field, SettingRequest baseline, SettingRequest expected, CancellationToken token)
        {
            var current = ControlPlanCompiler.Read(ControlPageId.Performance, BuildControls()).GetValueOrDefault(field);
            if (current is null || !ControlPlanCompiler.Same(current, expected))
                return Task.FromResult(Result(id, CommandState.Rejected, HardwareMutation.NoMutation));
            if (FailDirectRestoreOnce == field)
            {
                FailDirectRestoreOnce = null;
                return Task.FromResult(Result(id, CommandState.RecoveryRequired, HardwareMutation.NoMutation));
            }
            if (field == "pptWatts") DirectPpt = baseline.Value!.Value.GetDouble();
            else if (field == "mp1TemperatureC") DirectTemperature = baseline.Value!.Value.GetInt32();
            else if (field == "tdcCurrentMilliamps") DirectTdc = baseline.Value!.Value.GetInt32();
            else if (field == "edcCurrentMilliamps") DirectEdc = baseline.Value!.Value.GetInt32();
            else return Task.FromResult(Result(id, CommandState.Rejected, HardwareMutation.NoMutation));
            WriteCount++;
            if (UnverifiedDirectRestoreOnce == field)
            {
                UnverifiedDirectRestoreOnce = null;
                return Task.FromResult(Result(id, CommandState.RecoveryRequired, HardwareMutation.Unknown));
            }
            return Task.FromResult(Result(id, CommandState.Applied, HardwareMutation.Changed));
        }

        public Provider(int curve, string deviceKey)
        {
            DeviceKey = deviceKey;
            curves = Enumerable.Range(0, 8).ToDictionary(core => core, _ => curve);
            nodes = Enumerable.Range(0, 127).Select(index => new GpuVfNode(600 + 5 * index, 1200 + 15 * index, index == 60 ? 75000 : 0)).ToArray();
            OriginalOffsets = Offsets();
        }

        public int[] OriginalOffsets { get; }
        public int WriteCount { get; private set; }
        public int TelemetryReads { get; private set; }
        public bool FailMemoryRestore { get; set; }
        public bool FailDcBoostRestore { get; set; }
        public bool RollbackNextCpuWrite { get; set; }
        public Action? AfterCpuRollback { get; set; }
        public bool FailNextWindowsSchemeRead { get; set; }
        public int? InterruptCpuBatchAfterPlans { get; set; }
        public List<WindowsCpuSetting> WindowsRestorationWrites { get; } = [];
        public bool RejectCpuWrites { get; set; }
        public bool FanReadbackAvailable { get; set; } = true;
        public int FanReleases { get; private set; }
        public bool FirmwareStrongCooling { get; set; }
        public bool StrongCoolingReadbackAvailable { get; set; } = true;
        public bool FailFanRestore { get; set; }
        public bool FailNextFanWrite { get; set; }
        public bool FailNextLightingWrite { get; set; }
        public bool LightingFieldsReadable { get; set; } = true;
        public Func<KeyboardLightingPlan, KeyboardLightingPlan>? LightingWriteFault { get; set; }
        public bool FailCoreRestore { get; set; }
        public bool UnconfirmedCoreRestore { get; set; }
        public bool GpuRecoveryReady { get; set; } = true;
        public bool RecoverOnRequest { get; set; }
        public bool FailNextGpuWrite { get; set; }
        public bool RollbackNextMode { get; set; }
        public bool RollbackEvidenceAvailable { get; set; } = true;
        public bool UnconfirmedNextMode { get; set; }
        public bool ModeFailureApplies { get; set; } = true;
        public Func<SetPerformanceModeCommand, PerformanceMode, PerformanceMode?, Task>? RecordModeWrite { get; set; }
        public bool JournalEveryModeStep { get; set; }
        public Action? ModeWriteEffects { get; set; }
        public bool RejectCpuRestores { get; set; }
        public bool FailNextCpuWrite { get; set; }
        public bool FailCpuRestoreAfterHalf { get; set; }
        public bool NativeEvidenceAvailable { get; set; } = true;
        public bool ProductionModeAcknowledgment { get; set; }
        public string? ModeAcknowledgmentFault { get; set; }
        public TimeSpan ModeEvidenceAge { get; set; }
        public bool ChangeModeOnSecondEvidence { get; set; }
        public int ChangeModeOnEvidenceRead { get; set; }
        private int nativeEvidenceReads;
        public Task<PerformanceModeEvidence?> ReadNativeModeEvidenceAsync(CancellationToken token)
        {
            int observation = ++nativeEvidenceReads;
            if (observation == 2 && ChangeModeOnSecondEvidence || observation == ChangeModeOnEvidenceRead) mode = PerformanceMode.Quiet;
            byte raw = mode == PerformanceMode.Turbo ? (byte)1 : mode == PerformanceMode.Quiet ? (byte)2 : (byte)0;
            return Task.FromResult<PerformanceModeEvidence?>(NativeEvidenceAvailable ? new(DateTimeOffset.UtcNow - ModeEvidenceAge,
                "fake", 4, raw.ToString("X2"), raw, mode, null, "RequestStateUnverified") : null);
        }
        public void SetExternalMode(PerformanceMode value) => mode = value;
        public void SetExternalLight(KeyboardLightingPlan value) => light = value;
        public Func<ControlExecutionJournal.NativeModeOperation?>? ReadNativeModeScope { get; set; }
        public List<ControlExecutionJournal.NativeModeOperation> NativeModeScopes { get; } = [];
        public Func<HardwareCommand, GpuVfState, Task>? RecordFailedGpu { get; set; }
        public GpuVfState? PendingGpuBefore { get; set; }
        public GpuVfState? PendingGpuTarget { get; set; }
        public bool BlockGpuWhilePending { get; set; }
        public bool KeepKnownGpuIntermediate { get; set; }
        public bool JournalEveryGpuStep { get; set; }
        public bool FailGpuReconcileAfterRecoveryOnce { get; set; }
        public bool RejectGpuTargetVerification { get; set; }
        public int GpuTargetVerifications { get; private set; }
        public Task<bool> VerifyPendingGpuTargetAsync(Guid id, CancellationToken token)
        {
            Assert.AreEqual(id, ReadRecoveryScope?.Invoke());
            GpuTargetVerifications++;
            if (RejectGpuTargetVerification || PendingGpuTarget is not { } target || target.CoreOffsetKhz != coreOffset ||
                target.MemoryOffsetKhz != memoryOffset || !target.Nodes.Select(node => node.OffsetKhz).SequenceEqual(Offsets()))
                return Task.FromResult(false);
            GpuRecoveryReady = true;
            PendingGpuBefore = null;
            return Task.FromResult(true);
        }
        public Guid? RecoveredOperationId { get; private set; }
        public int? RecoveredMemoryBeforeExit { get; private set; }
        public int GpuRecoveries { get; private set; }
        public Task<CommandResult> RecoverPendingGpuAsync(Guid id, CancellationToken token)
        {
            Assert.AreEqual(id, ReadRecoveryScope?.Invoke());
            GpuRecoveries++;
            var mutation = HardwareMutation.NoMutation;
            if (RecoverOnRequest)
            {
                GpuRecoveryReady = true;
                if (PendingGpuBefore is { } before)
                {
                    bool changed = coreOffset != before.CoreOffsetKhz || memoryOffset != before.MemoryOffsetKhz ||
                        !Offsets().SequenceEqual(before.Nodes.Select(node => node.OffsetKhz));
                    nodes = before.Nodes.ToArray(); coreOffset = before.CoreOffsetKhz!.Value; memoryOffset = before.MemoryOffsetKhz!.Value;
                    PendingGpuBefore = null; RecoveredOperationId = id; RecoveredMemoryBeforeExit = memoryOffset;
                    if (changed) { WriteCount++; GpuRestorationWrites++; mutation = HardwareMutation.Changed; }
                }
                if (FailGpuReconcileAfterRecoveryOnce) { FailGpuReconcileAfterRecoveryOnce = false; FailGpuReconcileOnce = true; }
            }
            return Task.FromResult(Result(id, RecoverOnRequest ? CommandState.Applied : CommandState.Rejected, mutation));
        }
        public Guid? RecoveryScopeId { get; private set; }
        public int GpuRestorationWrites { get; private set; }
        public bool FailGpuReconcileOnce { get; set; }
        public Func<Guid?>? ReadRecoveryScope { get; set; }
        public Task<bool> ReconcileGpuRecoveryAsync(CancellationToken token)
        {
            RecoveryScopeId = ReadRecoveryScope?.Invoke();
            if (FailGpuReconcileOnce) { FailGpuReconcileOnce = false; return Task.FromResult(false); }
            return Task.FromResult(GpuRecoveryReady);
        }
        public bool PreviewActive { get; set; }
        public KeyboardLightingPlan Light => light;
        public PerformanceMode Mode => mode;
        public HomeControlState Controls => BuildControls();
        public CpuTuningState Cpu => BuildCpu();
        public WindowsPowerSettingsSnapshot ReadScheme(Guid scheme)
        {
            var value = schemes[scheme];
            return new(scheme, value.Ac, value.Dc, value.AcBoost, value.DcBoost)
                { AcMinActiveCoresPercent = value.AcParking, DcMinActiveCoresPercent = value.DcParking };
        }
        public void SetActiveScheme(Guid scheme) => activeScheme = scheme;
        public void ExternalSchemeIndex(Guid scheme, WindowsCpuSetting setting, int value) => SetSchemeValue(schemes[scheme], setting, value);
        public GpuVfState Gpu => new(nodes.ToArray(), -200000, 200000, null)
            { CoreOffsetKhz = coreOffset, MemoryOffsetKhz = memoryOffset, MemoryMinimumOffsetKhz = -1000000,
                MemoryMaximumOffsetKhz = 3000000, CoreMinimumOffsetKhz = -1000000, CoreMaximumOffsetKhz = 1000000 };
        public FanEcControlState Fan => new(DateTimeOffset.UtcNow, 0, fanControl, fanCpuTarget, fanGpuTarget, 0);

        public int DiagnoseReads { get; private set; }
        public int RecoveryKeyReads { get; private set; }
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token)
        { DiagnoseReads++; return Task.FromResult(BuildHardwareState()); }
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public HardwareSnapshot? TelemetryOverride { get; set; }
        public DeviceSupportState CapabilitySupportState { get; set; } = DeviceSupportState.Diagnosing;
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token)
        { TelemetryReads++; return Task.FromResult(TelemetryOverride ?? new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", null, null, null, null)); }
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(BuildControls());
        public Func<HomeControlState, HomeControlState>? ReadBaselineOverride { get; set; }
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false)
        { var controls = BuildControls(); return Task.FromResult(ReadBaselineOverride?.Invoke(controls) ?? controls); }
        public Task<WindowsPowerSettingsSnapshot?> ReadWindowsPowerSchemeAsync(Guid scheme, CancellationToken token)
        {
            if (FailNextWindowsSchemeRead) { FailNextWindowsSchemeRead = false; throw new IOException("simulatedWindowsRollbackReadFailure"); }
            return Task.FromResult<WindowsPowerSettingsSnapshot?>(schemes.ContainsKey(scheme) ? ReadScheme(scheme) : null);
        }
        public Task<CommandResult> RestoreWindowsPowerSettingAsync(Guid id, Guid scheme, WindowsCpuSetting setting, int expected, int target, CancellationToken token)
        {
            var state = schemes[scheme];
            int current = GetSchemeValue(state, setting);
            if (current == target) return Task.FromResult(Result(id, CommandState.Applied, HardwareMutation.NoMutation));
            if (current != expected) return Task.FromResult(Result(id, CommandState.Rejected, HardwareMutation.NoMutation));
            if (FailDcBoostRestore && setting == WindowsCpuSetting.DcBoostMode)
                return Task.FromResult(Result(id, CommandState.RecoveryRequired, HardwareMutation.NoMutation));
            SetSchemeValue(state, setting, target);
            WindowsRestorationWrites.Add(setting);
            WriteCount++;
            return Task.FromResult(Result(id, CommandState.Applied, HardwareMutation.Changed));
        }
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token)
        { RecoveryKeyReads++; return Task.FromResult<string?>(DeviceKey); }
        public Task<PerformanceModeEvidence?> ReadModeEvidenceAsync(CancellationToken token) => Task.FromResult<PerformanceModeEvidence?>(null);
        public Task<CommandResult> RestoreCpuFieldAsync(Guid operationId, string field, CpuFieldRestorationValue baseline,
            CpuFieldRestorationValue expected, CancellationToken token)
        {
            if (RejectCpuRestores) return Task.FromResult(Result(operationId, CommandState.Rejected, HardwareMutation.NoMutation));
            if (!RestorationMatches(field, Capture(field), expected))
                return Task.FromResult(Result(operationId, CommandState.Rejected, HardwareMutation.NoMutation));
            if (field == "isBoostEnabled")
            {
                schemes[activeScheme].AcBoost = baseline.AcBoostMode!.Value;
                schemes[activeScheme].DcBoost = baseline.DcBoostMode!.Value;
            }
            else if (field == "negativeCurveOptimizer" && baseline.PerCoreCurveOptimizer is { Count: 8 } target)
            {
                if (FailCpuRestoreAfterHalf)
                {
                    FailCpuRestoreAfterHalf = false;
                    foreach (int core in target.Keys.Order().Take(4)) curves[core] = target[core];
                    WriteCount++;
                    return Task.FromResult(Result(operationId, CommandState.RecoveryRequired, HardwareMutation.Unknown));
                }
                curves = target.ToDictionary(pair => pair.Key, pair => pair.Value);
            }
            else return Task.FromResult(Result(operationId, CommandState.Rejected, HardwareMutation.NoMutation));
            WriteCount++;
            return Task.FromResult(Result(operationId, CommandState.Applied, HardwareMutation.Changed));
        }

        private static bool RestorationMatches(string field, CpuFieldRestorationValue current, CpuFieldRestorationValue expected) =>
            field == "isBoostEnabled"
                ? current.AcBoostMode == expected.AcBoostMode && current.DcBoostMode == expected.DcBoostMode
                : field == "negativeCurveOptimizer" && current.PerCoreCurveOptimizer is { } actual &&
                    expected.PerCoreCurveOptimizer is { } target && actual.Count == target.Count &&
                    actual.All(pair => target.TryGetValue(pair.Key, out int value) && value == pair.Value);
        private CpuFieldRestorationValue Capture(string field) => field == "isBoostEnabled"
            ? new(schemes[activeScheme].AcBoost, schemes[activeScheme].DcBoost, null) : new(null, null, new(curves));

        public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (RollbackNextMode && command is SetPerformanceModeCommand)
            {
                RollbackNextMode = false;
                return Result(command.OperationId, CommandState.RolledBack, HardwareMutation.Unknown) with
                { Error = ServiceError.Create(ErrorCode.ReadBackMismatch, command.OperationId, false),
                  ModeEvidence = RollbackEvidenceAvailable ? new(DateTimeOffset.UtcNow, "fake", 4, "01", 1, mode, null, "RequestStateUnverified") : null };
            }
            if (RejectCpuWrites && command is SetCpuTuningCommand or SetCpuTuningBatchCommand)
                return Result(command.OperationId, CommandState.Rejected, HardwareMutation.NoMutation);
            if (command is ReleaseFanControlCommand && FailFanRestore)
                return Result(command.OperationId, CommandState.Rejected, HardwareMutation.NoMutation);
            if (command is SetGpuCoreOffsetCommand { OffsetKhz: 0 } && FailCoreRestore)
                return Result(command.OperationId, CommandState.Rejected, HardwareMutation.NoMutation);
            if (command is SetGpuMemoryOffsetCommand { OffsetKhz: 0 } && FailMemoryRestore)
                return Result(command.OperationId, CommandState.Rejected, HardwareMutation.NoMutation);
            if (BlockGpuWhilePending && !GpuRecoveryReady && PendingGpuBefore is not null &&
                command is SetGpuCoreOffsetCommand or SetGpuMemoryOffsetCommand or SetGpuVfCurveCommand)
                return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.NoMutation);
            var gpuBefore = Gpu;
            var cpuBefore = Capture("negativeCurveOptimizer");
            var boostBefore = Capture("isBoostEnabled");
            int dcBefore = schemes[activeScheme].Dc;
            var schemesBefore = RollbackNextCpuWrite ? schemes.ToDictionary(pair => pair.Key, pair => new SchemeState(
                pair.Value.Ac, pair.Value.Dc, pair.Value.AcBoost, pair.Value.DcBoost, pair.Value.AcParking, pair.Value.DcParking)) : null;
            var activeBefore = activeScheme;
            if (BeforeWrite is not null) await BeforeWrite(command);
            WriteCount++;
            switch (command)
            {
                case SetCpuTuningBatchCommand batch:
                    for (int index = 0; index < batch.Plans.Length; index++)
                    {
                        ApplyCpu(batch.Plans[index]);
                        if (InterruptCpuBatchAfterPlans == index + 1)
                        {
                            InterruptCpuBatchAfterPlans = null;
                            return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.Unknown);
                        }
                    }
                    break;
                case SetCpuTuningCommand cpu: ApplyCpu(cpu.Plan); break;
                case SetGpuVfCurveCommand vf:
                    CollectionAssert.AreEqual(vf.ExpectedOffsetsKhz, Offsets());
                    if (vf.OffsetsKhz.Length != 127) throw new InvalidOperationException("vfNodeCountInvalid");
                    nodes = nodes.Select((node, index) => node with { OffsetKhz = vf.OffsetsKhz[index] }).ToArray();
                    break;
                case SetGpuCoreOffsetCommand core:
                    Assert.AreEqual(core.ExpectedOffsetKhz, coreOffset);
                    if (core.ExpectedVfOffsetsKhz is not null) CollectionAssert.AreEqual(core.ExpectedVfOffsetsKhz, Offsets());
                    int delta = core.OffsetKhz - coreOffset;
                    nodes = nodes.Select((node, index) => index == 0 ? node : node with { OffsetKhz = node.OffsetKhz + delta }).ToArray();
                    coreOffset = core.OffsetKhz;
                    if (core.OffsetKhz == 0 && UnconfirmedCoreRestore)
                        return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.Changed);
                    break;
                case SetGpuMemoryOffsetCommand memory:
                    Assert.AreEqual(memory.ExpectedOffsetKhz, memoryOffset);
                    memoryOffset = memory.OffsetKhz;
                    break;
                case SetKeyboardLightingCommand keyboard:
                    var frame = keyboard.Plan with { LogoEnabled = keyboard.Plan.LogoEnabled ?? light.LogoEnabled };
                    light = LightingWriteFault?.Invoke(frame) ?? frame; PreviewActive = keyboard.Preview; break;
                case SetLidLogoCommand logo: light = light with { LogoEnabled = logo.Enabled }; break;
                case SetQuickSettingCommand { Setting: QuickSettingKind.LidLogo } logo: light = light with { LogoEnabled = logo.Enabled }; break;
                case SetQuickSettingCommand { Setting: QuickSettingKind.StrongCooling } cooling: SetCooling(cooling.Enabled); break;
                case SetStrongCoolingCommand cooling: SetCooling(cooling.Enabled); break;
                case SetFanControlCommand fan:
                    savedFirmwareCooling ??= FirmwareStrongCooling;
                    FirmwareStrongCooling = strongCooling = false;
                    fanPlan = fan.Plan;
                    fanControl = 0x0A;
                    fanCpuTarget = fanGpuTarget = (byte)((fan.Plan.FixedRpm ?? 4000) / 100);
                    break;
                case SetPerformanceModeCommand performance:
                    var beforeMode = mode;
                    if (ReadNativeModeScope?.Invoke() is { } modeScope) NativeModeScopes.Add(modeScope);
                    if (UnconfirmedNextMode)
                    {
                        UnconfirmedNextMode = false;
                        await RecordModeWrite!(performance, mode, null);
                        if (ModeFailureApplies) mode = performance.Mode;
                        return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.Unknown);
                    }
                    if (JournalEveryModeStep) await RecordModeWrite!(performance, beforeMode, null);
                    mode = performance.Mode;
                    ModeWriteEffects?.Invoke();
                    if (JournalEveryModeStep) await RecordModeWrite!(performance, beforeMode, mode);
                    break;
                case ReleaseFanControlCommand:
                    fanPlan = null;
                    fanControl = fanCpuTarget = fanGpuTarget = 0; strongCooling = false;
                    if (savedFirmwareCooling is bool original) FirmwareStrongCooling = original;
                    savedFirmwareCooling = null; FanReleases++; break;
                default: throw new InvalidOperationException("Unexpected fake command: " + command.GetType().Name);
            }
            if (FailNextLightingWrite && command is SetKeyboardLightingCommand)
            {
                FailNextLightingWrite = false;
                return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.Unknown);
            }
            if (FailNextFanWrite && command is SetFanControlCommand)
            {
                FailNextFanWrite = false;
                return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.Unknown);
            }
            if (RollbackNextCpuWrite && command is SetCpuTuningCommand or SetCpuTuningBatchCommand)
            {
                RollbackNextCpuWrite = false;
                foreach (var pair in schemesBefore!) schemes[pair.Key] = pair.Value;
                activeScheme = activeBefore;
                curves = cpuBefore.PerCoreCurveOptimizer!.ToDictionary(pair => pair.Key, pair => pair.Value);
                AfterCpuRollback?.Invoke();
                return Result(command.OperationId, CommandState.RolledBack, HardwareMutation.Unknown) with
                    { Error = ServiceError.Create(ErrorCode.ReadBackMismatch, command.OperationId, false) };
            }
            if (FailNextCpuWrite && command is SetCpuTuningCommand interruptedCpu)
            {
                FailNextCpuWrite = false;
                if (interruptedCpu.Plan.NegativeCurveOptimizer is not null || interruptedCpu.Plan.Advanced?.PerCoreCurveOptimizer is not null)
                    foreach (int core in curves.Keys.Where(core => core % 2 == 0).ToArray()) curves[core] = cpuBefore.PerCoreCurveOptimizer![core];
                if (interruptedCpu.Plan.BoostEnabled is not null || interruptedCpu.Plan.BoostPolicy is not null) schemes[activeScheme].DcBoost = boostBefore.DcBoostMode!.Value;
                if (interruptedCpu.Plan.DcMaxFrequencyMhz is not null) schemes[activeScheme].Dc = dcBefore;
                return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.Unknown);
            }
            if (JournalEveryGpuStep && !FailNextGpuWrite && command is SetGpuCoreOffsetCommand or SetGpuMemoryOffsetCommand or SetGpuVfCurveCommand)
                await RecordFailedGpu!(command, gpuBefore);
            if (FailNextGpuWrite && command is SetGpuCoreOffsetCommand or SetGpuMemoryOffsetCommand or SetGpuVfCurveCommand)
            {
                FailNextGpuWrite = false;
                await RecordFailedGpu!(command, gpuBefore);
                if (KeepKnownGpuIntermediate)
                {
                    if (command is SetGpuMemoryOffsetCommand) memoryOffset = gpuBefore.MemoryOffsetKhz!.Value;
                    else nodes = nodes.Select((node, index) => index % 2 == 0 ? gpuBefore.Nodes[index] : node).ToArray();
                }
                GpuRecoveryReady = false;
                return Result(command.OperationId, CommandState.RecoveryRequired, HardwareMutation.Unknown);
            }
            byte modeRaw = mode == PerformanceMode.Turbo ? (byte)1 : mode == PerformanceMode.Quiet ? (byte)2 : (byte)0;
            return Result(command.OperationId, CommandState.Applied, HardwareMutation.Changed) with
            {
                VerifiedPerformanceMode = command is SetPerformanceModeCommand && !ProductionModeAcknowledgment ? mode : null,
                ModeEvidence = command is SetPerformanceModeCommand requested && ProductionModeAcknowledgment && ModeAcknowledgmentFault != "missing"
                    ? new PerformanceModeEvidence(DateTimeOffset.UtcNow, "fake", 4, modeRaw.ToString("X2"), modeRaw,
                        ModeAcknowledgmentFault == "parsed" ? PerformanceMode.Turbo : mode, null,
                        ModeAcknowledgmentFault == "level" ? "RequestStateUnverified" : "InterfaceAcknowledged")
                        { RequestedMode = ModeAcknowledgmentFault == "requested" ? PerformanceMode.Turbo : requested.Mode }
                    : null
            };
        }

        public void SetExternalAcFrequency(int value) => schemes[activeScheme].Ac = value;
        public void SetExternalCurve(int value) => curves = curves.Keys.ToDictionary(key => key, _ => value);
        public void SetExternalCoreCurve(int core, int value) => curves[core] = value;
        public void SetExternalBoost(int ac, int dc) { schemes[activeScheme].AcBoost = ac; schemes[activeScheme].DcBoost = dc; }
        private void SetCooling(bool enabled)
        {
            fanPlan = null;
            savedFirmwareCooling ??= FirmwareStrongCooling;
            FirmwareStrongCooling = false;
            strongCooling = enabled;
            fanControl = enabled ? (byte)0x0A : (byte)0;
            fanCpuTarget = fanGpuTarget = enabled ? (byte)68 : (byte)0;
        }
        public void SetExternalFan(byte control, byte cpu, byte gpu) { fanControl = control; fanCpuTarget = cpu; fanGpuTarget = gpu; }
        public void SetExternalVfOffset(int index, int value) => nodes[index] = nodes[index] with { OffsetKhz = value };
        public int[] Offsets() => nodes.Select(node => node.OffsetKhz).ToArray();
        private static int GetSchemeValue(SchemeState state, WindowsCpuSetting setting) => setting switch
        {
            WindowsCpuSetting.AcMaxFrequencyMhz => state.Ac, WindowsCpuSetting.DcMaxFrequencyMhz => state.Dc,
            WindowsCpuSetting.AcBoostMode => state.AcBoost, WindowsCpuSetting.DcBoostMode => state.DcBoost,
            WindowsCpuSetting.AcMinActiveCoresPercent => state.AcParking, WindowsCpuSetting.DcMinActiveCoresPercent => state.DcParking,
            _ => throw new ArgumentOutOfRangeException(nameof(setting))
        };
        private static void SetSchemeValue(SchemeState state, WindowsCpuSetting setting, int value)
        {
            switch (setting)
            {
                case WindowsCpuSetting.AcMaxFrequencyMhz: state.Ac = value; break;
                case WindowsCpuSetting.DcMaxFrequencyMhz: state.Dc = value; break;
                case WindowsCpuSetting.AcBoostMode: state.AcBoost = value; break;
                case WindowsCpuSetting.DcBoostMode: state.DcBoost = value; break;
                case WindowsCpuSetting.AcMinActiveCoresPercent: state.AcParking = value; break;
                case WindowsCpuSetting.DcMinActiveCoresPercent: state.DcParking = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(setting));
            }
        }
        private void ApplyCpu(CpuTuningPlan plan)
        {
            if (plan.Advanced?.PptWatts is double ppt) DirectPpt = ppt;
            if (plan.Advanced?.Mp1TemperatureC is int temperature) DirectTemperature = temperature;
            if (plan.Advanced?.TdcCurrentMilliamps is int current) DirectTdc = current;
            if (plan.Advanced?.EdcCurrentMilliamps is int edc) DirectEdc = edc;
            if (plan.WindowsPowerSchemeId is Guid scheme) activeScheme = scheme;
            var state = schemes[activeScheme];
            if (plan.AcMaxFrequencyMhz is int ac) state.Ac = ac;
            if (plan.DcMaxFrequencyMhz is int dc) state.Dc = dc;
            if (plan.AcMinActiveCoresPercent is int acParking) state.AcParking = acParking;
            if (plan.DcMinActiveCoresPercent is int dcParking) state.DcParking = dcParking;
            if ((plan.NegativeCurveOptimizer ?? plan.Advanced?.CurveOptimizerAll) is int all)
                curves = curves.Keys.ToDictionary(core => core, _ => all);
            if (plan.Advanced?.PerCoreCurveOptimizer is { } perCore)
                foreach (var pair in perCore) curves[pair.Key] = pair.Value;
            if (plan.Advanced?.PboScalar is int scalar) pboScalar = scalar;
            if (plan.BoostPolicy is { } policy) { state.AcBoost = policy.AcMode; state.DcBoost = policy.DcMode; }
            else if (plan.BoostEnabled is bool enabled)
            {
                state.AcBoost = WindowsPowerBoostMode.SelectTarget(state.AcBoost, enabled);
                state.DcBoost = WindowsPowerBoostMode.SelectTarget(state.DcBoost, enabled);
            }
        }
        private CpuTuningState BuildCpu() => new(DirectLimitsEnabled ? DirectTemperature : null, null, null, null, schemes[activeScheme].AcBoost != 0 && schemes[activeScheme].DcBoost != 0, 8, activeScheme,
            curves.Values.Distinct().Count() == 1 ? curves[0] : null, "hardwareReadback")
        {
            AcMaxFrequencyMhz = schemes[activeScheme].Ac, DcMaxFrequencyMhz = schemes[activeScheme].Dc,
            AcMinActiveCoresPercent = schemes[activeScheme].AcParking, DcMinActiveCoresPercent = schemes[activeScheme].DcParking,
            AcBoostMode = schemes[activeScheme].AcBoost, DcBoostMode = schemes[activeScheme].DcBoost, PboScalar = pboScalar,
            PerCoreCurveOptimizer = curves.ToDictionary(pair => pair.Key, pair => pair.Value),
            AdvancedLimits = DirectLimitsEnabled ? new(PptWatts: DirectPpt, Mp1TemperatureC: DirectTemperature, TdcCurrentMilliamps: DirectTdc, EdcCurrentMilliamps: DirectEdc) : null
        };
        private HomeControlState BuildControls()
        {
            var controls = new HomeControlState(mode, StrongCoolingReadbackAvailable ? strongCooling || FirmwareStrongCooling : null, [])
            {
                CpuTuning = BuildCpu(), KeyboardLighting = light, KeyboardLightingPreviewActive = PreviewActive,
                GpuVf = Gpu,
                FanEcControl = FanReadbackAvailable ? Fan : null, ActiveFanControlPlan = fanPlan
            };
            return ControlStateTransform?.Invoke(controls) ?? controls;
        }
        private HomeHardwareState BuildHardwareState()
        {
            HardwareCommand[] commands =
            [
                new SetCpuTuningCommand(Guid.NewGuid(), new(null, null, null, null, null, null, Fixture.Scheme, null), true),
                new SetKeyboardLightingCommand(Guid.NewGuid(), light), new SetLidLogoCommand(Guid.NewGuid(), true),
                new SetPerformanceModeCommand(Guid.NewGuid(), mode), new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 0, true),
                new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 0, true), new SetGpuVfCurveCommand(Guid.NewGuid(), [], [], true),
                new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested), new SetStrongCoolingCommand(Guid.NewGuid(), false)
            ];
            var descriptors = commands.Select(command => HomeCapabilityCatalog.RequiredCapability(command)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(key => new CapabilityDescriptor(key, CapabilityState.Available, null)).ToArray();
            var fields = new[]
            {
                "acMaxFrequencyMhz", "dcMaxFrequencyMhz", "acMinActiveCoresPercent", "dcMinActiveCoresPercent", "isBoostEnabled", "windowsPowerSchemeId", "negativeCurveOptimizer",
                "keyboardLighting", "effect", "brightness", "hex", "speed", "logo", "performanceMode", "coreOffsetKhz", "memoryOffsetKhz", "vfOffsetsKhz", "fanControl", "strongCooling", "pboScalar",
                "strategy", "fixedRpm", "maximumRpm", "cpu", "gpu", "shared", "isShared"
            }.Concat(DirectLimitsEnabled ? new[] { "pptWatts", "mp1TemperatureC", "tdcCurrentMilliamps", "edcCurrentMilliamps" } : []).Select(key => new FieldCapability(key, CapabilityState.Available,
                LightingFieldsReadable || key is not ("effect" or "brightness" or "hex" or "speed" or "logo"), true,
                key == "acMaxFrequencyMhz" || key == "dcMaxFrequencyMhz" ? 400 : key == "negativeCurveOptimizer" ? -50 : key == "memoryOffsetKhz" ? -1000000 : null,
                key == "acMaxFrequencyMhz" || key == "dcMaxFrequencyMhz" ? 6000 : key == "negativeCurveOptimizer" ? 0 : key == "memoryOffsetKhz" ? 3000000 : null,
                null, "fake", "fake", "PreservesSelectedMode", !DirectCapabilitiesVerified && key is "pptWatts" or "mp1TemperatureC" or "tdcCurrentMilliamps" or "edcCurrentMilliamps" ? null : DateTimeOffset.UtcNow, 0)).ToArray();
            return new(new(descriptors) { Fields = fields, IsOnAcPower = true, SupportState = CapabilitySupportState }, DeviceSupportState.Ready, null, Identity, null);
        }
        private static CommandResult Result(Guid id, CommandState state, HardwareMutation mutation) =>
            new(id, state, null, RequiredUserAction.None, state == CommandState.Rejected
                ? ServiceError.Create(ErrorCode.ReadBackMismatch, id, false) : null, false) { Mutation = mutation };
    }
}
