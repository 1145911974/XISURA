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
            values.Any(pair => pair.Key is < 0 or > 15 || pair.Value is < -30 or > 0))
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

        try
        {
            curveOptimizer.WritePerCore(values, cancellationToken);
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Per-core curve optimizer transaction failed");
            bool rollbackFailed = exception.Data["curveOptimizerRollbackFailed"] is not false;
            return new(command.OperationId, rollbackFailed ? CommandState.RecoveryRequired : CommandState.RolledBack,
                null, RequiredUserAction.None,
                ServiceError.Create(rollbackFailed ? ErrorCode.RollbackFailed : ErrorCode.HardwareWriteFailed, command.OperationId, false), false);
        }
    }
}
