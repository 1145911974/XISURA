using System.Text.Json.Serialization;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Contracts.Commands;

[JsonConverter(typeof(LowerCamelEnumConverter<CommandState>))]
public enum CommandState
{
    Queued,
    Validating,
    Applying,
    Applied,
    Rejected,
    RolledBack,
    RecoveryRequired
}

public enum RequiredUserAction
{
    None,
    Restart
}

public sealed record CommandResult(
    Guid OperationId,
    CommandState State,
    HardwareSnapshot? VerifiedState,
    RequiredUserAction RequiredAction,
    ServiceError? Error,
    bool IsReplay)
{
    // Present only when the firmware mode was read back after the write; older services omit it.
    public PerformanceMode? VerifiedPerformanceMode { get; init; }
}

public static class CommandValidation
{
    public static ServiceError? Validate(HardwareCommand command) => command switch
    {
        SetCpuTuningCommand cpu => ValidateCpu(cpu.Plan),
        SetCpuTuningBatchCommand batch => ValidateCpuBatch(batch),
        SetGpuFrequencyLimitCommand gpu => ValidateGpu(gpu.Megahertz),
        SetGpuVoltageBoostCommand voltage => voltage.RiskConfirmed && voltage.ExpectedPercent <= 100 && voltage.Percent <= 100
            ? null : ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false),
        SetGpuPowerLimitCommand or SetGpuPowerPolicyCommand => ServiceError.Create(ErrorCode.CapabilityUnavailable, Guid.Empty, false),
        SetGpuVfCurveCommand vf => vf.RiskConfirmed && vf.ExpectedOffsetsKhz is { Length: 127 } && vf.ExpectedOffsetsKhz[0] == 0 &&
            vf.OffsetsKhz is { Length: 127 } && vf.OffsetsKhz[0] == 0 && vf.OffsetsKhz.Skip(1).All(value => value is >= -200_000 and <= 200_000)
            ? null : ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false),
        SetGpuMemoryOffsetCommand memory => memory.RiskConfirmed && memory.OffsetKhz is >= -200_000 and <= 200_000 &&
            memory.OffsetKhz % 1000 == 0 && memory.ExpectedOffsetKhz is >= -1_000_000 and <= 3_000_000
            ? null : ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false),
        SetGpuCoreOffsetCommand core => core.RiskConfirmed && core.OffsetKhz is >= -200_000 and <= 200_000 &&
            core.OffsetKhz % 1000 == 0 && core.ExpectedOffsetKhz is >= -1_000_000 and <= 1_000_000 &&
            (core.ExpectedVfOffsetsKhz is null || core.ExpectedVfOffsetsKhz is { Length: 127 } && core.ExpectedVfOffsetsKhz[0] == 0)
            ? null : ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false),
        SetFanControlCommand fan => ValidateFan(fan.Plan),
        SetKeyboardLightingCommand lighting => lighting.Plan?.IsValid() == true
            ? null : ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false),
        _ => null
    };

    private static ServiceError? ValidateCpuBatch(SetCpuTuningBatchCommand batch)
    {
        if (batch.NativeMode is not (null or PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo) ||
            !batch.RiskConfirmed || batch.Plans is not { Length: > 0 and <= 20 } ||
            batch.Plans.Any(plan => plan is null || ValidateCpu(plan) is not null))
            return ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false);
        // These commands address the same physical limits on the supported Dragon Range layout.
        bool Conflicts(IEnumerable<double?> values) => values.Where(value => value.HasValue).Distinct().Take(2).Count() > 1;
        if (Conflicts(batch.Plans.SelectMany(plan => new double?[] { plan.SpptWatts, plan.Advanced?.FastPptWatts, plan.Advanced?.PptWatts })) ||
            Conflicts(batch.Plans.SelectMany(plan => new double?[] { plan.TemperatureLimitC, plan.Advanced?.Mp1TemperatureC, plan.Advanced?.RsmuTemperatureC })) ||
            Conflicts(batch.Plans.SelectMany(plan => new double?[] { plan.Advanced?.VrmCurrentMilliamps, plan.Advanced?.TdcCurrentMilliamps })))
            return ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false);
        return null;
    }

    private static ServiceError? ValidateCpu(CpuTuningPlan plan)
    {
        if (plan.TemperatureLimitC is < 40 or > 110 ||
            plan.SplWatts is < 1 or > 200 ||
            plan.SpptWatts is < 1 or > 250 ||
            plan.MaxFrequencyMhz is < 500 or > 6000 ||
            plan.AcMaxFrequencyMhz is < 500 or > 6000 ||
            plan.DcMaxFrequencyMhz is < 500 or > 6000 ||
            plan.AcMinActiveCoresPercent is < 0 or > 100 ||
            plan.DcMinActiveCoresPercent is < 0 or > 100 ||
            plan.AcMinActiveCoresPercent.HasValue != plan.DcMinActiveCoresPercent.HasValue ||
            plan.EnabledCoreCount is < 1 or > 16 ||
            plan.NegativeCurveOptimizer is < -30 or > 0)
        {
            return ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false);
        }

        int curvePaths = (plan.NegativeCurveOptimizer.HasValue ? 1 : 0)
            + (plan.Advanced?.CurveOptimizerAll.HasValue == true ? 1 : 0)
            + (plan.Advanced?.PerCoreCurveOptimizer is { Count: > 0 } ? 1 : 0);
        if (curvePaths > 1 || (plan.Advanced is { } advanced && !ValidateAdvanced(advanced)))
            return ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false);

        return null;
    }

    private static bool ValidateAdvanced(AdvancedCpuTuningPlan plan) =>
        plan.OverclockEnabled is null && plan.OcClockMhz is null && plan.OcVoltageMillivolts is null && plan.PerCoreOcClockMhz is null &&
        plan.StapmWatts is not (< 1 or > 250) &&
        plan.FastPptWatts is not (< 1 or > 250) &&
        plan.SlowPptWatts is not (< 1 or > 250) &&
        plan.PptWatts is not (< 1 or > 250) &&
        plan.VrmCurrentMilliamps is not (< 0 or > 200_000) &&
        plan.TdcCurrentMilliamps is not (< 0 or > 200_000) &&
        plan.EdcCurrentMilliamps is not (< 0 or > 200_000) &&
        plan.Mp1TemperatureC is not (< 40 or > 115) &&
        plan.RsmuTemperatureC is not (< 40 or > 115) &&
        plan.PboScalar is not (< 1 or > 10) &&
        plan.OcClockMhz is not (< 1800 or > 5100) &&
        plan.OcVoltageMillivolts is not (< 800 or > 1200) &&
        (plan.OcVoltageMillivolts is null || plan.OcVoltageMillivolts % 25 == 0) &&
        plan.CurveOptimizerAll is not (< -30 or > 30) &&
        (plan.PerCoreOcClockMhz is null || plan.PerCoreOcClockMhz.All(pair => pair.Key is >= 0 and < 8 && pair.Value is >= 1800 and <= 5100)) &&
        (plan.PerCoreCurveOptimizer is null || plan.PerCoreCurveOptimizer.All(pair => pair.Key is >= 0 and < 16 && pair.Value is >= -30 and <= 30));

    private static ServiceError? ValidateGpu(int? megahertz) =>
        megahertz is < 300 or > 5000
            ? ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false)
            : null;

    private static ServiceError? ValidateFan(FanControlPlan? plan)
    {
        if (plan is null || plan.Strategy is not ("Auto" or "Curve" or "Fixed") ||
            plan.FixedRpm is < 1800 or > 5800 ||
            plan.MaximumRpm is < 1800 or > 5800 ||
            (plan.Strategy == "Fixed" && plan.FixedRpm is null) ||
            (plan.Strategy == "Auto" && plan.MaximumRpm is null) ||
            !ValidPoints(plan.Points) ||
            (plan.GpuPoints is not null && !ValidPoints(plan.GpuPoints)))
            return ServiceError.Create(ErrorCode.ValidationFailed, Guid.Empty, false);
        return null;
    }

    private static bool ValidPoints(FanPoint[]? points) =>
        points is { Length: >= 2 and <= 71 } &&
        points.All(point => point is not null && point.Percent is >= 0 and <= 100 && point.TemperatureC is >= 30 and <= 100) &&
        points.Zip(points.Skip(1)).All(pair => pair.First.TemperatureC < pair.Second.TemperatureC);
}
