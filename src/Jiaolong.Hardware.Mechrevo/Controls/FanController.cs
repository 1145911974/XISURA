using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public enum FanOwner
{
    EcAutomatic,
    ApplicationCurve,
    Unknown
}

public sealed record FanTelemetry(
    DateTimeOffset CapturedAtUtc,
    double CpuTemperatureC,
    double GpuTemperatureC);

public sealed record FanEvaluationResult(
    bool Applied,
    FanOwner FinalOwner,
    ReleaseReason? ReleaseReason);

public interface IFanTransport
{
    Task ApplyCurveAsync(FanControlPlan plan, CancellationToken cancellationToken);
    Task<FanOwner> ReadOwnerAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(ReleaseReason reason, CancellationToken cancellationToken);
}

public sealed class FanController(IFanTransport transport, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan TelemetryStaleAfter = TimeSpan.FromSeconds(3);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<FanEvaluationResult> EvaluateAsync(
        FanTelemetry telemetry,
        FanControlPlan plan,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (telemetry.CapturedAtUtc > now + TimeSpan.FromSeconds(1) ||
            now - telemetry.CapturedAtUtc > TelemetryStaleAfter ||
            !double.IsFinite(telemetry.CpuTemperatureC) ||
            !double.IsFinite(telemetry.GpuTemperatureC) ||
            telemetry.CpuTemperatureC < 0 || telemetry.GpuTemperatureC < 0)
        {
            return await ReleaseAsync(ReleaseReason.SensorStale, cancellationToken);
        }

        if (telemetry.CpuTemperatureC >= 95 || telemetry.GpuTemperatureC >= 87)
        {
            return await ReleaseAsync(ReleaseReason.ThermalEmergency, cancellationToken);
        }

        if (plan.Points is null || plan.Points.Length == 0)
        {
            return await ReleaseAsync(ReleaseReason.SafetyFallback, cancellationToken);
        }

        try
        {
            await transport.ApplyCurveAsync(plan, cancellationToken);
            return new FanEvaluationResult(true,
                await transport.ReadOwnerAsync(cancellationToken), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await ReleaseAsync(ReleaseReason.SafetyFallback, cancellationToken);
        }
    }

    public async Task<FanEvaluationResult> ReleaseAsync(
        ReleaseReason reason,
        CancellationToken cancellationToken)
    {
        await transport.ReleaseAsync(reason, cancellationToken);
        var owner = await transport.ReadOwnerAsync(cancellationToken);
        return new FanEvaluationResult(false, owner, reason);
    }
}
