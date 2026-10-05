using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PrototypeModeCommandCoordinatorTests
{
    [TestMethod]
    public void Hardware_mode_can_be_mapped_back_to_the_home_mode()
    {
        Assert.AreEqual(PrototypePerformanceMode.Office, PrototypeModeCommandCoordinator.Map(PerformanceMode.Quiet));
        Assert.AreEqual(PrototypePerformanceMode.Gaming, PrototypeModeCommandCoordinator.Map(PerformanceMode.Balanced));
        Assert.AreEqual(PrototypePerformanceMode.Turbo, PrototypeModeCommandCoordinator.Map(PerformanceMode.Turbo));
        Assert.AreEqual(PrototypePerformanceMode.Custom, PrototypeModeCommandCoordinator.Map(PerformanceMode.Custom));
    }

    [TestMethod]
    public void Initial_visual_mode_is_not_treated_as_hardware_applied()
    {
        var state = new PrototypeAppliedModeState();

        Assert.IsTrue(state.ShouldApply(PrototypePerformanceMode.Office));
        state.Confirm(PrototypePerformanceMode.Office);
        Assert.IsFalse(state.ShouldApply(PrototypePerformanceMode.Office));
        Assert.IsTrue(state.ShouldApply(PrototypePerformanceMode.Gaming));
    }

    [TestMethod]
    public void Mode_reconciliation_ignores_stale_observation_until_latest_request_is_read_back()
    {
        var reconciliation = new PrototypeModeReconciliation();
        reconciliation.Request(PrototypePerformanceMode.Gaming);

        Assert.IsFalse(reconciliation.AcceptObservation(PrototypePerformanceMode.Turbo));
        Assert.AreEqual(PrototypePerformanceMode.Gaming, reconciliation.PendingMode);
        Assert.IsTrue(reconciliation.AcceptObservation(PrototypePerformanceMode.Gaming));
        Assert.IsNull(reconciliation.PendingMode);
    }

    [TestMethod]
    public void Older_command_failure_does_not_clear_a_newer_mode_request()
    {
        var reconciliation = new PrototypeModeReconciliation();
        var first = reconciliation.Request(PrototypePerformanceMode.Gaming);
        var latest = reconciliation.Request(PrototypePerformanceMode.Turbo);

        reconciliation.Reject(first);

        Assert.IsFalse(reconciliation.IsCurrent(first));
        Assert.IsTrue(reconciliation.IsCurrent(latest));
        Assert.AreEqual(PrototypePerformanceMode.Turbo, reconciliation.PendingMode);
    }

    [TestMethod]
    public void Homepage_modes_map_to_supported_contract_modes()
    {
        Assert.AreEqual(PerformanceMode.Quiet, PrototypeModeCommandCoordinator.Map(PrototypePerformanceMode.Office));
        Assert.AreEqual(PerformanceMode.Balanced, PrototypeModeCommandCoordinator.Map(PrototypePerformanceMode.Gaming));
        Assert.AreEqual(PerformanceMode.Turbo, PrototypeModeCommandCoordinator.Map(PrototypePerformanceMode.Turbo));
        Assert.AreEqual(PerformanceMode.Custom, PrototypeModeCommandCoordinator.Map(PrototypePerformanceMode.Custom));
    }

    [TestMethod]
    public async Task Applied_service_result_commits_the_requested_mode()
    {
        SetPerformanceModeCommand? sent = null;
        var coordinator = new PrototypeModeCommandCoordinator((command, _) =>
        {
            sent = command;
            return Task.FromResult(Result(CommandState.Applied));
        });

        var outcome = await coordinator.ApplyAsync(PrototypePerformanceMode.Gaming, CancellationToken.None);

        Assert.IsTrue(outcome.Applied);
        Assert.AreEqual(PrototypePerformanceMode.Gaming, outcome.RequestedMode);
        Assert.AreEqual(PerformanceMode.Balanced, sent!.Mode);
        Assert.AreNotEqual(Guid.Empty, sent.OperationId);
    }

    [TestMethod]
    public async Task Rejected_service_result_does_not_commit_the_requested_mode()
    {
        var coordinator = new PrototypeModeCommandCoordinator((_, _) =>
            Task.FromResult(Result(CommandState.Rejected)));

        var outcome = await coordinator.ApplyAsync(PrototypePerformanceMode.Turbo, CancellationToken.None);

        Assert.IsFalse(outcome.Applied);
        Assert.AreEqual(PrototypeModeApplyKind.Rejected, outcome.Kind);
        Assert.AreEqual(CommandState.Rejected, outcome.Result!.State);
    }

    [TestMethod]
    public async Task Transport_failure_returns_unavailable_without_throwing()
    {
        var coordinator = new PrototypeModeCommandCoordinator((_, _) =>
            Task.FromException<CommandResult>(new IOException("pipe unavailable")));

        var outcome = await coordinator.ApplyAsync(PrototypePerformanceMode.Custom, CancellationToken.None);

        Assert.IsFalse(outcome.Applied);
        Assert.AreEqual(PrototypeModeApplyKind.Unavailable, outcome.Kind);
        Assert.IsNull(outcome.Result);
    }

    [TestMethod]
    public async Task Service_rejection_exception_is_not_reported_as_disconnected()
    {
        var coordinator = new PrototypeModeCommandCoordinator((_, _) =>
            Task.FromException<CommandResult>(new InvalidOperationException("Control service rejected the request.")));

        var outcome = await coordinator.ApplyAsync(PrototypePerformanceMode.Turbo, CancellationToken.None);

        Assert.AreEqual(PrototypeModeApplyKind.Rejected, outcome.Kind);
        Assert.IsFalse(outcome.Applied);
    }

    [TestMethod]
    public async Task Second_request_is_busy_while_the_first_command_is_pending()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PrototypeModeCommandCoordinator((_, _) =>
        {
            entered.SetResult();
            return release.Task;
        });

        var first = coordinator.ApplyAsync(PrototypePerformanceMode.Gaming, CancellationToken.None);
        await entered.Task;
        var second = await coordinator.ApplyAsync(PrototypePerformanceMode.Turbo, CancellationToken.None);
        release.SetResult(Result(CommandState.Applied));

        Assert.AreEqual(PrototypeModeApplyKind.Busy, second.Kind);
        Assert.IsTrue((await first).Applied);
    }

    private static CommandResult Result(CommandState state) =>
        new(Guid.NewGuid(), state, null, RequiredUserAction.None, null, false);
}
