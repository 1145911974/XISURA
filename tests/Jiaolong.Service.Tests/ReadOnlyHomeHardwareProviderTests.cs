using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ReadOnlyHomeHardwareProviderTests
{
    [TestMethod]
    public async Task Read_host_preserves_live_values_and_masks_all_write_capabilities()
    {
        var physical = new Provider();
        var guarded = new ReadOnlyHomeHardwareProvider(physical);
        var state = await guarded.DiagnoseAsync(CancellationToken.None);
        Assert.AreEqual(DeviceSupportState.ReadOnly, state.SupportState);
        Assert.AreEqual(DeviceSupportState.ReadOnly, state.Capabilities.SupportState);
        Assert.AreEqual(CapabilityState.ReadOnly, state.Capabilities.Items.Single().State);
        Assert.IsFalse(state.Capabilities.Fields.Single().Writable);
        Assert.AreEqual(physical.State.Identity, state.Identity);
        Assert.AreSame(physical.State.Controls, await guarded.ReadControlBaselineAsync(CancellationToken.None, true));
        Assert.IsTrue(physical.AdvancedRead);
        await guarded.ReinitializeAsync(CancellationToken.None);
        Assert.AreEqual(0, physical.Writes);
        Assert.AreEqual(DeviceSupportState.Ready, physical.State.SupportState);
        Assert.IsTrue(physical.State.Capabilities.Fields.Single().Writable);
    }

    [TestMethod]
    public async Task Direct_commands_and_internal_cpu_restore_never_reach_physical_writer()
    {
        var physical = new Provider();
        var guarded = new ReadOnlyHomeHardwareProvider(physical);
        HardwareCommand[] commands =
        [ new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet),
          new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 15000, true),
          new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.SessionEnded),
          new SetKeyboardLightingCommand(Guid.NewGuid(), new("Fixed", 100)) ];
        foreach (var command in commands)
        {
            var result = await guarded.ExecuteAsync(command, CancellationToken.None);
            Assert.AreEqual(CommandState.Rejected, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(ErrorCode.ReadOnlySafeMode, result.Error!.Code);
        }
        var restored = await guarded.RestoreCpuFieldAsync(Guid.NewGuid(), "negativeCurveOptimizer", new(null, null, new()), new(null, null, new()), CancellationToken.None);
        Assert.AreEqual(CommandState.Rejected, restored.State);
        var windows = await guarded.RestoreWindowsPowerSettingAsync(Guid.NewGuid(), Guid.NewGuid(), WindowsCpuSetting.AcBoostMode, 2, 1, CancellationToken.None);
        Assert.AreEqual(ErrorCode.ReadOnlySafeMode, windows.Error!.Code);
        Assert.AreEqual(HardwareMutation.NoMutation, windows.Mutation);
        Assert.IsFalse(await guarded.ReconcileGpuRecoveryAsync(CancellationToken.None));
        Assert.AreEqual(0, physical.Reconciles);
        Assert.AreEqual(HardwareMutation.NoMutation, (await guarded.RecoverPendingGpuAsync(Guid.NewGuid(), CancellationToken.None)).Mutation);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => guarded.ExecuteAsync(commands[0], cancelled.Token));
        Assert.AreEqual(0, physical.Writes);
    }

    [TestMethod]
    public async Task Runtime_session_and_write_requests_cannot_activate_a_read_only_host()
    {
        string root = Path.Combine(Path.GetTempPath(), "XisuraReadOnlyHostTests", Guid.NewGuid().ToString("N"));
        try
        {
            var physical = new Provider();
            var runtime = new HomeServiceRuntime(new ReadOnlyHomeHardwareProvider(physical), journal: new(root), requireClientSessions: true);
            var identity = ClientIdentity.LocalAdministrator("S-1-5-21-test", 1) with
                { ProcessId = 42, ProcessCreationFileTimeUtc = DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc() };
            Assert.AreEqual(CommandState.Rejected, (await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), identity, CancellationToken.None)).State);
            Assert.AreEqual(CommandState.Rejected, (await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo),
                CancellationToken.None, identity.ConnectionId, identity.UserSid, identity)).State);
            Assert.AreEqual(PerformanceMode.Balanced, (await runtime.GetHomeStateAsync(CancellationToken.None)).Controls.PerformanceMode);
            Assert.AreEqual(0, physical.Writes);
            Assert.IsFalse(File.Exists(Path.Combine(root, "client-control-session.json")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "control-execution.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Adaptive_bridge_keeps_explicit_scheme_and_field_without_using_active_scheme_values()
    {
        var physical = new Provider();
        var bridge = new AdaptiveAutomationHardwareProvider(physical, new AdaptiveAutomationStateStore());
        Guid scheme = Guid.NewGuid();
        var snapshot = await bridge.ReadWindowsPowerSchemeAsync(scheme, CancellationToken.None);
        Assert.AreEqual(scheme, snapshot!.ActiveSchemeId);
        var result = await bridge.RestoreWindowsPowerSettingAsync(Guid.NewGuid(), scheme, WindowsCpuSetting.DcBoostMode, 4, 3, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual((scheme, WindowsCpuSetting.DcBoostMode, 4, 3), physical.ScopedRestore);
        Assert.AreEqual(1, physical.Writes);
        Assert.IsTrue(await bridge.ReconcileGpuRecoveryAsync(CancellationToken.None));
        physical.RecoveryReady = false;
        Assert.IsFalse(await bridge.ReconcileGpuRecoveryAsync(CancellationToken.None));
        Assert.AreEqual(2, physical.Reconciles);
        Guid recovery = Guid.NewGuid();
        Assert.AreEqual(CommandState.Applied, (await bridge.RecoverPendingGpuAsync(recovery, CancellationToken.None)).State);
        Assert.AreEqual(recovery, physical.RecoveryOperationId);
    }

    private sealed class Provider : IHomeHardwareProvider
    {
        public int Writes { get; private set; }
        public bool AdvancedRead { get; private set; }
        public bool RecoveryReady = true;
        public int Reconciles { get; private set; }
        public Guid? RecoveryOperationId { get; private set; }
        public Task<CommandResult> RecoverPendingGpuAsync(Guid id, CancellationToken token)
        { Writes++; RecoveryOperationId = id; return Task.FromResult(new CommandResult(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed }); }
        public Task<bool> ReconcileGpuRecoveryAsync(CancellationToken token) { Reconciles++; return Task.FromResult(RecoveryReady); }
        public (Guid, WindowsCpuSetting, int, int)? ScopedRestore { get; private set; }
        public HomeHardwareState State { get; } = new(new([new("performanceMode", CapabilityState.Available, null)])
        { Fields = [new("test", CapabilityState.Available, true, true, null, null, null, "", "test", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)] },
            DeviceSupportState.Ready, null, new("board", "bios", "cpu", "gpu"), null)
            { Controls = new(PerformanceMode.Balanced, false, []) };
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(State);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) { Writes++; return Task.FromResult(State); }
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(State.Controls);
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false)
        { AdvancedRead = includeCpuAdvanced; return ReadControlsAsync(token); }
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", null, null, null, null));
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        { Writes++; throw new InvalidOperationException("Physical write reached by read-only host"); }
        public Task<CommandResult> RestoreCpuFieldAsync(Guid id, string field, CpuFieldRestorationValue baseline, CpuFieldRestorationValue expected, CancellationToken token)
        { Writes++; throw new InvalidOperationException("Physical CPU restore reached by read-only host"); }
        public Task<WindowsPowerSettingsSnapshot?> ReadWindowsPowerSchemeAsync(Guid scheme, CancellationToken token) =>
            Task.FromResult<WindowsPowerSettingsSnapshot?>(new(scheme, 4700, 4700, 2, 4));
        public Task<CommandResult> RestoreWindowsPowerSettingAsync(Guid id, Guid scheme, WindowsCpuSetting setting, int expected, int target, CancellationToken token)
        {
            Writes++; ScopedRestore = (scheme, setting, expected, target);
            return Task.FromResult(new CommandResult(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed });
        }
    }
}
