using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed partial class NativeModeLightingFollowTests
{
    [TestMethod]
    public async Task Required_session_blocks_lighting_reads_before_begin_and_after_freeze()
    {
        using var fixture = await Fixture.CreateAsync(managed: true);
        await fixture.TickAsync();
        Assert.AreEqual(0, fixture.Provider.NativeModeReads);
        var owner = Jiaolong.Service.Ipc.ClientIdentity.LocalAdministrator("S-1-5-21-101-102-103-1001", 1) with
            { ProcessId = 42, ProcessCreationFileTimeUtc = DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc() };
        var result = await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State);
        var handle = result.ClientSession!;
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(handle.SessionId, null), owner, CancellationToken.None)).State);
        await fixture.TickAsync();
        Assert.AreEqual(1, fixture.Provider.NativeModeReads);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(),
            handle.SessionId, owner, CancellationToken.None)).State);
        fixture.Provider.Mode = PerformanceMode.Quiet;
        await fixture.TickAsync(); await fixture.TickAsync();
        Assert.AreEqual(1, fixture.Provider.NativeModeReads);
        Assert.HasCount(0, fixture.Provider.Commands);
    }
    [TestMethod]
    public async Task Lighting_observation_uses_native_evidence_without_effective_cpu_reads()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.TickAsync();
        Assert.AreEqual(0, fixture.Provider.EffectiveModeReads);
        Assert.AreEqual(1, fixture.Provider.NativeModeReads);
        fixture.Provider.Mode = PerformanceMode.Quiet;
        await fixture.TickAsync();
        Assert.HasCount(0, fixture.Provider.Commands, "The first changed sample must not apply lighting.");
        await fixture.TickAsync();
        Assert.HasCount(1, fixture.Provider.Commands);
        Assert.AreEqual(0, fixture.Provider.EffectiveModeReads, "Execution leases also require only fresh native evidence.");
    }

    [TestMethod]
    public async Task Startup_and_disabled_configuration_do_not_replay_lighting()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.TickAsync();
        await fixture.TickAsync();
        Assert.HasCount(0, fixture.Provider.Commands);
        await fixture.ConfigureAsync(false);
        fixture.Provider.Mode = PerformanceMode.Quiet;
        await fixture.TickAsync();
        await fixture.TickAsync();
        Assert.HasCount(0, fixture.Provider.Commands);
    }

    [TestMethod]
    public async Task External_mode_with_no_client_applies_only_lighting_once_and_preserves_logo()
    {
        using var fixture = await Fixture.CreateAsync();
        typeof(HomeServiceRuntime).GetField("lastSubmittedMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Runtime, PerformanceMode.Balanced);
        await fixture.TickAsync();
        fixture.Provider.Mode = PerformanceMode.Quiet;
        await fixture.TickAsync();
        await fixture.TickAsync();
        await fixture.TickAsync();
        Assert.HasCount(1, fixture.Provider.Commands);
        var command = fixture.Provider.Commands.Single() as SetKeyboardLightingCommand;
        Assert.IsNotNull(command);
        Assert.AreEqual((byte)255, command.Plan.Blue);
        Assert.IsNull(command.Plan.LogoEnabled);
        Assert.AreEqual(PerformanceMode.Quiet, fixture.Provider.Mode);
        var saved = await fixture.Store.ReadAsync(CancellationToken.None);
        Assert.IsFalse(saved.Configuration!.Enabled, "Lighting follow cannot enable the performance scheduler.");
    }

    [TestMethod]
    public async Task Queued_lighting_is_cancelled_when_consent_owner_or_native_mode_changes()
    {
        foreach (string change in new[] { "disabled", "owner", "native" })
        {
            using var fixture = await Fixture.CreateAsync();
            fixture.Provider.Mode = PerformanceMode.Quiet;
            var saved = await fixture.Store.ReadAsync(CancellationToken.None);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = fixture.Runtime.Execution.WithHardwareGateAsync(async () =>
            { entered.SetResult(); await release.Task; return true; });
            await entered.Task;
            try
            {
                long generation = fixture.Runtime.Execution.Generation;
                var queued = fixture.ApplyAsync(PerformanceMode.Quiet, saved.Revision);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (fixture.Runtime.Execution.Generation == generation)
                    await Task.Delay(5, deadline.Token);
                if (change == "disabled") await fixture.ConfigureAsync(false);
                else if (change == "owner") await fixture.ConfigureAsync(true, "S-1-5-21-999-999-999-1002");
                else fixture.Provider.Mode = PerformanceMode.Turbo;
                release.SetResult();
                var result = await queued;
                Assert.AreNotEqual(CommandState.Applied, result.State, change);
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, change);
                Assert.HasCount(0, fixture.Provider.Commands, change);
            }
            finally { release.TrySetResult(); await blocker; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string TempRoot = @"C:\Users\Administrator\AppData\Local\Temp";
        private const string Owner = "S-1-5-21-101-102-103-1001";
        private readonly string root = Path.Combine(TempRoot, "XisuraNativeLightingTest-" + Guid.NewGuid().ToString("N"));
        private object? worker;
        public AdaptiveAutomationStateStore Store { get; } = new();
        public FakeProvider Provider { get; } = new();
        public AdaptiveAutomationHardwareProvider Bridge { get; }
        public HomeServiceRuntime Runtime { get; }

        private Fixture(bool managed = false)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(Store, true);
            typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(Store, new AtomicJsonStore(root, "adaptive.json"));
            Bridge = new(Provider, Store);
            Runtime = new(Bridge, journal: managed ? new(root) : null, requireClientSessions: managed);
        }

        public static async Task<Fixture> CreateAsync(bool managed = false)
        {
            var fixture = new Fixture(managed);
            await fixture.ConfigureAsync(true);
            await fixture.Runtime.InitializeAsync(CancellationToken.None);
            return fixture;
        }

        public Task ConfigureAsync(bool follow, string owner = Owner)
        {
            var office = PresetKey.Create(ControlModeId.Office, 1);
            var gaming = PresetKey.Create(ControlModeId.Gaming, 1);
            var turbo = PresetKey.Create(ControlModeId.Turbo, 1);
            AdaptiveAutomationPreset Preset(PresetKey key, PerformanceMode mode, string hex)
            {
                var item = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Lighting, key,
                    "test", JsonSerializer.SerializeToElement(new { Effect = "Static", Brightness = 2, Hex = hex, Logo = false, Speed = 1 }), DateTimeOffset.UtcNow)
                { Revision = 1, Settings = new()
                    { ["effect"] = SettingRequest.Set("Static"), ["brightness"] = SettingRequest.Set(2),
                      ["hex"] = SettingRequest.Set(hex), ["speed"] = SettingRequest.Set(1), ["logo"] = SettingRequest.Set(false) } };
                return new(key, mode, null) { Pages = [item], Selections = Enum.GetValues<ControlPageId>()
                    .Select(page => new PageSelection(page, follow, key, 1)).ToArray() };
            }
            var configuration = new AdaptiveAutomationConfiguration(false, AdaptiveAutomationStrategyId.BalancedAdaptive,
                new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
                new(office, gaming, turbo, office, gaming),
                [Preset(office, PerformanceMode.Quiet, "#0000FF"), Preset(gaming, PerformanceMode.Balanced, "#BCBCBC"), Preset(turbo, PerformanceMode.Turbo, "#FF0000")]);
            return Store.SetConfigurationAsync(configuration, CancellationToken.None, ownerSid: owner);
        }

        public Task TickAsync()
        {
            var type = typeof(HomeServiceRuntime).Assembly.GetType("Jiaolong.Service.Home.NativeModeLightingFollowWorker");
            Assert.IsNotNull(type, "A service-owned lighting follower is missing.");
            var logger = typeof(NullLogger<>).MakeGenericType(type).GetField("Instance")!.GetValue(null);
            worker ??= Activator.CreateInstance(type, Store, Runtime, Bridge, logger)!;
            var method = type.GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (Task)method.Invoke(worker, [CancellationToken.None])!;
        }

        public Task<CommandResult> ApplyAsync(PerformanceMode mode, long revision)
        {
            var method = typeof(AdaptiveAutomationHardwareProvider).GetMethod("ExecuteModeLightingFollowAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "The internal lighting-only dispatch lease is missing.");
            return (Task<CommandResult>)method.Invoke(Bridge, [Runtime, mode, revision, CancellationToken.None])!;
        }

        public void Dispose()
        {
            (worker as IDisposable)?.Dispose();
            if (!Path.GetFullPath(root).StartsWith(TempRoot + "\\XisuraNativeLightingTest-", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected test cleanup path");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FakeProvider : IHomeHardwareProvider
    {
        public int EffectiveModeReads { get; private set; }
        public int NativeModeReads { get; private set; }
        public PerformanceMode Mode { get; set; } = PerformanceMode.Balanced;
        public List<HardwareCommand> Commands { get; } = [];
        private KeyboardLightingPlan lighting = new("Fixed", 100)
            { Red = 204, Green = 255, Blue = 0, BrightnessLevel = 3, LogoEnabled = true };
        private PerformanceModeEvidence Evidence() => new(DateTimeOffset.UtcNow, "test", 4, "00", 0, Mode, Mode, "InterfaceAcknowledged");
        private HomeControlState Controls() => new(Mode, false, []) { KeyboardLighting = lighting, ModeEvidence = Evidence() };
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(new HomeHardwareState(
            new CapabilitySnapshot([new("performanceMode", CapabilityState.Available, null), new("keyboardLighting", CapabilityState.Available, null)])
                { SupportState = DeviceSupportState.Ready, Identity = new("test", "test", "test", "test"),
                  Fields = new[] { "effect", "brightness", "hex", "speed", "logo" }.Select(key => new FieldCapability(
                      key, CapabilityState.Available, true, true, null, null, null,
                      "", "test", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)).ToArray() },
            DeviceSupportState.Ready, null, new("test", "test", "test", "test"), null) { Controls = Controls() });
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "test", null, null, null, null));
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(Controls());
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) =>
            Task.FromResult<string?>(new string('A', 64));
        public Task<PerformanceModeEvidence?> ReadModeEvidenceAsync(CancellationToken token)
        { EffectiveModeReads++; return Task.FromResult<PerformanceModeEvidence?>(Evidence()); }
        public Task<PerformanceModeEvidence?> ReadNativeModeEvidenceAsync(CancellationToken token)
        { NativeModeReads++; return Task.FromResult<PerformanceModeEvidence?>(Evidence()); }
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            Commands.Add(command);
            if (command is not SetKeyboardLightingCommand light) throw new InvalidOperationException("Only lighting hardware commands are permitted.");
            lighting = light.Plan with { LogoEnabled = lighting.LogoEnabled };
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed });
        }
    }
}
