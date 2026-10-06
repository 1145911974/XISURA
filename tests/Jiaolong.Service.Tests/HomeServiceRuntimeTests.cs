using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong.Contracts.Protocol;
using System.Text.Json;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class HomeServiceRuntimeTests
{
    [TestMethod]
    public async Task Invalid_command_metadata_returns_an_error_without_touching_hardware()
    {
        var provider = new FakeProvider(AvailableState("performanceMode"));
        var dispatcher = new RequestDispatcher(homeRuntime: new HomeServiceRuntime(provider));
        var client = ClientIdentity.LocalInteractive("S-1-5-21", 1);
        var operationId = Guid.NewGuid();
        JsonElement[] invalidPayloads =
        [
            JsonSerializer.SerializeToElement(new { operationId }),
            JsonSerializer.SerializeToElement(new { type = "unknown", operationId }),
            JsonSerializer.SerializeToElement(new { operationId, type = "setPerformanceMode", mode = "balanced" })
        ];
        foreach (var payload in invalidPayloads)
        {
            var response = await dispatcher.DispatchAsync(Request(payload), client, CancellationToken.None);
            Assert.AreEqual(ResponseStatus.Error, response.Status);
            Assert.AreEqual(ErrorCode.InvalidFrame, response.Error?.Code);
            Assert.AreEqual(operationId, response.OperationId);
        }
        Assert.AreEqual(0, provider.ExecuteCount);

        var valid = JsonSerializer.SerializeToElement(
            new SetPerformanceModeCommand(operationId, PerformanceMode.Balanced),
            ProtocolJsonContext.Default.HardwareCommand);
        var accepted = await dispatcher.DispatchAsync(Request(valid), client, CancellationToken.None);
        Assert.AreEqual(ResponseStatus.Success, accepted.Status);
        Assert.AreEqual(1, provider.ExecuteCount);

        RequestEnvelope Request(JsonElement payload) => new(
            new ProtocolVersion(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow,
            operationId, "executeCommand", DateTimeOffset.UtcNow.AddSeconds(10), payload);
    }

    [TestMethod]
    public async Task Read_only_initialization_completes_without_lock_reentry()
    {
        var runtime = new HomeServiceRuntime(new FakeProvider(ReadOnlyState()));

        var state = await runtime.InitializeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(DeviceSupportState.ReadOnly, state.SupportState);
    }

    [TestMethod]
    public async Task Unsupported_command_rechecks_hardware_once_before_rejecting()
    {
        var provider = new FakeProvider(ReadOnlyState());
        var runtime = new HomeServiceRuntime(provider);

        var result = await runtime.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual(ErrorCode.CapabilityUnavailable, result.Error?.Code);
        Assert.AreEqual(1, provider.ReinitializeCount);
        Assert.AreEqual(0, provider.ExecuteCount);

        var legacyProvider = new FakeProvider(AvailableState("gpuVoltageBoost"));
        var legacyRuntime = new HomeServiceRuntime(legacyProvider);
        var legacy = await legacyRuntime.ExecuteAsync(new SetGpuVoltageBoostCommand(Guid.NewGuid(), 0, 1, true), CancellationToken.None);
        Assert.AreEqual(CommandState.Rejected, legacy.State);
        Assert.AreEqual(ErrorCode.CapabilityUnavailable, legacy.Error?.Code);
        Assert.AreEqual(0, legacyProvider.ExecuteCount);
        Assert.AreEqual(0, legacyProvider.ReinitializeCount);
    }

    [TestMethod]
    public async Task Available_command_is_executed_without_repair()
    {
        var provider = new FakeProvider(AvailableState("performanceMode"));
        var runtime = new HomeServiceRuntime(provider);

        var result = await runtime.ExecuteAsync(
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced),
            CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.IsNull(result.Error);
        Assert.AreEqual(0, provider.ReinitializeCount);
        Assert.AreEqual(1, provider.ExecuteCount);
    }

    [TestMethod]
    public async Task Applied_command_refreshes_controls_from_hardware_after_write()
    {
        var provider = new FakeProvider(AvailableState("performanceMode"));
        provider.LiveControls = provider.State.Controls with { PerformanceMode = PerformanceMode.Balanced };
        var runtime = new HomeServiceRuntime(provider);

        _ = await runtime.ExecuteAsync(
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced),
            CancellationToken.None);

        Assert.AreEqual(1, provider.DiagnoseCount);
        Assert.AreEqual(1, provider.ReadControlsCount);
        Assert.AreEqual(1, provider.ReadTelemetryCount);
        Assert.AreSame(provider.LiveControls, (await runtime.GetStateAsync(CancellationToken.None)).Controls);
    }

    [TestMethod]
    public async Task Home_state_reads_live_controls_after_external_hardware_change()
    {
        var provider = new FakeProvider(AvailableState("performanceMode"))
        {
            LiveControls = new HomeControlState(PerformanceMode.Turbo, null, Array.Empty<QuickSettingStatus>())
        };
        var runtime = new HomeServiceRuntime(provider);

        await runtime.InitializeAsync(CancellationToken.None);
        var snapshot = await runtime.GetHomeStateAsync(CancellationToken.None);

        Assert.AreEqual(PerformanceMode.Turbo, snapshot.Controls.PerformanceMode);
        Assert.AreEqual(1, provider.ReadControlsCount);
    }

    [TestMethod]
    public async Task Home_state_refreshes_telemetry_instead_of_reusing_the_diagnosis_snapshot()
    {
        var diagnosisTelemetry = new HardwareSnapshot(DateTimeOffset.UtcNow.AddSeconds(-10), "normal", 45, null, 30, null)
        {
            CpuUsagePercent = 10
        };
        var liveTelemetry = new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 55, null, 40, null)
        {
            CpuUsagePercent = 80
        };
        var provider = new FakeProvider(AvailableState("monitoring") with { Telemetry = diagnosisTelemetry })
        {
            LiveTelemetry = liveTelemetry
        };
        var runtime = new HomeServiceRuntime(provider);

        await runtime.InitializeAsync(CancellationToken.None);
        var snapshot = await runtime.GetHomeStateAsync(CancellationToken.None);

        Assert.AreEqual(liveTelemetry.CapturedAtUtc, snapshot.Telemetry?.CapturedAtUtc);
        Assert.AreEqual(80d, snapshot.Telemetry?.CpuUsagePercent);
        Assert.AreEqual(1, provider.ReadTelemetryCount);
    }

    [TestMethod]
    public async Task Lid_logo_quick_setting_uses_the_verified_home_capability()
    {
        var provider = new FakeProvider(AvailableState("quickSetting:lidLogo"));
        var runtime = new HomeServiceRuntime(provider);

        var result = await runtime.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.LidLogo, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(1, provider.ExecuteCount);
    }

    [TestMethod]
    public async Task Failed_reinitialize_exposes_repair_required_state()
    {
        var provider = new FakeProvider(ReadOnlyState()) { ReinitializeSucceeds = false };
        var runtime = new HomeServiceRuntime(provider);

        _ = await runtime.ExecuteAsync(
            new SetStrongCoolingCommand(Guid.NewGuid(), true),
            CancellationToken.None);

        Assert.AreEqual(DeviceSupportState.RepairRequired, runtime.State.SupportState);
        Assert.AreEqual("hardwareUnavailable", runtime.State.Reason);
    }

    [TestMethod]
    public async Task Rejected_commands_do_not_repeat_hardware_repair_forever()
    {
        var provider = new FakeProvider(ReadOnlyState());
        var runtime = new HomeServiceRuntime(provider);

        _ = await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true), CancellationToken.None);
        _ = await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true), CancellationToken.None);

        Assert.AreEqual(1, provider.ReinitializeCount);
    }

    [TestMethod]
    public void Cpu_tuning_command_requires_cpu_tuning_capability()
    {
        var command = new SetCpuTuningCommand(
            Guid.NewGuid(),
            new CpuTuningPlan(75, 35, 45, 3800, false, null, null, null),
            false);

        Assert.AreEqual("cpuTuning", HomeCapabilityCatalog.RequiredCapability(command));
        CollectionAssert.Contains(HomeCapabilityCatalog.ControlKeys.ToArray(), "cpuTuning");
    }

    [TestMethod]
    public void Telemetry_read_prefers_verified_provider_and_falls_back_to_observed_provider()
    {
        var verified = new WmiProviderEvidence { Namespace = "verified" };
        var observed = new WmiProviderEvidence { Namespace = "observed" };

        Assert.AreSame(verified, HomeTelemetryProviderSelection.ForRead(verified, observed));
        Assert.AreSame(observed, HomeTelemetryProviderSelection.ForRead(null, observed));
    }

    [TestMethod]
    public async Task Fan_disconnect_releases_only_current_owner_once()
    {
        var provider = new FakeProvider(AvailableState("strongCooling"));
        var runtime = new HomeServiceRuntime(provider);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true), CancellationToken.None, first);
        Assert.IsNull(await runtime.ReleaseClientFanAsync(second));
        await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true), CancellationToken.None, second);
        Assert.IsNull(await runtime.ReleaseClientFanAsync(first));
        Assert.AreEqual(CommandState.Applied, (await runtime.ReleaseClientFanAsync(second))!.State);
        Assert.AreEqual(ReleaseReason.SessionEnded, ((ReleaseFanControlCommand)provider.LastCommand!).Reason);
        Assert.IsNull(await runtime.ReleaseClientFanAsync(second));
        Assert.AreEqual(3, provider.ExecuteCount);
        var controls = new HomeControlState(PerformanceMode.Turbo, false, []);
        var ec = new FanEcControlState(DateTimeOffset.UtcNow, 0, 4, 58, 58, 0);
        Assert.IsTrue(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { FanEcControl = ec }));
        Assert.IsTrue(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { FanEcControl = ec with { CpuTarget = 0 } }));
        Assert.IsTrue(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { FanEcControl = ec with { GpuTarget = 0 } }));
        Assert.IsFalse(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { FanEcControl = ec with { CpuTarget = 0, GpuTarget = 0 } }));
        Assert.IsTrue(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { StrongCooling = true }));
        Assert.IsFalse(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { FanEcControl = ec, FanAutomaticCeilingActive = true }));
        Assert.IsTrue(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { FanEcControl = ec, FanAutomaticCeilingActive = true, StrongCooling = true }));
        Assert.IsTrue(AdaptiveAutomationWorker.HasManualCoolingControl(controls with { FanEcControl = ec with { Control = 10, CpuTarget = 0, GpuTarget = 0 } }));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var ownerTask = Task.Run(() => BldingFanEcTransport.WithEcGate(() =>
        {
            held.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return true;
        }));
        Task? contender = null;
        try
        {
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(2)));
            contender = Task.Run(() =>
            {
                contenderStarted.Set();
                return BldingFanEcTransport.WithEcGate(() => { entered.Set(); return true; });
            });
            Assert.IsTrue(contenderStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(entered.Wait(TimeSpan.FromMilliseconds(150)), "EC register selection must remain exclusive across clients.");
        }
        finally
        {
            release.Set();
            await ownerTask;
            if (contender is not null) await contender;
        }
        Assert.IsTrue(entered.IsSet);
        Assert.ThrowsExactly<IOException>(() => BldingFanEcTransport.WithEcGate<bool>(() => throw new IOException()));
        Assert.IsTrue(await Task.Run(() => BldingFanEcTransport.WithEcGate(() => true)), "A failed EC operation must release the gate.");
    }

    [TestMethod]
    public async Task Fan_rejected_takeover_preserves_owner_and_failed_release_can_retry()
    {
        var provider = new FakeProvider(AvailableState("strongCooling"));
        var runtime = new HomeServiceRuntime(provider);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true), CancellationToken.None, owner);
        provider.ResultState = CommandState.Rejected;
        await runtime.ExecuteAsync(new SetStrongCoolingCommand(Guid.NewGuid(), true), CancellationToken.None, other);
        Assert.IsNull(await runtime.ReleaseClientFanAsync(other));
        provider.ResultState = CommandState.RecoveryRequired;
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.ReleaseClientFanAsync(owner))!.State);
        provider.ResultState = CommandState.Applied;
        Assert.AreEqual(CommandState.Applied, (await runtime.ReleaseClientFanAsync(owner))!.State);
        Assert.AreEqual(4, provider.ExecuteCount);
    }

    private static HomeHardwareState ReadOnlyState() => new(
        new CapabilitySnapshot(Array.Empty<CapabilityDescriptor>()),
        DeviceSupportState.ReadOnly,
        "capabilityUnavailable",
        new HardwareIdentity("MRID6-23", "MRID6_23_P_V39", "AMD Ryzen 7 7745HX", "NVIDIA GeForce RTX 4070 Laptop GPU"),
        null);

    [TestMethod]
    public async Task Automatic_fan_follow_yields_to_strong_cooling_without_stealing_ownership()
    {
        var provider = new FakeProvider(AvailableState("fanControl"))
        {
            LiveControls = new HomeControlState(PerformanceMode.Balanced, true, [])
        };
        var runtime = new HomeServiceRuntime(provider);
        var owner = Guid.NewGuid();
        var follower = Guid.NewGuid();
        var plan = new FanControlPlan([new FanPoint(30, 30), new FanPoint(100, 100)]);
        var manual = new SetFanControlCommand(Guid.NewGuid(), plan, true);
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(manual, CancellationToken.None, owner)).State);
        foreach (bool? strongCooling in new bool?[] { true, null })
        {
            provider.LiveControls = provider.LiveControls with { StrongCooling = strongCooling };
            HardwareCommand[] commands = [
                manual with { OperationId = Guid.NewGuid(), PreserveStrongCooling = true },
                new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested) { PreserveStrongCooling = true }
            ];
            foreach (var command in commands)
            {
                var result = await runtime.ExecuteAsync(command, CancellationToken.None, follower);
                Assert.AreEqual(CommandState.Rejected, result.State);
                Assert.AreEqual(ErrorCode.CommandInProgress, result.Error?.Code);
            }
        }
        Assert.AreEqual(1, provider.ExecuteCount);
        Assert.IsNull(await runtime.ReleaseClientFanAsync(follower));
        Assert.AreEqual(CommandState.Applied, (await runtime.ReleaseClientFanAsync(owner))!.State);
        provider.LiveControls = provider.LiveControls with { StrongCooling = false };
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(manual with { PreserveStrongCooling = true }, CancellationToken.None, follower)).State);
        Assert.AreEqual(3, provider.ExecuteCount);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task Adaptive_handoff_drains_the_entire_cycle_and_explicit_enable_releases_manual_override()
    {
        string root = Path.Combine(@"C:\Users\Administrator\AppData\Local\Temp", "xisura-cycle-test-" + Guid.NewGuid().ToString("N"));
        var store = new AdaptiveAutomationStateStore();
        // Isolate persistence from the running service's ProgramData state.
        typeof(AdaptiveAutomationStateStore).GetField("loaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(store, true);
        typeof(AdaptiveAutomationStateStore).GetField("jsonStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(store, new Jiaolong.Service.Storage.AtomicJsonStore(root, "adaptive.json"));
        var provider = new FakeProvider(AvailableState("performanceMode"));
        var bridge = new AdaptiveAutomationHardwareProvider(provider, store);
        var runtime = new HomeServiceRuntime(bridge);
        await runtime.InitializeAsync(CancellationToken.None);
        var office = PresetKey.Create(ControlModeId.Office, 2);
        var game = PresetKey.Create(ControlModeId.Gaming, 2);
        var turbo = PresetKey.Create(ControlModeId.Turbo, 2);
        var configuration = new AdaptiveAutomationConfiguration(false, AdaptiveAutomationStrategyId.BalancedAdaptive,
            new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
            new(office, game, turbo, office, game),
            [new(office, PerformanceMode.Quiet, null), new(game, PerformanceMode.Balanced, null), new(turbo, PerformanceMode.Turbo, null)]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cycle = bridge.RunAutomationCycleAsync(async token =>
        {
            await release.Task.WaitAsync(token);
            var result = await bridge.ExecuteAutomaticCommandAsync(runtime, new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), token);
            Assert.AreEqual(CommandState.Applied, result.State);
        }, CancellationToken.None);
        try
        {
            var disable = bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration), CancellationToken.None);
            var manual = bridge.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), CancellationToken.None);
            Assert.IsFalse(disable.IsCompleted);
            Assert.IsFalse(manual.IsCompleted);
            Assert.AreEqual(0, provider.ExecuteCount);
            using var cancelled = new CancellationTokenSource();
            var interrupted = bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration), cancelled.Token);
            cancelled.Cancel();
            try { await interrupted; Assert.Fail("Cancelled handoff should not acquire the cycle gate"); }
            catch (OperationCanceledException) { }
            release.SetResult();
            await Task.WhenAll(cycle, disable, manual);
            Assert.AreEqual(2, provider.ExecuteCount);
            Assert.IsNotNull((await store.ReadAsync(CancellationToken.None)).ManualOverrideUntilUtc);
            await bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration with { Enabled = true }), CancellationToken.None);
            Assert.IsNull((await store.ReadAsync(CancellationToken.None)).ManualOverrideUntilUtc);
            await store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow, true, CancellationToken.None);
            await bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration with { Enabled = true }), CancellationToken.None);
            Assert.IsNotNull((await store.ReadAsync(CancellationToken.None)).ManualOverrideUntilUtc);
        }
        finally
        {
            release.TrySetResult();
            await cycle;
            Directory.Delete(root, recursive: true);
        }
    }

    private static HomeHardwareState AvailableState(string key) => new(
        new CapabilitySnapshot([new CapabilityDescriptor(key, CapabilityState.Available, null)]),
        DeviceSupportState.Ready,
        null,
        new HardwareIdentity("MRID6-23", "MRID6_23_P_V39", "AMD Ryzen 7 7745HX", "NVIDIA GeForce RTX 4070 Laptop GPU"),
        null);

    private sealed class FakeProvider(HomeHardwareState state) : IHomeHardwareProvider
    {
        public HomeHardwareState State { get; private set; } = state;
        public int ReinitializeCount { get; private set; }
        public int DiagnoseCount { get; private set; }
        public int ReadControlsCount { get; private set; }
        public int ReadTelemetryCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public HardwareCommand? LastCommand { get; private set; }
        public CommandState ResultState { get; set; } = CommandState.Applied;
        public bool ReinitializeSucceeds { get; init; } = true;
        public HomeControlState LiveControls { get; set; } = state.Controls;
        public HardwareSnapshot? LiveTelemetry { get; set; }

        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken)
        {
            DiagnoseCount++;
            return Task.FromResult(State);
        }

        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken)
        {
            ReinitializeCount++;
            if (!ReinitializeSucceeds)
            {
                State = State with { SupportState = DeviceSupportState.RepairRequired, Reason = "hardwareUnavailable" };
            }

            return Task.FromResult(State);
        }

        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken)
        {
            ReadTelemetryCount++;
            return Task.FromResult(LiveTelemetry ?? State.Telemetry ?? new HardwareSnapshot(DateTimeOffset.UtcNow, "unknown", null, null, null, null));
        }

        public Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken)
        {
            ReadControlsCount++;
            return Task.FromResult(LiveControls);
        }

        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            LastCommand = command;
            return Task.FromResult(new CommandResult(command.OperationId, ResultState, State.Telemetry, RequiredUserAction.None, null, false));
        }
    }
}
