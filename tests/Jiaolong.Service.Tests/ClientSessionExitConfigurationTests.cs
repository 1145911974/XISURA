using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ClientSessionExitConfigurationTests
{
    [TestMethod]
    public async Task Defaults_must_be_persisted_before_writes_and_fields_keep_session_and_original_values()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        Assert.AreEqual("Preparing", (await fixture.Saved()).Phase.ToString());
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await fixture.Write(handle, 4600)).Error!.Code);
        Assert.AreEqual(0, fixture.Provider.Writes);
        Assert.AreEqual(CommandState.Applied, (await fixture.Configure(handle, new(1, handle.DeviceKey, -30))).State);
        var saved = await fixture.Saved();
        Assert.AreEqual(-30, saved.ExitDefaults!.CurveOptimizerAll);
        Assert.AreEqual(handle.DeviceKey, saved.ExitDefaults.DeviceKey);
        Assert.IsTrue(saved.Baseline.QuickSettings.Single().Enabled);
        foreach (int target in new[] { 4600, 4500 })
        {
            var result = await fixture.Write(handle, target);
            Assert.AreEqual(CommandState.Applied, result.State);
            var field = result.ExecutionState!.Fields["performance.acMaxFrequencyMhz"];
            Assert.AreEqual(handle.SessionId, field.ClientSessionId);
            Assert.AreEqual(4700, field.Baseline!.Value!.Value.GetInt32());
            Assert.AreEqual(target, field.LastWritten!.Value!.Value.GetInt32());
            Assert.AreEqual(ApplyState.Confirmed, field.WriteState);
        }
        var journal = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
        Assert.AreEqual(handle.SessionId, journal!.Current!.Fields["performance.acMaxFrequencyMhz"].ClientSessionId);
        Assert.AreEqual(2, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Other_device_keeps_its_baseline_and_cannot_use_transplanted_or_invalid_defaults()
    {
        using var local = new Fixture();
        using var other = new Fixture();
        other.Provider.Key = new string('B', 64);
        other.Provider.Curve = -17;
        var localHandle = await local.Begin();
        var otherHandle = await other.Begin();
        Assert.AreEqual(CommandState.Applied, (await local.Configure(localHandle, new(1, localHandle.DeviceKey, -30))).State);
        foreach (var invalid in new[] { new ExitDeviceDefaults(1, localHandle.DeviceKey, -30),
            new ExitDeviceDefaults(2, otherHandle.DeviceKey, -30), new ExitDeviceDefaults(1, otherHandle.DeviceKey, -51),
            new ExitDeviceDefaults(1, otherHandle.DeviceKey, 1), new ExitDeviceDefaults(1, "", -30) })
            Assert.AreEqual(ErrorCode.ValidationFailed, (await other.Configure(otherHandle, invalid)).Error!.Code);
        Assert.AreEqual(CommandState.Applied, (await other.Configure(otherHandle, null)).State);
        var saved = await other.Saved();
        Assert.IsNull(saved.ExitDefaults);
        Assert.AreEqual(-17, saved.Baseline.CpuTuning!.NegativeCurveOptimizer);
        Assert.AreEqual(0, local.Provider.Writes + other.Provider.Writes);
    }

    [TestMethod]
    public async Task Configuration_IPC_requires_owner_and_cannot_replace_an_active_or_closing_target()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var defaults = new ExitDeviceDefaults(1, handle.DeviceKey, -30);
        var dispatcher = new RequestDispatcher(homeRuntime: fixture.Runtime);
        async Task<CommandResult> Dispatch(ClientIdentity client)
        {
            var id = Guid.NewGuid();
            var request = new RequestEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow, id, "configureClientSession",
                DateTimeOffset.UtcNow.AddSeconds(10), JsonSerializer.SerializeToElement(new ClientSessionExitConfiguration(handle.SessionId, defaults),
                    ProtocolJsonContext.Default.ClientSessionExitConfiguration));
            var response = await dispatcher.DispatchAsync(request, client, CancellationToken.None);
            Assert.AreEqual(ResponseStatus.Success, response.Status);
            return response.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!;
        }
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await Dispatch(Owner() with { ProcessId = 43 })).Error!.Code);
        Assert.AreEqual(CommandState.Applied, (await Dispatch(Owner())).State);
        var original = await File.ReadAllTextAsync(fixture.Path);
        Assert.IsTrue((await Dispatch(Owner())).IsReplay);
        Assert.AreEqual(ErrorCode.ConflictDetected, (await fixture.Configure(handle, defaults with { CurveOptimizerAll = -20 })).Error!.Code);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Configure(handle with { SessionId = Guid.NewGuid() }, defaults)).Error!.Code);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(), handle.SessionId, Owner(), CancellationToken.None)).State);
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await fixture.Configure(handle, defaults)).Error!.Code);
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Configuration_failure_cancellation_or_stale_epoch_never_opens_writes()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var defaults = new ExitDeviceDefaults(1, handle.DeviceKey, -30);
        var original = await File.ReadAllTextAsync(fixture.Path);
        using (var locked = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Configure(handle, defaults)).State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, defaults), Owner(), cancelled.Token));
        Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await fixture.Write(handle, 4600)).Error!.Code);
        fixture.Runtime.Execution.InvalidateHardware();
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Configure(handle, defaults)).State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(0, fixture.Provider.Writes);
        foreach (bool configurationReplay in new[] { true, false })
        {
            using var active = new Fixture();
            var activeHandle = await active.Begin();
            var activeDefaults = new ExitDeviceDefaults(1, activeHandle.DeviceKey, -30);
            Assert.AreEqual(CommandState.Applied, (await active.Configure(activeHandle, activeDefaults)).State);
            using (var locked = new FileStream(active.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = configurationReplay ? await active.Configure(activeHandle, activeDefaults)
                    : await active.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None);
                Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            }
            Assert.AreEqual(ErrorCode.CancelledBeforeApply, (await active.Write(activeHandle, 4600)).Error!.Code);
            Assert.AreEqual(0, active.Provider.Writes);
        }
    }

    [TestMethod]
    public async Task Journal_ownership_without_its_session_file_blocks_recapture_of_tuned_values()
    {
        using var fixture = new Fixture(requireSession: false);
        var command = CpuCommand(null, 4600);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ExecuteAsync(command, CancellationToken.None, userSid: Owner().UserSid)).State);
        int reads = fixture.Provider.BaselineReads;
        var journalPath = System.IO.Path.Combine(fixture.Root, "control-execution.json");
        var original = await File.ReadAllTextAsync(journalPath);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
        var result = await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.IsNull(result.ClientSession);
        Assert.AreEqual(reads, fixture.Provider.BaselineReads);
        Assert.IsFalse(File.Exists(fixture.Path));
        Assert.AreEqual(original, await File.ReadAllTextAsync(journalPath));
        Assert.AreEqual(1, fixture.Provider.Writes);
    }

    private static readonly long Birth = DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc();
    private static ClientIdentity Owner() => ClientIdentity.LocalAdministrator("S-1-5-21-1", 1) with
        { ProcessId = 42, ProcessCreationFileTimeUtc = Birth, ConnectionId = Guid.NewGuid() };
    private static SetCpuTuningCommand CpuCommand(Guid? session, int target) => new(Guid.NewGuid(),
        new(null, null, null, null, null, null, null, null) { AcMaxFrequencyMhz = target }, true) { ClientSessionId = session };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XisuraSessionExitConfigurationTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "client-control-session.json");
        public Provider Provider { get; } = new();
        public HomeServiceRuntime Runtime { get; }
        public Fixture(bool requireSession = true) => Runtime = new(Provider, journal: new(Root), requireClientSessions: requireSession);
        public async Task<ClientControlSessionHandle> Begin() => (await Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None)).ClientSession!;
        public Task<CommandResult> Configure(ClientControlSessionHandle handle, ExitDeviceDefaults? defaults) =>
            Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, defaults), Owner(), CancellationToken.None);
        public Task<CommandResult> Write(ClientControlSessionHandle handle, int target) => Runtime.ExecuteAsync(CpuCommand(handle.SessionId, target), CancellationToken.None,
            userSid: Owner().UserSid, clientIdentity: Owner());
        public async Task<ClientControlSessionSnapshot> Saved() => JsonSerializer.Deserialize<ClientControlSessionSnapshot>(await File.ReadAllTextAsync(Path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Provider : IHomeHardwareProvider
    {
        private static readonly Guid Scheme = Guid.Parse("ab4a14c8-9838-4f42-8b18-000000000001");
        public string Key { get; set; } = new('A', 64);
        public int Curve { get; set; } = -50;
        public int AcFrequency { get; private set; } = 4700;
        public int Writes { get; private set; }
        public int BaselineReads { get; private set; }
        private static readonly HardwareIdentity Identity = new("board", "bios", "cpu", "gpu");
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(new HomeHardwareState(new(
            [new(HomeCapabilityCatalog.RequiredCapability(CpuCommand(null, 4600)), CapabilityState.Available, null)])
            { IsOnAcPower = true, Fields = [new("acMaxFrequencyMhz", CapabilityState.Available, true, true, 400, 6000, 100,
                "MHz", "fake", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)] }, DeviceSupportState.Ready, null, Identity, null));
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) => Task.FromResult<string?>(Key);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", null, null, null, null));
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(new HomeControlState(PerformanceMode.Turbo, false, [new(QuickSettingKind.WinKey, true, null)])
            { CpuTuning = new(null, null, null, null, null, 1, Scheme, Curve, "readback") { AcMaxFrequencyMhz = AcFrequency,
                DcMaxFrequencyMhz = 4700, PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = Curve } } });
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false)
        { BaselineReads++; return ReadControlsAsync(token); }
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            if (command is not SetCpuTuningCommand { Plan.AcMaxFrequencyMhz: { } target }) throw new InvalidOperationException("Unexpected command");
            Writes++; AcFrequency = target;
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed });
        }
    }
}
