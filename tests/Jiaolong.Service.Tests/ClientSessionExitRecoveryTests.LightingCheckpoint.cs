using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Lighting_checkpoint_success_clears_only_checkpoint_and_keeps_unverified_ownership()
    {
        foreach (bool logoOnly in new[] { true, false })
        {
            using var fixture = new Fixture();
            fixture.Provider.LightingFieldsReadable = false;
            var handle = await fixture.Begin(null);
            HardwareCommand command = logoOnly ? new SetLidLogoCommand(Guid.NewGuid(), false)
                : new SetKeyboardLightingCommand(Guid.NewGuid(), Fixture.OriginalLight with { Red = 17, LogoEnabled = false });
            int dispatches = 0;
            fixture.Provider.BeforeWrite = _ => { dispatches++; return Task.CompletedTask; };
            var result = await fixture.Write(command, handle);
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.IsNull(saved.Current!.PendingStep);
            Assert.AreEqual(0, saved.Unresolved.Length);
            Assert.IsTrue(saved.Current.Fields.Count > 0);
            Assert.IsTrue(saved.Current.Fields.Values.All(field => field.WriteState == ApplyState.SubmittedUnverified));
            Assert.IsFalse(fixture.Provider.Light.LogoEnabled);
            Assert.AreEqual(1, dispatches);
        }
    }

    [TestMethod]
    public async Task Lighting_checkpoint_no_mutation_success_clears_without_creating_ownership()
    {
        foreach (string outcome in new[] { "noMutation", "error" })
        {
            using var fixture = new Fixture();
            fixture.Provider.LightingFieldsReadable = false;
            var wrapped = new LightingCheckpointProvider(fixture.Provider, result => outcome switch
            {
                "noMutation" => result with { Mutation = HardwareMutation.NoMutation },
                "error" => result with { Error = ServiceError.Create(ErrorCode.ReadBackMismatch, result.OperationId, false) },
                _ => result with { Mutation = HardwareMutation.Unknown }
            });
            var runtime = new HomeServiceRuntime(wrapped, journal: fixture.Journal, requireClientSessions: true);
            var begin = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            var handle = begin.ClientSession!;
            await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, null), fixture.Owner, CancellationToken.None);
            // NoMutation represents an already matching request; the fake provider performs no physical I/O.
            var command = new SetLidLogoCommand(Guid.NewGuid(), outcome == "noMutation") { ClientSessionId = handle.SessionId };
            await runtime.ExecuteAsync(command, CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            if (outcome == "noMutation")
            {
                Assert.IsNull(saved.Current!.PendingStep);
                Assert.AreEqual(0, saved.Current.Fields.Count);
                Assert.AreEqual(0, saved.Unresolved.Length);
            }
            else Assert.IsNotNull(saved.Current!.PendingStep, outcome);
        }
    }

    [TestMethod]
    public async Task Lighting_checkpoint_recovery_required_keeps_WAL_and_recovers_across_service_restart()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.LightingFieldsReadable = false;
        var prior = await SeedPendingLighting(fixture, false);
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.IsNotNull(saved.Current!.PendingStep);
        Assert.AreEqual(prior.Parent, saved.Unresolved.Single().OperationId);
        Assert.IsTrue(saved.Current.Fields.Values.Any(field => field.WriteState == ApplyState.Unknown));
        var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, saved.Unresolved.Length);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    [TestMethod]
    public async Task Lighting_checkpoint_completion_does_not_clear_a_different_page_checkpoint()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(null);
        var command = new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 10000, true);
        var stateField = typeof(HomeServiceRuntime).GetField("controlExecutionState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        fixture.Provider.BeforeWrite = actual =>
        {
            var state = (ControlExecutionState)stateField.GetValue(fixture.Runtime)!;
            stateField.SetValue(fixture.Runtime, state with
                { PendingStep = new(actual with { ClientSessionId = handle.SessionId }, ["gpu.memoryOffsetKhz"]) });
            return Task.CompletedTask;
        };
        await fixture.Write(command, handle);
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(command.OperationId, saved.Current!.PendingStep!.Command.OperationId);
        CollectionAssert.AreEqual(new[] { "gpu.memoryOffsetKhz" }, saved.Current.PendingStep.Fields);
    }

    [TestMethod]
    public async Task Lighting_checkpoint_preserves_checkpoint_for_unknown_mutation_or_owned_field()
    {
        foreach (bool unknownMutation in new[] { true, false })
        {
        using var fixture = new Fixture();
        fixture.Provider.LightingFieldsReadable = false;
        bool injectMutation = false;
        var wrapped = new LightingCheckpointProvider(fixture.Provider, result => injectMutation
            ? result with { Mutation = HardwareMutation.Unknown } : result);
        var runtime = new HomeServiceRuntime(wrapped, journal: fixture.Journal, requireClientSessions: true);
        var begin = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
        var handle = begin.ClientSession!;
        await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, null), fixture.Owner, CancellationToken.None);
        await runtime.ExecuteAsync(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 10000, true) { ClientSessionId = handle.SessionId },
            CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        injectMutation = unknownMutation;
        fixture.Provider.BeforeWrite = _ =>
        {
            if (unknownMutation) return Task.CompletedTask;
            var owners = (System.Collections.IDictionary)typeof(HomeServiceRuntime)
                .GetField("ownedFields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
            var owner = owners["gpu.memoryOffsetKhz"]!;
            owner.GetType().GetProperty("State")!.SetValue(owner, ApplyState.Unknown);
            return Task.CompletedTask;
        };
        await runtime.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false) { ClientSessionId = handle.SessionId },
            CancellationToken.None, fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.IsNotNull(saved.Current!.PendingStep);
        if (!unknownMutation) Assert.AreEqual(ApplyState.Unknown, saved.Current.Fields["gpu.memoryOffsetKhz"].WriteState);
        }
    }

    private sealed class LightingCheckpointProvider(IHomeHardwareProvider inner, Func<CommandResult, CommandResult> transform) : IHomeHardwareProvider
    {
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => inner.DiagnoseAsync(token);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => inner.ReinitializeAsync(token);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => inner.ReadTelemetryAsync(token);
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => inner.ReadControlsAsync(token);
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false) => inner.ReadControlBaselineAsync(token, includeCpuAdvanced);
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) => inner.ReadDeviceRecoveryKeyAsync(identity, token);
        public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token) => transform(await inner.ExecuteAsync(command, token));
    }
}
