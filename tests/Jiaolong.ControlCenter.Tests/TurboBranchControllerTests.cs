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
        Assert.IsTrue(controller.HasOwnership);
        Assert.IsInstanceOfType<ApplyTurboBranchCommand>(hardware.Commands.Single());
        Assert.AreEqual(TurboBranchTier.Quiet, ((ApplyTurboBranchCommand)hardware.Commands[0]).Tier);
        Assert.AreEqual(2400, hardware.State.Controls.GpuClockLimit!.SubmittedMhz);
        Assert.IsNull(hardware.State.Controls.ActiveFanControlPlan);
        hardware.State = hardware.State with { Controls = hardware.State.Controls with { PerformanceMode = PerformanceMode.Quiet } };
        Assert.IsTrue(controller.InvalidateIfChanged(hardware.State));
        await controller.ReleaseAsync(CancellationToken.None);
        Assert.IsInstanceOfType<ReleaseTurboBranchCommand>(hardware.Commands[^1]);
        Assert.AreEqual(hardware.Commands[0].OperationId, ((ReleaseTurboBranchCommand)hardware.Commands[^1]).BranchOperationId);
        Assert.IsFalse(controller.HasOwnership);
        Assert.AreEqual(2400, hardware.State.Controls.GpuClockLimit!.SubmittedMhz);
        await controller.ApplyAsync("Extreme", CancellationToken.None);
        Assert.AreEqual(TurboBranchTier.Extreme, ((ApplyTurboBranchCommand)hardware.Commands[^1]).Tier);
    }

    [TestMethod]
    public async Task Cpu_failure_restores_owned_side_controls_without_invalid_120W_user_restore()
    {
        var hardware = new Hardware { RejectCpu = true };
        var controller = new TurboBranchController(() => hardware.State, hardware.Execute);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.ApplyAsync("Extreme", CancellationToken.None));
        Assert.IsNull(controller.ActiveTier);
        Assert.IsFalse(controller.HasOwnership);
        Assert.AreEqual(false, hardware.State.Controls.StrongCooling);
        Assert.AreEqual(2400, hardware.State.Controls.GpuClockLimit!.SubmittedMhz);
        Assert.AreEqual(120, hardware.State.Controls.CpuTuning!.SpptWatts);
        Assert.AreEqual(1, hardware.Commands.Count);
        hardware.RejectCpu = false;
        hardware.UnknownApply = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.ApplyAsync("Quiet", CancellationToken.None));
        Assert.IsTrue(controller.HasOwnership, "Lost/uncertain replies retain the owner token.");
        hardware.FailRelease = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.ReleaseAsync(CancellationToken.None));
        Assert.IsTrue(controller.HasOwnership, "Unconfirmed release retains the owner token for retry.");
        hardware.FailRelease = false;
        await controller.ReleaseAsync(CancellationToken.None);
        Assert.IsFalse(controller.HasOwnership);
        Assert.IsInstanceOfType<ReleaseTurboBranchCommand>(hardware.Commands[^1]);
    }

    private sealed class Hardware
    {
        public bool RejectCpu { get; set; }
        public bool UnknownApply { get; set; }
        public bool FailRelease { get; set; }
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
            if (command is ApplyTurboBranchCommand && (RejectCpu || UnknownApply))
                return Task.FromResult(new CommandResult(command.OperationId, UnknownApply ? CommandState.RecoveryRequired : CommandState.Rejected,
                    null, RequiredUserAction.None, ServiceError.Create(ErrorCode.ReadBackMismatch, command.OperationId, false), false)
                { Mutation = UnknownApply ? HardwareMutation.Unknown : HardwareMutation.NoMutation });
            if (command is ReleaseTurboBranchCommand && FailRelease)
                return Task.FromResult(new CommandResult(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                    ServiceError.Create(ErrorCode.ConflictDetected, command.OperationId, false), false) { Mutation = HardwareMutation.NoMutation });
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { Mutation = HardwareMutation.NoMutation });
        }
    }
}
