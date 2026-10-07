using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private CommandResult ExecuteCpuTuningBatchLocked(SetCpuTuningBatchCommand command, CancellationToken token)
    {
        if (!command.RiskConfirmed || CommandValidation.Validate(command) is not null || performanceController is null ||
            compatibilityDecision?.Mode != CompatibilityMode.Writable || SystemPowerStatusReader.Read().AcPowerConnected is not true)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        bool Available(string key) => compatibilityDecision.Capabilities.Items.Any(item => item.Key == key && item.State == CapabilityState.Available);
        if (!Available("cpuTuning")) return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        bool thermalGuard = false;
        foreach (var plan in command.Plans)
        {
            if (plan.TemperatureLimitC is < 40 or > 100 || plan.SplWatts is < 45 or > 75 || plan.SpptWatts is < 45 or > 75)
                return Rejected(command.OperationId, ErrorCode.ValidationFailed);
            // Cooling presets must remain usable while hot; only raising a limit needs headroom.
            thermalGuard |= CpuPresetThermalPolicy.RequiresHeadroom(plan,
                field => ReadOptionalCpuField(field, token) as int?);
            if (plan.Advanced is { } advanced)
            {
                var smuOnly = advanced with { PboScalar = null, CurveOptimizerAll = null, PerCoreCurveOptimizer = null };
                if (smuOnly != new AdvancedCpuTuningPlan())
                {
                    if (!Available("cpuTuning:smu") || RyzenSmuAdvancedLimits.BuildSingle(smuOnly) is null)
                        return Rejected(command.OperationId, ErrorCode.ValidationFailed);
                    thermalGuard = true;
                }
                if (advanced.PboScalar.HasValue)
                {
                    if (!Available("cpuTuning:pbo")) return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
                    if (ReadOptionalCpuField(CpuTuningField.PboScalar, token) is not int previous)
                        return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
                    thermalGuard |= advanced.PboScalar.Value > previous;
                }
                if (advanced.CurveOptimizerAll is < -30 or > 0 ||
                    advanced.PerCoreCurveOptimizer?.Any(pair => pair.Key is < 0 or > 15 || pair.Value is < -30 or > 0) == true)
                    return Rejected(command.OperationId, ErrorCode.ValidationFailed);
            }
            if ((plan.NegativeCurveOptimizer.HasValue || plan.Advanced?.CurveOptimizerAll.HasValue == true || plan.Advanced?.PerCoreCurveOptimizer is { Count: > 0 }) &&
                !Available("cpuTuning:curveOptimizer")) return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        }
        if (thermalGuard)
        {
            var temperature = ReadTelemetryLocked(token).CpuTemperatureC;
            if (temperature is null or >= 80)
            {
                var rejected = Rejected(command.OperationId, ErrorCode.ValidationFailed);
                rejected.Error!.Details["reason"] = "cpuThermalHeadroomRequired";
                rejected.Error.Details["cpuTemperatureC"] = temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable";
                return rejected;
            }
        }
        bool hasOemLimits = command.Plans.Any(plan => plan.TemperatureLimitC.HasValue || plan.SplWatts.HasValue || plan.SpptWatts.HasValue);
        var nativeMode = command.NativeMode ?? (hasOemLimits ? ReadPerformanceMode(telemetryProvider ?? observedMiProvider, token) : null);
        if (hasOemLimits && nativeMode is null) return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
        PerformanceMode? originalNativeMode = null;
        if (nativeMode.HasValue)
        {
            if (!Available("performanceMode")) return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
            originalNativeMode = ReadPerformanceMode(telemetryProvider ?? observedMiProvider, token);
            if (originalNativeMode is not (PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo))
                return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
        }
        Task SetNativeModeAsync(PerformanceMode mode, CancellationToken modeToken)
        {
            if (ReadPerformanceMode(telemetryProvider ?? observedMiProvider, modeToken) == mode)
                return Task.CompletedTask;
            var applied = ExecuteLocked(new SetPerformanceModeCommand(Guid.NewGuid(), mode), modeToken);
            if (applied.State != CommandState.Applied || applied.Error is not null)
                throw new InvalidOperationException("cpuBatchNativeModeWriteFailed");
            return Task.CompletedTask;
        }
        PerformanceMode? observedNativeMode = null;
        bool VerifyNativeMode(CancellationToken modeToken)
        {
            observedNativeMode = ReadPerformanceMode(telemetryProvider ?? observedMiProvider, modeToken);
            return observedNativeMode == nativeMode;
        }
        var result = performanceController.ApplyCpuTuningBatchAsync(command.Plans, true, token,
            prepareNativeMode: nativeMode is { } target ? async modeToken =>
            {
                // CMEN can select Turbo. Enable it before selecting the requested native mode.
                await cpuTuningTransport!.PrepareOemLimitsAsync(command.Plans, modeToken);
                await SetNativeModeAsync(target, modeToken);
            } : null,
            restoreNativeMode: originalNativeMode is { } original ? modeToken => SetNativeModeAsync(original, modeToken) : null,
            verifyNativeMode: nativeMode.HasValue
                ? modeToken => Task.FromResult(VerifyNativeMode(modeToken))
                : null).GetAwaiter().GetResult();
        smuLimitCacheAt = default;
        var error = result.Error is null ? null : result.Error with { CorrelationId = command.OperationId };
        if (error is not null && nativeMode.HasValue)
        {
            error.Details["expectedNativeMode"] = nativeMode.Value.ToString();
            error.Details["observedNativeMode"] = observedNativeMode?.ToString() ?? "unreadable";
        }
        if (result.State == CommandState.RolledBack && originalNativeMode.HasValue &&
            ReadPerformanceMode(telemetryProvider ?? observedMiProvider, CancellationToken.None) != originalNativeMode)
            error = ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false) with { Details = error?.Details ?? [] };
        return new(command.OperationId, error?.Code == ErrorCode.RollbackFailed ? CommandState.RecoveryRequired : result.State,
            null, RequiredUserAction.None, error, false);
    }

    private CommandResult ExecuteAdvancedCpuLimitLocked(SetCpuTuningCommand command, CancellationToken cancellationToken)
    {
        var plan = command.Plan;
        var single = plan.Advanced is null ? null : RyzenSmuAdvancedLimits.BuildSingle(plan.Advanced);
        if ((plan with { Advanced = null }) != new CpuTuningPlan(null, null, null, null, null, null, null, null) ||
            plan.Advanced is null || single is null)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        if (!command.RiskConfirmed || compatibilityDecision?.Mode != CompatibilityMode.Writable ||
            compatibilityDecision.Capabilities.Items.Any(item => item.Key == "cpuTuning:smu" && item.State == CapabilityState.Available) != true ||
            curveOptimizer is null || SystemPowerStatusReader.Read().AcPowerConnected is not true ||
            ReadTelemetryLocked(cancellationToken).CpuTemperatureC is not double temperature || temperature >= 80)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        if (performanceController is null) return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        var result = performanceController.ApplyCpuTuningAsync(plan, true, cancellationToken).GetAwaiter().GetResult();
        smuLimitCacheAt = default;
        var error = result.Error is null ? null : result.Error with { CorrelationId = command.OperationId };
        return new(command.OperationId,
            error?.Code == ErrorCode.RollbackFailed ? CommandState.RecoveryRequired : result.State,
            null, RequiredUserAction.None, error, false);
    }
}
