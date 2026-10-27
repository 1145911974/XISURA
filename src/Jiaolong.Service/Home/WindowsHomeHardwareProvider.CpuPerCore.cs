using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private CommandResult ExecutePerCoreCurveLocked(SetCpuTuningCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!command.RiskConfirmed) return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        var plan = command.Plan;
        var values = plan.Advanced?.PerCoreCurveOptimizer;
        bool onlyPerCore = values is not null &&
            (plan with { Advanced = null }) == new CpuTuningPlan(null, null, null, null, null, null, null, null) &&
            (plan.Advanced! with { PerCoreCurveOptimizer = null }) == new AdvancedCpuTuningPlan();
        if (!onlyPerCore || values!.Count is < 1 or > 16 ||
            values.Any(pair => pair.Key is < 0 or > 15 || pair.Value is < -50 or > 0))
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);

        if (compatibilityDecision?.Mode != CompatibilityMode.Writable ||
            compatibilityDecision.Capabilities.Items.Any(item =>
                item.Key == "cpuTuning:curveOptimizer" && item.State == CapabilityState.Available) != true ||
            curveOptimizer is null ||
            ReadOptionalCpuField(CpuTuningField.EnabledCoreCount, cancellationToken) is not int cores || cores is < 1 or > 16 ||
            values.Any(pair => pair.Key >= cores) ||
            SystemPowerStatusReader.Read().AcPowerConnected is not true ||
            ReadTelemetryLocked(cancellationToken).CpuTemperatureC is not double temperature || temperature >= 80)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        return ExecutePerCoreCurveTransaction(command.OperationId, values, cancellationToken);
    }

    internal CommandResult ExecutePerCoreCurveTransaction(Guid operationId, IReadOnlyDictionary<int, int> values,
        CancellationToken cancellationToken)
    {
        try
        {
            curveOptimizer!.WritePerCore(values, cancellationToken);
            return new(operationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested &&
            exception.Data["curveOptimizerWriteAttempted"] is not bool) { throw; }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Per-core curve optimizer transaction failed");
            bool noMutation = exception.Data["curveOptimizerWriteAttempted"] is false;
            bool rollbackFailed = exception.Data["curveOptimizerRollbackFailed"] is not false;
            var code = noMutation
                ? exception is OperationCanceledException || exception.Message == "hardwareAccessPaused"
                    ? ErrorCode.CancelledBeforeApply : exception is ArgumentException
                        ? ErrorCode.ValidationFailed : ErrorCode.HardwareReadFailed
                : rollbackFailed ? ErrorCode.RollbackFailed : ErrorCode.HardwareWriteFailed;
            var error = ServiceError.Create(code, operationId, false) with
            {
                Details = new()
                {
                    ["reason"] = exception.Message,
                    ["writeAttempted"] = (!noMutation).ToString(),
                    ["rollbackConfirmed"] = (!noMutation && !rollbackFailed).ToString()
                }
            };
            return new CommandResult(operationId, noMutation ? CommandState.Rejected : rollbackFailed
                ? CommandState.RecoveryRequired : CommandState.RolledBack, null, RequiredUserAction.None, error, false)
            { Mutation = noMutation ? HardwareMutation.NoMutation : HardwareMutation.Unknown };
        }
    }
}
