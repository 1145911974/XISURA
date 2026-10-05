using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class TurboBranchControllerTests
{
    [TestMethod]
    public async Task Builtin_branch_uses_its_own_values_and_preserves_later_manual_side_controls()
    {
        var hardware = new Hardware();
        var controller = new TurboBranchController(() => hardware.State, hardware.Execute);
        await controller.ApplyAsync("Quiet", CancellationToken.None);
        Assert.AreEqual("Quiet", controller.ActiveTier);
        Assert.AreEqual(45, hardware.State.Controls.CpuTuning!.SplWatts);
        Assert.AreEqual(3500, hardware.State.Controls.ActiveFanControlPlan!.MaximumRpm);
        Assert.AreEqual(2100, hardware.State.Controls.GpuClockLimit!.SubmittedMhz);
        Assert.IsInstanceOfType<SetCpuTuningBatchCommand>(hardware.Commands[^1]);
        hardware.State = hardware.State with { Controls = hardware.State.Controls with {
            GpuClockLimit = hardware.State.Controls.GpuClockLimit with { SubmittedMhz = 2200 },
            ActiveFanControlPlan = new FanControlPlan([]) { Strategy = "Fixed", FixedRpm = 4200 } } };
        Assert.IsTrue(controller.InvalidateIfChanged(hardware.State));
        int writes = hardware.Commands.Count;
        await controller.ReleaseAsync(CancellationToken.None);
        Assert.AreEqual(writes, hardware.Commands.Count);
        Assert.AreEqual(2200, hardware.State.Controls.GpuClockLimit!.SubmittedMhz);
    }

    [TestMethod]
    public async Task Cpu_failure_restores_owned_side_controls_without_invalid_120W_user_restore()
    {
        var hardware = new Hardware { RejectCpu = true };
        var controller = new TurboBranchController(() => hardware.State, hardware.Execute);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => controller.ApplyAsync("Extreme", CancellationToken.None));
        Assert.IsNull(controller.ActiveTier);
        Assert.IsFalse(controller.HasOwnership);
        Assert.AreEqual(false, hardware.State.Controls.StrongCooling);
        Assert.AreEqual(2400, hardware.State.Controls.GpuClockLimit!.SubmittedMhz);
        Assert.AreEqual(120, hardware.State.Controls.CpuTuning!.SpptWatts);
        Assert.AreEqual(1, hardware.Commands.OfType<SetCpuTuningBatchCommand>().Count());
    }

    private sealed class Hardware
    {
        public bool RejectCpu { get; init; }
        public List<HardwareCommand> Commands { get; } = [];
        public HomeStateSnapshot State { get; set; } = new(
            new CapabilitySnapshot([new("fanControl", CapabilityState.Available, null), new("strongCooling", CapabilityState.Available, null), new("gpuFrequencyLimit", CapabilityState.Available, null)]),
            null,
            new HomeControlState(PerformanceMode.Balanced, false, []) {
                FanAutomaticCeilingAvailable = true,
                CpuTuning = new CpuTuningState(99, 60, 120, 4700, true, 8, Guid.NewGuid(), null, null) { AcMaxFrequencyMhz = 4700, DcMaxFrequencyMhz = 3300 },
                GpuClockLimit = new(210, 3105, 2400, true, null) });
        public Task<CommandResult> Execute(HardwareCommand command, CancellationToken token)
        {
            Commands.Add(command);
            var controls = State.Controls;
            switch (command)
            {
                case SetCpuTuningBatchCommand batch when RejectCpu:
                    return Task.FromResult(new CommandResult(command.OperationId, CommandState.RolledBack, null, RequiredUserAction.None,
                        ServiceError.Create(ErrorCode.ReadBackMismatch, command.OperationId, false), false));
                case SetCpuTuningBatchCommand batch:
                    var plan = batch.Plans[0];
                    controls = controls with { PerformanceMode = batch.NativeMode, CpuTuning = controls.CpuTuning! with {
                        TemperatureLimitC = plan.TemperatureLimitC, SplWatts = plan.SplWatts, SpptWatts = plan.SpptWatts,
                        AcMaxFrequencyMhz = plan.AcMaxFrequencyMhz, DcMaxFrequencyMhz = plan.DcMaxFrequencyMhz, BoostEnabled = plan.BoostEnabled } };
                    break;
                case SetGpuFrequencyLimitCommand gpu:
                    controls = controls with { GpuClockLimit = controls.GpuClockLimit! with { SubmittedMhz = gpu.Megahertz } };
                    break;
                case SetFanControlCommand fan:
                    controls = controls with { ActiveFanControlPlan = fan.Plan, StrongCooling = false };
                    break;
                case SetStrongCoolingCommand strong:
                    controls = controls with { StrongCooling = strong.Enabled, ActiveFanControlPlan = null };
                    break;
                case ReleaseFanControlCommand:
                    controls = controls with { StrongCooling = false, ActiveFanControlPlan = null };
                    break;
            }
            State = State with { Controls = controls };
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false));
        }
    }
}
