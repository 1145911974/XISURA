using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong.Contracts.Protocol;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class HomeControlSessionTests
{
    [TestMethod]
    public async Task Failed_control_read_recovers_after_startup_restore_is_superseded()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string pipeName = "Jiaolong-recovery-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var recovered = new TaskCompletionSource<HomeStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        int controlReads = 0;
        int commands = 0;
        var expected = CreateState(PerformanceMode.Balanced);
        expected = expected with { Capabilities = expected.Capabilities with { SupportState = DeviceSupportState.ReadOnly } };

        async Task<MessageEnvelope> ReadAsync()
        {
            var prefix = new byte[4];
            await server.ReadExactlyAsync(prefix, deadline.Token);
            var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
            await server.ReadExactlyAsync(payload, deadline.Token);
            return JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.MessageEnvelope)!;
        }
        async Task WriteAsync(MessageEnvelope envelope)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.MessageEnvelope);
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
            await server.WriteAsync(prefix, deadline.Token);
            await server.WriteAsync(payload, deadline.Token);
        }
        var host = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync(deadline.Token);
                var hello = (HelloEnvelope)await ReadAsync();
                await WriteAsync(new HelloAckEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, hello.MessageId, "test"));
                while (!deadline.IsCancellationRequested)
                {
                    var request = (RequestEnvelope)await ReadAsync();
                    if (request.Operation == "subscribeTelemetry") continue;
                    if (request.Operation != "getDeviceState") { Interlocked.Increment(ref commands); continue; }
                    bool fail = Interlocked.Increment(ref controlReads) == 1;
                    await WriteAsync(new ResponseEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow,
                        request.MessageId, request.OperationId, fail ? ResponseStatus.Error : ResponseStatus.Success,
                        fail ? null : JsonSerializer.SerializeToElement(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        fail ? ServiceError.Create(ErrorCode.ServiceUnavailable, request.OperationId, false) : null));
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }
        }, deadline.Token);
        var restorePaths = new RecordingPathProvider();
        var restore = new AppliedConfigurationRestore(restorePaths);
        var savedMode = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced);
        await restore.RecordAppliedAsync(savedMode, new(savedMode.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false), CancellationToken.None);
        try
        {
            await using var session = new HomeControlSession(new ControlCenterClient(pipeName), new RecordingRadioController(), restore);
            Assert.IsFalse(session.ConfigurationRestorationSettled);
            var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ConfigurationRestorationCompleted += () => settled.TrySetResult(session.ConfigurationRestorationSettled);
            session.StateChanged += snapshot => recovered.TrySetResult(snapshot);
            await session.StartAsync(deadline.Token);
            Assert.IsFalse(session.ConfigurationRestorationSettled);
            session.SupersedeAutomaticRestore();
            Assert.AreEqual(HomeSessionStatus.Disconnected, session.Status);
            var actual = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(expected.Controls.PerformanceMode, actual.Controls.PerformanceMode);
            Assert.AreEqual(HomeSessionStatus.ReadOnly, session.Status);
            Assert.IsTrue(controlReads >= 2);
            Assert.AreEqual(0, commands);
            while (!session.ConfigurationRestorationSettled) await Task.Delay(20, deadline.Token);
            Assert.IsTrue(session.ConfigurationRestorationSettled);
            Assert.IsTrue(await settled.Task.WaitAsync(deadline.Token));
            Assert.AreEqual(0, commands);
        }
        finally
        {
            deadline.Cancel(); await host;
            var root = Path.GetFullPath(restorePaths.LocalAppDataRoot);
            Assert.IsTrue(root.StartsWith(Path.Combine(Path.GetTempPath(), "JiaolongTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Control_state_refresh_is_fast_enough_for_hardware_hotkeys()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), HomeControlSession.ControlStateRefreshInterval);
    }

    [TestMethod]
    public void Equivalent_state_snapshots_do_not_trigger_a_second_visual_refresh()
    {
        var first = CreateState(PerformanceMode.Balanced);
        Assert.IsFalse(HomeStateEquivalence.AreEquivalent(first, first with { Controls = first.Controls with { KeyboardLightingNativeCycleAvailable = true } }));
        var second = CreateState(PerformanceMode.Balanced);

        Assert.IsTrue(HomeStateEquivalence.AreEquivalent(first, second));
        Assert.IsFalse(HomeStateEquivalence.AreEquivalent(first, CreateState(PerformanceMode.Quiet)));
    }

    [TestMethod]
    public void Telemetry_only_changes_do_not_repaint_control_state_but_mode_and_power_changes_do()
    {
        var baseline = CreateState(PerformanceMode.Balanced) with
        {
            Telemetry = new HardwareSnapshot(DateTimeOffset.UtcNow, "Connected", 65, 50, 35, 20) { AcPowerConnected = true }
        };
        var sample = baseline with { Telemetry = baseline.Telemetry! with { CpuTemperatureC = 66, CpuFrequencyMhz = 3200, GpuUsagePercent = 8 } };
        Assert.IsTrue(HomeStateEquivalence.HaveSameControls(baseline, sample));
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(baseline, sample with { Controls = sample.Controls with { PerformanceMode = PerformanceMode.Quiet } }));
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(baseline, sample with { Telemetry = sample.Telemetry! with { AcPowerConnected = false } }));
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(baseline, sample with { Telemetry = sample.Telemetry! with { HardwareState = "Unavailable" } }));
    }

    [TestMethod]
    public void Gpu_vf_readback_change_refreshes_the_curve()
    {
        var first = CreateState(PerformanceMode.Balanced);
        var before = first with { Controls = first.Controls with { GpuVf = new GpuVfState([new GpuVfNode(750, 1800, 75_000)], -200_000, 200_000, null) } };
        var same = first with { Controls = first.Controls with { GpuVf = new GpuVfState([new GpuVfNode(750, 1800, 75_000)], -200_000, 200_000, null) } };
        var changed = first with { Controls = first.Controls with { GpuVf = new GpuVfState([new GpuVfNode(750, 1800, 90_000)], -200_000, 200_000, null) } };
        Assert.IsTrue(HomeStateEquivalence.AreEquivalent(before, same));
        Assert.IsFalse(HomeStateEquivalence.AreEquivalent(before, changed));
    }

    [TestMethod]
    public void Read_only_message_is_compact()
    {
        StringAssert.Contains(HomeSessionMessages.ReadOnlyMode, "安全只读模式");
        Assert.AreEqual(9, HomeSessionMessages.ReadOnlyMode.Length);
    }

    [TestMethod]
    public async Task Wifi_toggle_uses_interactive_radio_controller()
    {
        var radio = new RecordingRadioController();
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(QuickSettingKind.Wifi, radio.LastSetting);
        Assert.IsTrue(radio.LastValue);
    }

    [TestMethod]
    public async Task Bluetooth_toggle_uses_interactive_radio_controller()
    {
        var radio = new RecordingRadioController();
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Bluetooth, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(QuickSettingKind.Bluetooth, radio.LastSetting);
        Assert.IsTrue(radio.LastValue);
    }

    [TestMethod]
    public async Task Radio_readback_mismatch_restores_and_verifies_previous_state()
    {
        var radio = new MismatchedRadioController(restoreSucceeds: true);
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(ErrorCode.ReadBackMismatch, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { true, false }, radio.Writes);
        Assert.IsFalse(radio.WifiEnabled);
    }

    [TestMethod]
    public async Task Radio_restore_mismatch_reports_recovery_required()
    {
        var radio = new MismatchedRadioController(restoreSucceeds: false);
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(ErrorCode.RollbackFailed, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { true, false }, radio.Writes);
    }

    private static HomeStateSnapshot CreateState(PerformanceMode mode) => new(
        new CapabilitySnapshot([
            new CapabilityDescriptor("performanceMode", CapabilityState.Available, null),
            new CapabilityDescriptor("monitoring", CapabilityState.Available, null)
        ])
        {
            Identity = new HardwareIdentity("board", "bios", "cpu", "gpu"),
            SupportState = DeviceSupportState.Ready,
            Reason = null
        },
        null,
        new HomeControlState(mode, false, [
            new QuickSettingStatus(QuickSettingKind.Wifi, true, null),
            new QuickSettingStatus(QuickSettingKind.Bluetooth, false, null)
        ]));

    private sealed class RecordingRadioController : IInteractiveRadioController
    {
        private bool wifiEnabled = true;
        private bool bluetoothEnabled;
        public QuickSettingKind LastSetting { get; private set; }
        public bool LastValue { get; private set; }

        public Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HomeRadioSnapshot(wifiEnabled, bluetoothEnabled, null));

        public Task<bool> SetAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken)
        {
            LastSetting = setting;
            LastValue = enabled;
            if (setting == QuickSettingKind.Wifi) wifiEnabled = enabled;
            if (setting == QuickSettingKind.Bluetooth) bluetoothEnabled = enabled;
            return Task.FromResult(true);
        }
    }

    private sealed class MismatchedRadioController(bool restoreSucceeds) : IInteractiveRadioController
    {
        public bool WifiEnabled { get; private set; }
        public List<bool> Writes { get; } = [];

        public Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HomeRadioSnapshot(WifiEnabled, false, null));

        public Task<bool> SetAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken)
        {
            Writes.Add(enabled);
            WifiEnabled = enabled ? false : !restoreSucceeds;
            return Task.FromResult(true);
        }
    }
}
