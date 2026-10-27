using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using System.Text.Json;

namespace Jiaolong_ControlCenter.Services;

public sealed class TurboBranchController(
    Func<HomeStateSnapshot?> state,
    Func<HardwareCommand, CancellationToken, Task<CommandResult>> execute)
{
    private Guid? operationId;
    private long generation;
    private long epoch;
    public string? ActiveTier { get; private set; }
    public bool HasOwnership => operationId is not null;
    public CommandResult? LastResult { get; private set; }

    public async Task ApplyAsync(string tier, CancellationToken token)
    {
        var selected = tier switch
        { "Quiet" => TurboBranchTier.Quiet, "Extreme" => TurboBranchTier.Extreme, _ => throw new ArgumentOutOfRangeException(nameof(tier)) };
        await ReleaseAsync(token);
        if (state() is null) throw new InvalidOperationException("硬件状态不可用");
        var command = new ApplyTurboBranchCommand(Guid.NewGuid(), selected);
        // Retain the operation on lost replies so cleanup cannot invent a new owner.
        operationId = command.OperationId;
        LastResult = await execute(command, token);
        if (LastResult.State != CommandState.Applied || LastResult.Error is not null)
        {
            if (LastResult.Mutation == HardwareMutation.NoMutation) operationId = null;
            throw new InvalidOperationException($"狂飙分支未完整应用：{LastResult.Error?.Code}");
        }
        generation = LastResult.ExecutionState?.Generation ?? 0;
        epoch = LastResult.ExecutionState?.HardwareEpoch ?? 0;
        ActiveTier = tier;
    }

    public bool InvalidateIfChanged(HomeStateSnapshot snapshot)
    {
        if (ActiveTier is null) return false;
        var execution = snapshot.Controls.ExecutionState;
        if (snapshot.Controls.PerformanceMode == PerformanceMode.Turbo && execution?.Generation == generation &&
            execution.HardwareEpoch == epoch && execution.Pages.All(page => page.State is not (ApplyState.Unknown or ApplyState.Failed or ApplyState.Superseded)) &&
            execution.Fields.Where(field => field.Value.OwnerOperationId == operationId && field.Value.WriteState == ApplyState.Confirmed)
                .All(field => MatchesConfirmedField(field.Key, field.Value, snapshot.Controls)))
            return false;
        ActiveTier = null;
        return true;
    }

    private static bool MatchesConfirmedField(string key, FieldExecutionState field, HomeControlState controls)
    {
        var cpu = controls.CpuTuning;
        object? actual = key switch
        {
            "performance.acMaxFrequencyMhz" => cpu?.AcFrequency(), "performance.dcMaxFrequencyMhz" => cpu?.DcFrequency(),
            "performance.temperatureLimitC" => cpu?.TemperatureLimitC, "performance.splWatts" => cpu?.SplWatts,
            "performance.spptWatts" => cpu?.SpptWatts, "performance.isBoostEnabled" => cpu?.BoostEnabled,
            "fan.strategy" => controls.ActiveFanControlPlan?.Strategy ?? "Auto", "fan.strongCooling" => controls.StrongCooling,
            _ => null
        };
        if (key == "fan.strategy" && controls.ActiveFanControlPlan is not null) return false;
        if (key == "performance.isBoostEnabled" && field.LastWrittenDetails is { } details &&
            (details.AcBoostMode != cpu?.AcBoostMode || details.DcBoostMode != cpu?.DcBoostMode)) return false;
        return actual is not null && field.LastWritten?.Value is { } expected && JsonSerializer.SerializeToElement(actual).GetRawText() == expected.GetRawText();
    }

    public async Task ReleaseAsync(CancellationToken token)
    {
        ActiveTier = null;
        if (operationId is not { } owner) return;
        LastResult = await execute(new ReleaseTurboBranchCommand(Guid.NewGuid(), owner), token);
        if (LastResult.State != CommandState.Applied || LastResult.Error is not null)
            throw new InvalidOperationException($"狂飙分支恢复未确认：{LastResult.Error?.Code}");
        operationId = null;
    }
}
