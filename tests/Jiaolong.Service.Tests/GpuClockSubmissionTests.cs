using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class GpuClockSubmissionTests
{
    [TestMethod]
    public async Task Interrupted_submission_keeps_unknown_audit_and_cannot_replay_after_restart()
    {
        await WithRoot(async root =>
        {
            var request = Request(2200);
            int writes = 0;
            var result = await HomeServiceRuntime.SubmitClockWithAuditAsync(root, request, () =>
            {
                writes++;
                throw new IOException("driverReplyLost");
            }, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
            Assert.AreEqual("Unknown", (await Read(root, request.OperationId))!.Status);
            var replay = await HomeServiceRuntime.SubmitClockWithAuditAsync(root, request, () =>
            {
                writes++;
                return Task.FromResult(result);
            }, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, replay.State);
            Assert.AreEqual(1, writes);
            Assert.IsFalse(ControlExecutionJournal.RequiresGpuRecoveryCheck(
                new SetGpuFrequencyLimitCommand(request.OperationId, request.MaximumMhz, true)));
        });
    }

    [TestMethod]
    public async Task Unwritable_audit_prevents_dispatch()
    {
        await WithRoot(async root =>
        {
            var request = Request(2200);
            string blocked = Path.Combine(root, "gpu-clock-submissions");
            await File.WriteAllTextAsync(blocked, "not a directory");
            int writes = 0;
            try
            {
                await HomeServiceRuntime.SubmitClockWithAuditAsync(root, request, () =>
                {
                    writes++;
                    throw new InvalidOperationException("must not dispatch");
                }, CancellationToken.None);
                Assert.Fail("An unavailable durable audit must reject the request.");
            }
            catch (IOException) { }
            Assert.AreEqual(0, writes);
        });
    }

    private static GpuClockSubmission Request(int? maximum) => new(Guid.NewGuid(),
        new("MRID6-23", "V35", "test-cpu", "test-gpu"), "test-owner", Guid.NewGuid(), 1, maximum, DateTimeOffset.UtcNow);

    private static Task<GpuClockSubmission?> Read(string root, Guid id) =>
        new AtomicJsonStore(Path.Combine(root, "gpu-clock-submissions"), id.ToString("N") + ".json")
            .ReadAsync<GpuClockSubmission>(CancellationToken.None);

    private static async Task WithRoot(Func<string, Task> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "XisuraGpuClockSubmissionTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await run(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Clock_submission_and_reset_keep_real_sessions_closable_with_both_exit_decisions()
    {
        foreach (bool keep in new[] { false, true })
        {
            using var fixture = new Fixture();
            var clock = new SubmissionProvider(fixture.Provider, fixture.Root);
            var runtime = new HomeServiceRuntime(clock, journal: fixture.Journal, requireClientSessions: true);
            await runtime.InitializeAsync(CancellationToken.None);
            var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, begun.State);
            var handle = begun.ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                new(handle.SessionId, null), fixture.Owner, CancellationToken.None)).State);
            foreach (int? maximum in new int?[] { 2200, null })
            {
                var request = new SetGpuFrequencyLimitCommand(Guid.NewGuid(), maximum, true) { ClientSessionId = handle.SessionId };
                var result = await runtime.ExecuteAsync(request, CancellationToken.None,
                    fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
                Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Code.ToString());
                var audit = await new AtomicJsonStore(Path.Combine(fixture.Root, "gpu-clock-submissions"), request.OperationId.ToString("N") + ".json")
                    .ReadAsync<GpuClockSubmission>(CancellationToken.None);
                Assert.AreEqual("SubmittedUnverified", audit!.Status);
                Assert.AreEqual(maximum, audit.MaximumMhz);
                Assert.AreEqual(handle.SessionId, audit.ClientSessionId);
                var journal = await fixture.Journal.ReadAsync(CancellationToken.None);
                Assert.AreEqual(0, journal.Unresolved.Length);
                Assert.IsFalse(journal.Current?.Fields.ContainsKey("gpu.coreFrequencyLimitMhz") == true);
                var execution = (ControlExecutionState?)typeof(HomeServiceRuntime).GetField("controlExecutionState",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime);
                Assert.IsFalse(execution?.Fields.ContainsKey("gpu.coreFrequencyLimitMhz") == true);
            }
            Assert.AreEqual(2, clock.Writes.Count);
            var ended = await runtime.EndClientControlSessionAsync(Guid.NewGuid(), handle.SessionId,
                fixture.Owner, CancellationToken.None, keepChanges: keep);
            Assert.AreEqual(CommandState.Applied, ended.State, ended.Error?.Code.ToString());
            Assert.AreEqual(2, clock.Writes.Count, "Exit must preserve the persistent submission without another reset.");
            CollectionAssert.AreEqual(new int?[] { 2200, null }, clock.Writes.ToArray());
            Assert.AreEqual(ClientControlSessionPhase.Completed,
                (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
        }
    }

    [TestMethod]
    public async Task Clock_submission_preserves_session_authority_risk_and_automatic_admission_guards()
    {
        foreach (bool automatic in new[] { false, true })
        {
            using var fixture = new Fixture();
            var clock = new SubmissionProvider(fixture.Provider, fixture.Root);
            var runtime = new HomeServiceRuntime(clock, journal: fixture.Journal, requireClientSessions: true);
            if (automatic)
            {
                // Exercise the same public route with the coordinator's real Automatic admission bit.
                var execute = typeof(HomeServiceRuntime).GetMethod("ExecuteDirectAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var coordinator = new ControlExecutionCoordinator((command, token, connection) =>
                    (Task<CommandResult>)execute.Invoke(runtime, [command, token, connection])!,
                    (_, _) => Task.CompletedTask, () => Task.CompletedTask, _ => true);
                typeof(HomeServiceRuntime).GetField("execution",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(runtime, coordinator);
            }
            await runtime.InitializeAsync(CancellationToken.None);
            var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, begun.State);
            var handle = begun.ClientSession!;
            await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, null), fixture.Owner, CancellationToken.None);
            foreach (var request in new[]
            {
                new SetGpuFrequencyLimitCommand(Guid.NewGuid(), 2200, false) { ClientSessionId = handle.SessionId },
                new SetGpuFrequencyLimitCommand(Guid.NewGuid(), 2200, true) { ClientSessionId = Guid.NewGuid() }
            })
                Assert.AreEqual(CommandState.Rejected, (await runtime.ExecuteAsync(request, CancellationToken.None,
                    fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner)).State);
            var foreign = fixture.Owner with { UserSid = "S-1-5-21-999", ProcessId = 999 };
            Assert.AreEqual(CommandState.Rejected, (await runtime.ExecuteAsync(
                new SetGpuFrequencyLimitCommand(Guid.NewGuid(), 2200, true) { ClientSessionId = handle.SessionId },
                CancellationToken.None, foreign.ConnectionId, foreign.UserSid, foreign)).State);
            if (automatic)
                Assert.AreEqual(CommandState.Rejected, (await runtime.ExecuteAsync(
                    new SetGpuFrequencyLimitCommand(Guid.NewGuid(), 2200, true) { ClientSessionId = handle.SessionId },
                    CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner)).State);
            Assert.AreEqual(0, clock.Writes.Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "gpu-clock-submissions")));
        }
    }

    private sealed class SubmissionProvider(Provider inner, string root) : IHomeHardwareProvider
    {
        public List<int?> Writes { get; } = [];
        public async Task<HomeHardwareState> DiagnoseAsync(CancellationToken token)
        {
            var state = await inner.DiagnoseAsync(token);
            return state with { Capabilities = state.Capabilities with
            {
                Items = [.. state.Capabilities.Items, new("gpuFrequencyLimit", CapabilityState.Available, null)],
                Fields = [.. state.Capabilities.Fields, new("coreFrequencyLimitMhz", CapabilityState.Available,
                    false, true, 300, 2500, 1, "MHz", "nvidiaSmiSubmission", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)]
            } };
        }
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => inner.ReadTelemetryAsync(token);
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => inner.ReadControlsAsync(token);
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false) => inner.ReadControlBaselineAsync(token, includeCpuAdvanced);
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) => inner.ReadDeviceRecoveryKeyAsync(identity, token);
        public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            if (command is not SetGpuFrequencyLimitCommand clock) return await inner.ExecuteAsync(command, token);
            var pending = await new AtomicJsonStore(Path.Combine(root, "gpu-clock-submissions"), command.OperationId.ToString("N") + ".json")
                .ReadAsync<GpuClockSubmission>(token);
            Assert.AreEqual("Pending", pending!.Status, "The real provider dispatch requires a durable prewrite audit.");
            Writes.Add(clock.Megahertz);
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed };
        }
    }
}
