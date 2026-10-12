using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong.Contracts.Protocol;
using System.Text.Json;
using System.Reflection;
using Jiaolong.Service.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class HomeServiceRuntimeTests
{
    [TestMethod]
    public async Task Interface_acknowledgement_is_not_effective_confirmation_and_delayed_reversion_is_recorded()
    {
        var evidence = new PerformanceModeEvidence(DateTimeOffset.UtcNow, "MI/19", 4, "0000000001", 1,
            PerformanceMode.Turbo, PerformanceMode.Turbo, "InterfaceAcknowledged");
        var provider = new FakeProvider(AvailableState("performanceMode"))
        { ReplyEvidence = evidence, ObservedEvidence = evidence with { RawValue = 0, ParsedMode = PerformanceMode.Balanced } };
        var writer = new RecordingDiagnostics();
        var runtime = new HomeServiceRuntime(provider, writer);
        var result = await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), CancellationToken.None);
        Assert.IsNull(result.VerifiedPerformanceMode);
        Assert.AreEqual("InterfaceAcknowledged", runtime.State.Controls.ModeEvidence?.VerificationLevel);
        Assert.AreEqual(0, provider.ReadControlsCount);
        await writer.Observation.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.AreEqual("Balanced", writer.Observation.Task.Result.Fields["parsedMode"]);
        Assert.AreEqual(result.OperationId, writer.Observation.Task.Result.OperationId);
        Assert.AreEqual("1000", writer.Observation.Task.Result.Fields["sampleTargetMs"]);
        var alternate = new FakeProvider(AvailableState("performanceMode"))
        {
            ReplyEvidence = evidence with { RawValue = 2, ParsedMode = PerformanceMode.Quiet },
            LiveControls = new HomeControlState(PerformanceMode.Quiet, null, [])
        };
        var alternateRuntime = new HomeServiceRuntime(alternate);
        var alternateResult = await alternateRuntime.ExecuteAsync(
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), CancellationToken.None);
        Assert.IsNull(alternateResult.VerifiedPerformanceMode);
        Assert.AreEqual(PerformanceMode.Quiet, alternateRuntime.State.Controls.PerformanceMode);
    }

    private sealed class RecordingDiagnostics : Jiaolong.Diagnostics.IDiagnosticEventWriter
    {
        public TaskCompletionSource<Jiaolong.Diagnostics.DiagnosticEvent> Observation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Jiaolong.Diagnostics.DiagnosticEvent? LastCommand { get; private set; }
        public ValueTask WriteAsync(Jiaolong.Diagnostics.DiagnosticEvent value, CancellationToken token)
        {
            if (value.EventName == "mode.observed" && value.Fields.GetValueOrDefault("parsedMode") == "Balanced") Observation.TrySetResult(value);
            else LastCommand = value;
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task No_mutation_rejection_preserves_state_and_unknown_write_keeps_readback()
    {
        foreach (var mutation in new[] { HardwareMutation.NoMutation, HardwareMutation.Unknown })
        {
            var error = ServiceError.Create(ErrorCode.HardwareReadFailed, Guid.Empty, false) with
                { Details = new() { ["stage"] = "Capture", ["cause"] = "advancedCpuPmTableUnsupported" } };
            var provider = new FakeProvider(AvailableState("cpuTuning"))
            { ResultState = CommandState.Rejected, Mutation = mutation, ReplyError = error };
            provider.LiveControls = provider.LiveControls with
            { CpuTuning = new(null, null, null, 4200, true, null, Guid.NewGuid(), null, null) { AcBoostMode = 2, DcBoostMode = 2 } };
            var writer = new RecordingDiagnostics();
            var runtime = new HomeServiceRuntime(provider, writer);
            var before = await runtime.GetStateAsync(CancellationToken.None);
            await runtime.ExecuteAsync(new SetCpuTuningBatchCommand(Guid.NewGuid(),
                [new(null, null, null, 3800, false, null, null, null)], true), CancellationToken.None);
            Assert.AreEqual(1, provider.DiagnoseCount);
            Assert.AreEqual(mutation == HardwareMutation.NoMutation ? 1 : 2, provider.ReadControlsCount);
            Assert.AreEqual(0, provider.ReinitializeCount);
            Assert.AreEqual("hardwareReadFailed", writer.LastCommand?.ErrorCode);
            Assert.AreNotEqual(Guid.Empty, writer.LastCommand?.CorrelationId);
            Assert.AreEqual("Capture", writer.LastCommand?.Fields["stage"]);
            Assert.AreEqual("advancedCpuPmTableUnsupported", writer.LastCommand?.Fields["cause"]);
            if (mutation == HardwareMutation.NoMutation) Assert.AreSame(before, runtime.State);
        }
    }

    [TestMethod]
    public async Task Successful_followed_control_refreshes_state_without_repeating_device_diagnosis()
    {
        var provider = new FakeProvider(AvailableState("quickSetting:fnLock"));
        var runtime = new HomeServiceRuntime(provider);
        var result = await runtime.ExecuteAsync(new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.FnLock, true), CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(1, provider.DiagnoseCount);
        Assert.AreEqual(1, provider.ReadControlsCount);
    }

    [TestMethod]
    public async Task Verified_mode_reply_does_not_wait_for_monitor_refresh()
    {
        var sensorRead = new TaskCompletionSource<HardwareSnapshot>();
        var provider = new FakeProvider(AvailableState("performanceMode"))
        {
            VerifiedMode = PerformanceMode.Balanced,
            PendingTelemetry = sensorRead.Task
        };
        var runtime = new HomeServiceRuntime(provider);
        var operation = runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None);
        try
        {
            Assert.IsTrue(operation.IsCompletedSuccessfully, "A verified mode must not wait for unrelated sensors.");
            Assert.AreEqual(PerformanceMode.Balanced, (await runtime.GetStateAsync(CancellationToken.None)).Controls.PerformanceMode);
            Assert.AreEqual(0, provider.ReadTelemetryCount);
        }
        finally
        {
            sensorRead.TrySetResult(new(DateTimeOffset.UtcNow, "normal", 50, 40, 20, 10));
            await operation;
        }
    }

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
        var actual = (await runtime.GetStateAsync(CancellationToken.None)).Controls;
        Assert.AreEqual(provider.LiveControls.PerformanceMode, actual.PerformanceMode);
        Assert.IsNotNull(actual.ExecutionState);
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

        await runtime.HandleLifecycleAsync(false, CancellationToken.None);
        Assert.IsTrue(provider.BackgroundWorkPaused, "Background timers must stop before lifecycle work waits for hardware.");
        int controlsBeforeSuspendPoll = provider.ReadControlsCount;
        var suspended = await runtime.GetHomeStateAsync(CancellationToken.None);
        var suspendedTelemetry = await runtime.ReadTelemetryAsync(CancellationToken.None);
        Assert.AreEqual(1, provider.ReadTelemetryCount, "Suspend must stop new sensor reads.");
        Assert.AreEqual(controlsBeforeSuspendPoll, provider.ReadControlsCount, "Suspend must stop WMI/SMU control reads.");
        Assert.AreEqual(liveTelemetry.CapturedAtUtc, suspended.Telemetry?.CapturedAtUtc, "Retained data must keep its original age.");
        Assert.AreEqual("suspended-last-known", suspendedTelemetry.HardwareState);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runtime.GetHomeStateAsync(cancelled.Token));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runtime.ReadTelemetryAsync(cancelled.Token));
        }

        await runtime.HandleLifecycleAsync(true, CancellationToken.None);
        Assert.IsFalse(provider.BackgroundWorkPaused, "Successful resume may restart background work.");
        provider.LiveTelemetry = liveTelemetry with { CapturedAtUtc = liveTelemetry.CapturedAtUtc.AddSeconds(1) };
        var resumed = await runtime.GetHomeStateAsync(CancellationToken.None);
        Assert.AreEqual(2, provider.ReadTelemetryCount);
        Assert.AreEqual(provider.LiveTelemetry.CapturedAtUtc, resumed.Telemetry?.CapturedAtUtc);

        var coldProvider = new FakeProvider(AvailableState("monitoring"));
        var coldRuntime = new HomeServiceRuntime(coldProvider);
        await coldRuntime.HandleLifecycleAsync(false, CancellationToken.None);
        var cold = await coldRuntime.GetHomeStateAsync(CancellationToken.None);
        var unknown = await coldRuntime.ReadTelemetryAsync(CancellationToken.None);
        await coldRuntime.GetStateAsync(CancellationToken.None);
        await coldRuntime.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(0, coldProvider.DiagnoseCount, "A suspended cold host must not begin hardware discovery.");
        Assert.AreEqual(0, coldProvider.ReadTelemetryCount);
        Assert.IsNull(cold.Telemetry);
        Assert.AreEqual("suspended-unavailable", unknown.HardwareState);
        Assert.AreEqual(DateTimeOffset.MinValue, unknown.CapturedAtUtc);

        var pendingSensor = new TaskCompletionSource<HardwareSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raceProvider = new FakeProvider(AvailableState("monitoring") with { Telemetry = diagnosisTelemetry })
            { PendingTelemetry = pendingSensor.Task };
        var raceRuntime = new HomeServiceRuntime(raceProvider);
        var inFlightRead = raceRuntime.GetHomeStateAsync(CancellationToken.None);
        Assert.AreEqual(1, raceProvider.ReadTelemetryCount);
        await raceRuntime.HandleLifecycleAsync(false, CancellationToken.None);
        pendingSensor.SetResult(liveTelemetry);
        var pausedDuringRead = await inFlightRead;
        Assert.AreEqual(0, raceProvider.ReadControlsCount, "A read already in flight must not start the next native read after suspend.");
        Assert.AreEqual(diagnosisTelemetry.CapturedAtUtc, pausedDuringRead.Telemetry?.CapturedAtUtc);

        var sampledProvider = new FakeProvider(AvailableState("performanceMode"));
        var sampledRuntime = new HomeServiceRuntime(sampledProvider, new RecordingDiagnostics());
        await sampledRuntime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), CancellationToken.None);
        int evidenceReadsBeforeSuspend = sampledProvider.ReadModeEvidenceCount;
        await sampledRuntime.HandleLifecycleAsync(false, CancellationToken.None);
        await Task.Delay(400);
        Assert.AreEqual(evidenceReadsBeforeSuspend, sampledProvider.ReadModeEvidenceCount, "Delayed mode evidence must not poll suspended hardware.");
        await sampledRuntime.HandleLifecycleAsync(true, CancellationToken.None);
        await sampledRuntime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None);
        int evidenceReadsBeforeFastResume = sampledProvider.ReadModeEvidenceCount;
        await sampledRuntime.HandleLifecycleAsync(false, CancellationToken.None);
        await sampledRuntime.HandleLifecycleAsync(true, CancellationToken.None);
        await Task.Delay(400);
        Assert.AreEqual(evidenceReadsBeforeFastResume, sampledProvider.ReadModeEvidenceCount, "Resume must not revive sampling from an older hardware epoch.");

        var delayedInitialization = new TaskCompletionSource<HomeHardwareState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedProvider = new FakeProvider(AvailableState("monitoring")) { PendingReinitialize = delayedInitialization.Task };
        var delayedRuntime = new HomeServiceRuntime(delayedProvider);
        await delayedRuntime.InitializeAsync(CancellationToken.None);
        await delayedRuntime.HandleLifecycleAsync(false, CancellationToken.None);
        var resuming = delayedRuntime.HandleLifecycleAsync(true, CancellationToken.None);
        Assert.IsTrue(delayedRuntime.IsSuspended);
        Assert.IsTrue(delayedProvider.BackgroundWorkPaused);
        delayedInitialization.SetException(new IOException("Simulated resume discovery failure"));
        await Assert.ThrowsExactlyAsync<IOException>(() => resuming);
        Assert.IsTrue(delayedRuntime.IsSuspended, "Failed reinitialization must retain the pause.");
        Assert.IsTrue(delayedProvider.BackgroundWorkPaused);
        var failedDiscovery = new FakeProvider(AvailableState("monitoring")) { ReinitializeSucceeds = false };
        var failedRuntime = new HomeServiceRuntime(failedDiscovery);
        await failedRuntime.InitializeAsync(CancellationToken.None);
        await failedRuntime.HandleLifecycleAsync(false, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() => failedRuntime.HandleLifecycleAsync(true, CancellationToken.None));
        Assert.IsTrue(failedRuntime.IsSuspended, "A returned hardwareUnavailable state is not a successful resume.");
        Assert.IsTrue(failedDiscovery.BackgroundWorkPaused);
        var quickProvider = new FakeProvider(AvailableState("quickSetting:fnLock"));
        var quickRuntime = new HomeServiceRuntime(quickProvider);
        await quickRuntime.InitializeAsync(CancellationToken.None);
        await quickRuntime.HandleLifecycleAsync(false, CancellationToken.None);
        var blockedQuick = await quickRuntime.ExecuteAsync(new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.FnLock, true), CancellationToken.None);
        Assert.AreEqual(CommandState.Rejected, blockedQuick.State, "Unmanaged quick settings must also stop during suspend.");
        Assert.AreEqual(HardwareMutation.NoMutation, blockedQuick.Mutation);
        Assert.AreEqual(0, quickProvider.ExecuteCount);
    }

    [TestMethod]
    public async Task Suspend_callback_pauses_readers_and_background_work_before_consumer_runs()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var provider = new FakeProvider(AvailableState("monitoring"));
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(deadline.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handledVersions = new List<long>();
        var notifications = new SystemPowerNotifications(async (resume, version, token) =>
        {
            entered.TrySetResult();
            await unblock.Task.WaitAsync(token);
            await runtime.HandleLifecycleAsync(resume, token, version);
            handledVersions.Add(version);
        }, NullLogger.Instance, deadline.Token, registerNative: false, prepare: runtime.PreparePowerTransition);
        try
        {
            NotifyPower(notifications, 4);
            await entered.Task.WaitAsync(deadline.Token);
            NotifyPower(notifications, 4);
            Assert.IsTrue(runtime.IsSuspended, "The OS callback must pause before a blocked consumer gets its turn.");
            Assert.IsTrue(provider.BackgroundWorkPaused);
            int telemetryReads = provider.ReadTelemetryCount;
            await runtime.GetHomeStateAsync(deadline.Token);
            Assert.AreEqual(telemetryReads, provider.ReadTelemetryCount);
        }
        finally { unblock.TrySetResult(); await notifications.DisposeAsync(); }
        Assert.AreEqual(2, handledVersions.Count, "A new suspend version must not be collapsed into an older direction.");
        Assert.AreNotEqual(handledVersions[0], handledVersions[1]);
    }

    [TestMethod]
    public async Task Earlier_resume_completion_cannot_unpause_a_newer_suspend()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var initialized = AvailableState("monitoring");
        var discovery = new TaskCompletionSource<HomeHardwareState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(initialized) { PendingReinitialize = discovery.Task };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(deadline.Token);
        var priorState = runtime.State;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pausedAfterOldResume = false, backgroundPausedAfterOldResume = false;
        var notifications = new SystemPowerNotifications(async (resume, version, token) =>
        {
            var transition = runtime.HandleLifecycleAsync(resume, token, version);
            if (resume) entered.TrySetResult();
            await transition;
            if (resume) { pausedAfterOldResume = runtime.IsSuspended; backgroundPausedAfterOldResume = provider.BackgroundWorkPaused; }
        }, NullLogger.Instance, deadline.Token, registerNative: false, prepare: runtime.PreparePowerTransition);
        try
        {
            NotifyPower(notifications, 4);
            NotifyPower(notifications, 18);
            await entered.Task.WaitAsync(deadline.Token);
            NotifyPower(notifications, 4);
        }
        finally { discovery.TrySetResult(AvailableState("cpuTuning")); await notifications.DisposeAsync(); }
        Assert.IsTrue(pausedAfterOldResume, "An older discovery result must not release the newer suspend.");
        Assert.IsTrue(backgroundPausedAfterOldResume, "Timers must observe the same atomic lifecycle state.");
        Assert.IsTrue(runtime.IsSuspended);
        Assert.AreSame(priorState, runtime.State, "The superseded diagnosis must not replace the current capability state.");
    }

    [TestMethod]
    public async Task Queued_resume_superseded_by_suspend_does_not_start_discovery()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var provider = new FakeProvider(AvailableState("monitoring"));
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(deadline.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new SystemPowerNotifications(async (resume, version, token) =>
        {
            await runtime.HandleLifecycleAsync(resume, token, version);
            if (!entered.Task.IsCompleted) { entered.TrySetResult(); await unblock.Task.WaitAsync(token); }
        }, NullLogger.Instance, deadline.Token, registerNative: false, prepare: runtime.PreparePowerTransition);
        try
        {
            NotifyPower(notifications, 4);
            await entered.Task.WaitAsync(deadline.Token);
            NotifyPower(notifications, 18);
            NotifyPower(notifications, 4);
        }
        finally { unblock.TrySetResult(); await notifications.DisposeAsync(); }
        Assert.AreEqual(0, provider.ReinitializeCount, "A stale queued resume must not begin hardware discovery.");
        Assert.IsTrue(runtime.IsSuspended);
        Assert.IsTrue(provider.BackgroundWorkPaused);
    }

    private static void NotifyPower(SystemPowerNotifications notifications, uint type) =>
        ((Delegate)typeof(SystemPowerNotifications).GetField("callback", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(notifications)!).DynamicInvoke(IntPtr.Zero, type, IntPtr.Zero);

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
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(manual with { OperationId = Guid.NewGuid(), PreserveStrongCooling = true }, CancellationToken.None, follower)).State);
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
        await store.SetConfigurationAsync(configuration with { Enabled = true }, CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cycle = bridge.RunAutomationCycleAsync(async token =>
        {
            await release.Task.WaitAsync(token);
            var result = await bridge.ExecuteAutomaticCommandAsync(runtime, new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), token);
            Assert.AreEqual(CommandState.Rejected, result.State, "A queued manual request invalidates the unfinished automatic cycle.");
        }, CancellationToken.None);
        try
        {
            var disable = bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration), CancellationToken.None);
            var manual = bridge.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), CancellationToken.None);
            Assert.IsFalse(disable.IsCompleted);
            Assert.IsFalse(manual.IsCompleted);
            Assert.IsFalse((await store.ReadAsync(CancellationToken.None)).Configuration!.Enabled);
            Assert.AreEqual(0, provider.ExecuteCount);
            using var cancelled = new CancellationTokenSource();
            var interrupted = bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration), cancelled.Token);
            cancelled.Cancel();
            try { await interrupted; Assert.Fail("Cancelled handoff should not acquire the cycle gate"); }
            catch (OperationCanceledException) { }
            release.SetResult();
            await Task.WhenAll(cycle, disable, manual);
            Assert.AreEqual(1, provider.ExecuteCount);
            Assert.IsNotNull((await store.ReadAsync(CancellationToken.None)).ManualOverrideUntilUtc);
            await bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration with { Enabled = true }), CancellationToken.None);
            Assert.IsNull((await store.ReadAsync(CancellationToken.None)).ManualOverrideUntilUtc);
            await store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow, true, CancellationToken.None);
            Assert.IsFalse((await store.ReadAsync(CancellationToken.None)).Configuration!.Enabled,
                "Manual takeover must persistently disable automation, without a dwell timer reopening it.");
            long disabledRevision = (await store.ReadAsync(CancellationToken.None)).Revision;
            await store.SetConfigurationAsync(configuration with { Enabled = true }, CancellationToken.None,
                disabledRevision, AutomationConfigurationIntent.Update);
            Assert.IsFalse((await store.ReadAsync(CancellationToken.None)).Configuration!.Enabled,
                "Publishing edited snapshots must not enable a manually disabled strategy.");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SetConfigurationAsync(configuration,
                CancellationToken.None, disabledRevision, AutomationConfigurationIntent.Enable));
            await bridge.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration with { Enabled = true }), CancellationToken.None);
            Assert.IsNull((await store.ReadAsync(CancellationToken.None)).ManualOverrideUntilUtc);
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

    [TestMethod]
    public async Task Adaptive_downshifts_with_curves_strong_cooling_or_missing_client_context()
    {
        foreach (int scenario in Enumerable.Range(0, 8))
        {
            string root = Path.Combine(Path.GetTempPath(), "xisura-adaptive77-" + Guid.NewGuid().ToString("N"));
            var store = new AdaptiveAutomationStateStore();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
            typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store,
                new Jiaolong.Service.Storage.AtomicJsonStore(root, "adaptive.json"));
            try
            {
                var office = PresetKey.Create(ControlModeId.Office, 2);
                var game = PresetKey.Create(ControlModeId.Gaming, 2);
                var turbo = PresetKey.Create(ControlModeId.Turbo, 2);
                var rules = scenario is 3 or 7 ? new[] { new AdaptiveAutomationApplicationRule("private-game.exe", false, AdaptiveAutomationStageId.Turbo) } : [];
                await store.SetConfigurationAsync(new(true, AdaptiveAutomationStrategyId.BalancedAdaptive,
                    new(40, 35, 1, true, 85, 90, 1, 20, 15, 1, 20, 30, true, 85, 80, 1, 1, 1, scenario is 3 or 7, 30, rules) { TurboMinimumDwellSeconds = 1 },
                    new(office, game, turbo, office, game),
                    [new(office, PerformanceMode.Quiet, null), new(game, PerformanceMode.Balanced, null), new(turbo, PerformanceMode.Turbo, null)]),
                    CancellationToken.None, ownerSid: "user-a");
                var controls = new HomeControlState(scenario >= 4 ? PerformanceMode.Quiet : PerformanceMode.Turbo, scenario is 1 or 4 or 5, [])
                { FanEcControl = new(DateTimeOffset.UtcNow, 0, 4, 35, 35, 0), FanAutomaticCeilingActive = scenario == 2 };
                var ready = AvailableState("performanceMode");
                ready = ready with { Capabilities = ready.Capabilities with
                    { Items = ready.Capabilities.Items.Append(new CapabilityDescriptor("fanControl", CapabilityState.Available, null)).ToArray() } };
                var provider = new FakeProvider(ready with { Capabilities = ready.Capabilities with { SupportState = DeviceSupportState.Ready } })
                { LiveControls = controls, FollowModeWrites = true };
                var bridge = new AdaptiveAutomationHardwareProvider(provider, store);
                var journal = new ControlExecutionJournal(root);
                if (scenario == 6)
                    await journal.SaveAsync(new(1, 0, 0, ControlModeId.Office, PerformanceMode.Quiet, [], new())
                        { NativeModeApplyState = ApplyState.Unknown }, ready.Identity, Guid.NewGuid(), true, CancellationToken.None);
                var runtime = new HomeServiceRuntime(bridge, journal: journal);
                await runtime.InitializeAsync(CancellationToken.None);
                var start = DateTimeOffset.UtcNow.AddSeconds(-2);
                var clock = new AdaptiveTestTimeProvider(start);
                var worker = new AdaptiveAutomationWorker(store, runtime, bridge,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveAutomationWorker>.Instance, clock);
                var tick = typeof(AdaptiveAutomationWorker).GetMethod("TickAsync", flags)!;
                for (int second = 0; second <= (scenario >= 4 ? 3 : 2); second++)
                {
                    clock.Now = start.AddSeconds(second);
                    provider.LiveTelemetry = new HardwareSnapshot(start.AddSeconds(second), "connected",
                        scenario is 4 or 5 ? 99 : scenario >= 6 ? 65 : 92, scenario == 2 ? null : scenario is 4 or 5 ? 90 : scenario >= 6 ? 60 : 82, 8, 0)
                    { CpuUsagePercent = scenario >= 6 ? 65 : 3, GpuUsagePercent = scenario >= 6 ? 65 : 0,
                      AcPowerConnected = scenario == 5 ? null : true, BatteryPercent = 95, BatterySaver = false };
                    await bridge.RunAutomationCycleAsync(token => (Task)tick.Invoke(worker, [token])!, CancellationToken.None);
                    if (scenario is 4 or 5) typeof(AdaptiveAutomationWorker).GetField("configurationChanged", flags)!.SetValue(worker, false);
                }
                if (scenario >= 4)
                {
                    var final = (await store.ReadAsync(CancellationToken.None)).Status!;
                    Assert.AreEqual(scenario == 6 ? 0 : 1, provider.ExecuteCount, final.Reason);
                    if (scenario is 4 or 5) Assert.IsFalse(provider.LiveControls.StrongCooling, "Emergency EC release cannot depend on target or known power.");
                    if (scenario == 6) { Assert.IsNull(final.SubmittedTarget); StringAssert.Contains(final.Reason!, "未知"); }
                    if (scenario == 7) Assert.AreEqual(game, final.SubmittedTarget, "Stale application context falls back to fresh service load.");
                    string exported = System.Text.Json.JsonSerializer.Serialize(await store.ReadDiagnosticsAsync(CancellationToken.None));
                    Assert.IsFalse(exported.Contains("private-game.exe", StringComparison.OrdinalIgnoreCase));
                    using var otherUser = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
                        await store.ReadDiagnosticsAsync(CancellationToken.None, "user-b")));
                    Assert.AreEqual(System.Text.Json.JsonValueKind.Null, otherUser.RootElement.GetProperty("configuration").ValueKind);
                    using var owningUser = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
                        await store.ReadDiagnosticsAsync(CancellationToken.None, "user-a")));
                    Assert.AreEqual(System.Text.Json.JsonValueKind.Object, owningUser.RootElement.GetProperty("configuration").ValueKind);
                    continue;
                }
                Assert.AreEqual(PerformanceMode.Quiet, provider.LiveControls.PerformanceMode,
                    $"scenario {scenario}: {(await store.ReadAsync(CancellationToken.None)).Status?.Reason}");
                var status = (await store.ReadAsync(CancellationToken.None)).Status!;
                Assert.AreEqual(office, status.SubmittedTarget);
                Assert.IsNull(status.CurrentTarget, "Transport acceptance alone never confirms the actual native mode.");
                Assert.AreEqual(ApplyState.SubmittedUnverified, status.TargetApplyState);
                Assert.AreEqual(1, provider.ExecuteCount);
                Assert.AreEqual(controls.FanEcControl, provider.LiveControls.FanEcControl);
                Assert.AreEqual(controls.StrongCooling, provider.LiveControls.StrongCooling);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    private sealed class AdaptiveTestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class FakeProvider(HomeHardwareState state) : IHomeHardwareProvider
    {
        public HomeHardwareState State { get; private set; } = state;
        public int ReinitializeCount { get; private set; }
        public int DiagnoseCount { get; private set; }
        public int ReadControlsCount { get; private set; }
        public int ReadTelemetryCount { get; private set; }
        private bool backgroundWorkPaused;
        private PowerLifecycleState? lifecycle;
        public bool BackgroundWorkPaused => backgroundWorkPaused || lifecycle?.IsPaused == true;
        public void SetBackgroundWorkPaused(bool paused) => backgroundWorkPaused = paused;
        public void SetBackgroundWorkState(PowerLifecycleState value) => lifecycle = value;
        public Task<HomeHardwareState>? PendingReinitialize { get; init; }
        public int ExecuteCount { get; private set; }
        public HardwareCommand? LastCommand { get; private set; }
        public CommandState ResultState { get; set; } = CommandState.Applied;
        public HardwareMutation Mutation { get; init; } = HardwareMutation.Unknown;
        public PerformanceModeEvidence? ReplyEvidence { get; init; }
        public PerformanceModeEvidence? ObservedEvidence { get; init; }
        public ServiceError? ReplyError { get; init; }
        public bool ReinitializeSucceeds { get; init; } = true;
        public HomeControlState LiveControls { get; set; } = state.Controls;
        public HardwareSnapshot? LiveTelemetry { get; set; }
        public PerformanceMode? VerifiedMode { get; init; }
        public bool FollowModeWrites { get; init; }
        public Task<HardwareSnapshot>? PendingTelemetry { get; init; }

        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken)
        {
            DiagnoseCount++;
            return Task.FromResult(State);
        }

        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken)
        {
            ReinitializeCount++;
            if (PendingReinitialize is not null) return PendingReinitialize;
            if (!ReinitializeSucceeds)
            {
                State = State with { SupportState = DeviceSupportState.RepairRequired, Reason = "hardwareUnavailable" };
            }

            return Task.FromResult(State);
        }

        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken)
        {
            ReadTelemetryCount++;
            if (PendingTelemetry is not null) return PendingTelemetry;
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
            if (FollowModeWrites && command is SetPerformanceModeCommand mode)
                LiveControls = LiveControls with { PerformanceMode = mode.Mode };
            if (FollowModeWrites && command is ReleaseFanControlCommand)
                LiveControls = LiveControls with { StrongCooling = false, ActiveFanControlPlan = null };
            return Task.FromResult(new CommandResult(command.OperationId, ResultState, State.Telemetry, RequiredUserAction.None, ReplyError, false)
                { VerifiedPerformanceMode = VerifiedMode, Mutation = Mutation, ModeEvidence = ReplyEvidence });
        }
        private int evidenceReadCount;
        public int ReadModeEvidenceCount => Volatile.Read(ref evidenceReadCount);
        public Task<PerformanceModeEvidence?> ReadModeEvidenceAsync(CancellationToken token) =>
            Task.FromResult(Interlocked.Increment(ref evidenceReadCount) == 1 ? ReplyEvidence : ObservedEvidence);
    }
}
