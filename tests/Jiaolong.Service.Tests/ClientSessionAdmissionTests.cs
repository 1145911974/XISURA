using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ClientSessionAdmissionTests
{
    [TestMethod]
    public async Task Only_matching_session_and_verified_process_can_write_or_replay()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var command = Command(handle.SessionId);
        var dispatcher = new RequestDispatcher(homeRuntime: fixture.Runtime);
        async Task<CommandResult> Dispatch(HardwareCommand value, ClientIdentity client)
        {
            var request = new RequestEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow, value.OperationId,
                "executeCommand", DateTimeOffset.UtcNow.AddSeconds(10), JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.HardwareCommand));
            var response = await dispatcher.DispatchAsync(request, client, CancellationToken.None);
            return response.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!;
        }
        Assert.AreEqual(CommandState.Applied, (await Dispatch(command, Owner())).State);
        Assert.AreEqual(1, fixture.Provider.Writes);
        Assert.AreEqual(CommandState.Applied, (await Dispatch(command, Owner())).State);
        foreach (var invalid in new[] { command with { ClientSessionId = null, OperationId = Guid.NewGuid() },
            command with { ClientSessionId = Guid.NewGuid(), OperationId = Guid.NewGuid() } })
            Assert.AreEqual(ErrorCode.UnauthorizedClient, (await Dispatch(invalid, Owner())).Error!.Code);
        foreach (var other in new[] { Owner() with { UserSid = "S-1-5-21-2" }, Owner() with { ProcessId = 43 },
            Owner() with { ProcessCreationFileTimeUtc = Birth + 1 }, Owner() with { SessionId = 2 } })
            Assert.AreEqual(ErrorCode.UnauthorizedClient, (await Dispatch(command, other)).Error!.Code);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Execute(command, null)).Error!.Code);
        Assert.AreEqual(1, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Freeze_drains_inflight_write_and_rejects_queued_or_new_work_without_reopening()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeWrite = async () => { entered.TrySetResult(); await release.Task; };
        var first = fixture.Execute(Command(handle.SessionId), Owner());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = fixture.Execute(Command(handle.SessionId), Owner());
        var frozen = fixture.Freeze(handle.SessionId);
        release.TrySetResult();
        Assert.AreEqual(CommandState.Applied, (await first).State);
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await queued).Error!.Code);
        Assert.AreEqual(CommandState.Applied, (await frozen).State);
        Assert.AreEqual(1, fixture.Provider.Writes);
        var original = await File.ReadAllTextAsync(fixture.Path);
        using var saved = JsonDocument.Parse(original);
        Assert.AreEqual("Closing", saved.RootElement.GetProperty("phase").GetString());
        Assert.AreEqual(CommandState.Applied, (await fixture.Freeze(handle.SessionId)).State);
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await fixture.Execute(Command(handle.SessionId), Owner())).Error!.Code);
        Assert.AreEqual(CommandState.Rejected, (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None)).State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
    }

    [TestMethod]
    public async Task Cancellation_while_freezing_stays_closed_and_authenticated_retry_can_finish()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = fixture.Runtime.Execution.WithHardwareGateAsync(async () => { entered.TrySetResult(); await release.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var freeze = fixture.Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(), handle.SessionId, Owner(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => freeze);
        release.TrySetResult();
        await held;
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await fixture.Execute(Command(handle.SessionId), Owner())).Error!.Code);
        Assert.AreEqual(CommandState.Applied, (await fixture.Freeze(handle.SessionId)).State);
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Foreign_stale_or_failed_freeze_preserves_evidence_and_never_enables_writes()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var original = await File.ReadAllTextAsync(fixture.Path);
        Assert.AreEqual(ErrorCode.UnauthorizedClient,
            (await fixture.Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(), handle.SessionId, Owner() with { ProcessId = 43 }, CancellationToken.None)).Error!.Code);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Freeze(Guid.NewGuid())).Error!.Code);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        using (var locked = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Freeze(handle.SessionId)).State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await fixture.Execute(Command(handle.SessionId), Owner())).Error!.Code);
        fixture.Runtime.Execution.InvalidateHardware();
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Freeze(handle.SessionId)).State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Freeze_rejects_changed_missing_or_damaged_snapshot_without_replacing_it()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var original = await File.ReadAllTextAsync(fixture.Path);
        foreach (string changed in new[] { "{", original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal),
            original.Replace("\"processId\": 42", "\"processId\": 43", StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(fixture.Path, changed);
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Freeze(handle.SessionId)).State);
            Assert.AreEqual(CommandState.RecoveryRequired,
                (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None)).State);
            Assert.AreEqual(changed, await File.ReadAllTextAsync(fixture.Path));
        }
        File.Move(fixture.Path, fixture.Path + ".retained");
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Freeze(handle.SessionId)).State);
        Assert.IsFalse(File.Exists(fixture.Path));
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None)).State);
        File.Move(fixture.Path + ".retained", fixture.Path);
        await File.WriteAllTextAsync(fixture.Path, original);
        Assert.AreEqual(CommandState.Applied, (await fixture.Freeze(handle.SessionId)).State);
        await File.WriteAllTextAsync(fixture.Path, "{");
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Freeze(handle.SessionId)).State);
        Assert.AreEqual("{", await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Service_internal_catalog_publication_requires_active_owner_and_stops_after_freeze()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Lighting,
            PresetKey.Create(ControlModeId.Gaming, 1), "test", JsonSerializer.SerializeToElement(new
                { Effect = "Static", Brightness = 2, Hex = "#BCBCBC", Logo = false, Speed = 1 }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["effect"] = SettingRequest.Set("Static"), ["brightness"] = SettingRequest.Set(2),
                ["hex"] = SettingRequest.Set("#BCBCBC"), ["logo"] = SettingRequest.Set(false), ["speed"] = SettingRequest.Set(1) } };
        var publication = new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ExecuteAsync(publication,
            CancellationToken.None, userSid: "adaptive:S-1-5-21-1")).State);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Runtime.ExecuteAsync(publication with { OperationId = Guid.NewGuid() },
            CancellationToken.None, userSid: "adaptive:S-1-5-21-2")).Error!.Code);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Runtime.ExecuteAsync(publication with { OperationId = Guid.NewGuid() },
            CancellationToken.None, userSid: "adaptive:S-1-5-21-1", clientIdentity: Owner())).Error!.Code);
        Assert.AreEqual(CommandState.Applied, (await fixture.Freeze(handle.SessionId)).State);
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await fixture.Runtime.ExecuteAsync(publication with { OperationId = Guid.NewGuid() },
            CancellationToken.None, userSid: "adaptive:S-1-5-21-1")).Error!.Code);
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Required_session_rejects_unbound_work_but_capture_enables_only_its_owner()
    {
        using var fixture = new Fixture(requireSession: true);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Execute(Command(null), Owner())).Error!.Code);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Execute(Command(Guid.NewGuid()), Owner())).Error!.Code);
        Assert.AreEqual(0, fixture.Provider.Writes);
        var handle = await fixture.Begin();
        Assert.AreEqual(CommandState.Applied, (await fixture.Execute(Command(handle.SessionId), Owner())).State);
        fixture.Runtime.Execution.InvalidateHardware();
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Execute(Command(handle.SessionId), Owner())).State);
        Assert.AreEqual(1, fixture.Provider.Writes);
    }

    private static readonly long Birth = DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc();
    private static ClientIdentity Owner() => ClientIdentity.LocalAdministrator("S-1-5-21-1", 1) with
        { ProcessId = 42, ProcessCreationFileTimeUtc = Birth, ConnectionId = Guid.NewGuid() };
    private static SetQuickSettingCommand Command(Guid? session) => new(Guid.NewGuid(), QuickSettingKind.WinKey, false) { ClientSessionId = session };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XisuraSessionAdmissionTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "client-control-session.json");
        public Provider Provider { get; } = new();
        public HomeServiceRuntime Runtime { get; }
        public Fixture(bool requireSession = false) => Runtime = new(Provider, journal: new(Root), requireClientSessions: requireSession);
        public async Task<ClientControlSessionHandle> Begin()
        {
            var handle = (await Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None)).ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, null), Owner(), CancellationToken.None)).State);
            return handle;
        }
        public Task<CommandResult> Execute(HardwareCommand command, ClientIdentity? owner) => Runtime.ExecuteAsync(command, CancellationToken.None,
            owner?.ConnectionId ?? Guid.Empty, owner?.UserSid, owner);
        public Task<CommandResult> Freeze(Guid session) => Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(), session, Owner(), CancellationToken.None);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Provider : IHomeHardwareProvider
    {
        public int Writes { get; private set; }
        public Func<Task>? BeforeWrite { get; set; }
        private static readonly HardwareIdentity Identity = new("board", "bios", "cpu", "gpu");
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(new HomeHardwareState(
            new([new(HomeCapabilityCatalog.RequiredCapability(Command(null)), CapabilityState.Available, null)]), DeviceSupportState.Ready, null, Identity, null));
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", null, null, null, null));
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(new HomeControlState(PerformanceMode.Turbo, false, []));
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) => Task.FromResult<string?>(new string('A', 64));
        public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            if (BeforeWrite is not null) await BeforeWrite();
            Writes++;
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed };
        }
    }
}
