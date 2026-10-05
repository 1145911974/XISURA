using System.Diagnostics;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private BldingFanEcTransport? fanTransport;
    private Timer? fanTimer;
    private FanControlPlan? activeFanPlan;
    private bool strongCoolingActive;
    private bool fanTouched;
    private byte? fanPreviousStrongCooling;
    private FanEcControlState? fanLastEcState;
    private readonly AutomaticFanCeiling automaticFanCeiling = new();

    private FanEcControlState? ReadFanEcControlLocked()
    {
        if (fanTransport is null) return fanLastEcState;
        try { return fanLastEcState = fanTransport.ReadControlState(); }
        catch (Exception error) { logger?.LogDebug(error, "Fan EC status read unavailable"); return null; }
    }

    private CommandResult ExecuteFanLocked(HardwareCommand command, CancellationToken cancellationToken)
    {
        if (command is ReleaseFanControlCommand)
        {
            try
            {
                ReleaseFanLocked();
                return FanSubmitted(command.OperationId);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Fan EC release failed");
                return FanRecoveryRequired(command.OperationId);
            }
        }

        if (command is SetStrongCoolingCommand strong) return ExecuteStrongCoolingLocked(strong, cancellationToken);

        if (compatibilityDecision?.Mode != CompatibilityMode.Writable || !BldingFanEcTransport.HasVerifiedFiles())
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        if (ThirdPartyFanWriterRunning())
            return Rejected(command.OperationId, ErrorCode.ConflictDetected);
        if (strongCoolingActive)
        {
            try { ReleaseFanLocked(); }
            catch { return FanRecoveryRequired(command.OperationId); }
        }
        if (activeFanPlan is null && (fanTouched || fanPreviousStrongCooling is not null))
            return FanRecoveryRequired(command.OperationId);

        FanControlPlan plan;
        if (command is SetFanControlCommand requested)
        {
            if (!requested.RiskConfirmed || CommandValidation.Validate(requested) is not null)
                return Rejected(command.OperationId, ErrorCode.ValidationFailed);
            plan = requested.Plan;
        }
        else return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        if (activeFanPlan is not null && (activeFanPlan.Strategy == "Auto" || plan.Strategy == "Auto"))
        {
            try { ReleaseFanLocked(); }
            catch { return FanRecoveryRequired(command.OperationId); }
        }

        var telemetry = ReadTelemetryLocked(cancellationToken);
        if (!TryTargets(plan, telemetry, out var cpu, out var gpu))
            return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
        if (telemetry.CpuTemperatureC >= 95 || telemetry.GpuTemperatureC >= 87 ||
            (telemetry.CpuTemperatureC >= 85 || telemetry.GpuTemperatureC >= 80) && (cpu < 50 || gpu < 50))
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);

        try
        {
            PrepareFanStrongCoolingLocked(cancellationToken);
            fanTransport ??= new BldingFanEcTransport();
            fanTransport.Open();
            if (plan.Strategy == "Auto") ApplyAutomaticCeilingLocked(plan, telemetry, cpu, gpu);
            else fanTransport.SetTargets(cpu, gpu);
            fanTouched |= fanTransport.HasWritten;
            activeFanPlan = plan;
            strongCoolingActive = false;
            fanTimer ??= new Timer(_ => FanTick(), null, Timeout.Infinite, Timeout.Infinite);
            fanTimer.Change(1000, 1000);
            return FanSubmitted(command.OperationId);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Fan EC apply failed");
            fanTouched |= fanTransport?.HasWritten == true;
            try { ReleaseFanLocked(); }
            catch (Exception releaseError)
            {
                logger?.LogError(releaseError, "Fan EC rollback failed");
                return FanRecoveryRequired(command.OperationId);
            }
            return Rejected(command.OperationId, ErrorCode.HardwareWriteFailed);
        }
    }

    private void FanTick()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (disposed) return;
            if (strongCoolingActive)
            {
                if (fanTransport is null || ThirdPartyFanWriterRunning() ||
                    !HasSafeFanTelemetry(ReadTelemetryLocked(CancellationToken.None)))
                { ReleaseFanLocked(); return; }
                fanTransport.SetTargets(68, 68);
                return;
            }
            if (activeFanPlan is null)
            {
                if (fanTouched || fanPreviousStrongCooling is not null) ReleaseFanLocked();
                return;
            }
            if (fanTransport is null) { ReleaseFanLocked(); return; }
            if (ThirdPartyFanWriterRunning())
            {
                ReleaseFanLocked();
                return;
            }
            var telemetry = ReadTelemetryLocked(CancellationToken.None);
            if (!TryTargets(activeFanPlan, telemetry, out var cpu, out var gpu) ||
                telemetry.CpuTemperatureC >= 95 || telemetry.GpuTemperatureC >= 87)
            {
                ReleaseFanLocked();
                return;
            }
            if (activeFanPlan.Strategy == "Auto") ApplyAutomaticCeilingLocked(activeFanPlan, telemetry, cpu, gpu);
            else fanTransport.SetTargets(cpu, gpu);
            fanTouched |= fanTransport.HasWritten;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Fan EC update failed; releasing control");
            fanTouched |= fanTransport?.HasWritten == true;
            try { ReleaseFanLocked(); }
            catch (Exception releaseError) { logger?.LogError(releaseError, "Fan EC fallback failed"); }
        }
        finally { Monitor.Exit(gate); }
    }

    private void ApplyAutomaticCeilingLocked(FanControlPlan plan, HardwareSnapshot telemetry, byte cpu, byte gpu)
    {
        if (automaticFanCeiling.Update(plan.MaximumRpm!.Value, telemetry))
        {
            fanTransport!.SetTargets(cpu, gpu);
            fanTouched = true;
        }
        else if (fanTransport!.HasWritten)
        {
            fanTransport.Clear();
            fanLastEcState = fanTransport.ReadControlState();
            fanTouched = false;
        }
    }

    private void ReleaseFanLocked()
    {
        fanTouched |= fanTransport?.HasWritten == true;
        activeFanPlan = null;
        strongCoolingActive = false;
        automaticFanCeiling.Reset();
        fanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (fanTouched && fanTransport is null)
            {
                fanTransport = new BldingFanEcTransport();
                fanTransport.Open();
            }
            if (fanTouched)
            {
                fanTransport!.Clear();
                fanLastEcState = fanTransport.ReadControlState();
            }
            fanTouched = false;
        }
        finally
        {
            try
            {
                if (!fanTouched)
                {
                    fanTransport?.Dispose();
                    fanTransport = null;
                }
                // Restore the firmware flag only after releasing both EC targets.
                if (!fanTouched)
                {
                    RestoreStrongCoolingLocked();
                }
            }
            finally
            {
                if (fanTouched || fanPreviousStrongCooling is not null)
                {
                    fanTimer ??= new Timer(_ => FanTick(), null, Timeout.Infinite, Timeout.Infinite);
                    fanTimer.Change(1000, 1000);
                }
            }
        }
    }

    private static bool HasSafeFanTelemetry(HardwareSnapshot telemetry)
    {
        var age = DateTimeOffset.UtcNow - telemetry.CapturedAtUtc;
        return telemetry.CpuTemperatureC is double cpu && double.IsFinite(cpu) && cpu is >= 0 and < 95 &&
            telemetry.GpuTemperatureC is double gpu && double.IsFinite(gpu) && gpu is >= 0 and < 87 &&
            age >= TimeSpan.FromSeconds(-1) && age <= TimeSpan.FromSeconds(3);
    }

    private static bool TryTargets(FanControlPlan plan, HardwareSnapshot telemetry, out byte cpu, out byte gpu)
    {
        cpu = gpu = 0;
        if (!HasSafeFanTelemetry(telemetry)) return false;
        double cpuTemperature = telemetry.CpuTemperatureC!.Value;
        double gpuTemperature = telemetry.GpuTemperatureC!.Value;

        if (plan.Strategy == "Auto")
        {
            if (plan.MaximumRpm is not (>= 1800 and <= 5800)) return false;
            if (telemetry.CpuFanRpm is not { } cpuRpm || telemetry.GpuFanRpm is not { } gpuRpm ||
                !double.IsFinite(cpuRpm) || !double.IsFinite(gpuRpm) || cpuRpm < 0 || gpuRpm < 0) return false;
            cpu = gpu = (byte)(plan.MaximumRpm.Value / 100);
            return true;
        }

        if (plan.Strategy == "Fixed")
        {
            cpu = gpu = (byte)Math.Clamp((plan.FixedRpm ?? 1800) / 100, 18, 58);
            return true;
        }
        cpu = ToEcTarget(Interpolate(plan.Points, cpuTemperature));
        gpu = ToEcTarget(Interpolate(plan.GpuPoints ?? plan.Points, gpuTemperature));
        if (plan.MaximumRpm is int maximum)
        {
            cpu = (byte)Math.Min(cpu, maximum / 100);
            gpu = (byte)Math.Min(gpu, maximum / 100);
        }
        return true;
    }

    private static byte ToEcTarget(double percent) =>
        (byte)Math.Clamp((int)Math.Round((1800 + Math.Clamp(percent, 0, 100) * 40) / 100), 18, 58);

    private static double Interpolate(FanPoint[] points, double temperature)
    {
        if (temperature <= points[0].TemperatureC) return points[0].Percent;
        for (int i = 1; i < points.Length; i++)
        {
            if (temperature > points[i].TemperatureC) continue;
            var left = points[i - 1];
            var right = points[i];
            return left.Percent + (right.Percent - left.Percent) *
                (temperature - left.TemperatureC) / (right.TemperatureC - left.TemperatureC);
        }
        return points[^1].Percent;
    }

    private static bool ThirdPartyFanWriterRunning()
    {
        try
        {
            foreach (string name in new[] { "第三方蛟龙游戏控制中心", "JiaoLongControl", "JiaoLongControl.Server" })
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Length > 0) return true; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return false;
        }
        catch { return true; }
    }

    private CommandResult FanSubmitted(Guid operationId) => new(
        operationId, CommandState.Applied, null, RequiredUserAction.None, null, false);

    private static CommandResult FanRecoveryRequired(Guid operationId) => new(
        operationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
        ServiceError.Create(ErrorCode.RollbackFailed, operationId, false), false);
}
