using Jiaolong.Contracts.Commands;
using Jiaolong_ControlCenter.Prototype.Controls;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformanceLiveOwnershipTests
{
    [TestMethod]
    public void Only_confirmed_no_mutation_or_rollback_can_restore_following()
    {
        static CommandResult Result(CommandState state, HardwareMutation mutation) =>
            new(Guid.NewGuid(), state, null, RequiredUserAction.None, null, false) { Mutation = mutation };

        Assert.IsTrue(PerformanceWorkspaceV2.IsConfirmedNoLiveMutation(
            Result(CommandState.Rejected, HardwareMutation.NoMutation)));
        Assert.IsTrue(PerformanceWorkspaceV2.IsConfirmedNoLiveMutation(
            Result(CommandState.RolledBack, HardwareMutation.Changed)));
        Assert.IsFalse(PerformanceWorkspaceV2.IsConfirmedNoLiveMutation(
            Result(CommandState.RecoveryRequired, HardwareMutation.NoMutation)));
        Assert.IsFalse(PerformanceWorkspaceV2.IsConfirmedNoLiveMutation(
            Result(CommandState.Rejected, HardwareMutation.Unknown)));
        Assert.IsFalse(PerformanceWorkspaceV2.IsConfirmedNoLiveMutation(
            Result(CommandState.Applied, HardwareMutation.Changed)));
    }
}
