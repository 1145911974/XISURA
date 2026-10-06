using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private readonly NvidiaClockLimitTransport gpuClockTransport = new();
    private readonly TimeProvider gpuClockTimeProvider = TimeProvider.System;
    private readonly Func<CancellationToken, Task<NvidiaClockDevice>>? gpuClockProbe;
    private NvidiaClockDevice? gpuClockDevice;
    private GpuClockLimitState? gpuClockState;
    private long? gpuClockProbeFailedAt;

    internal WindowsHomeHardwareProvider(TimeProvider timeProvider, Func<CancellationToken, Task<NvidiaClockDevice>> probe) : this()
    {
        gpuClockTimeProvider = timeProvider;
        gpuClockProbe = probe;
    }

    private GpuClockLimitState? ReadGpuClockLimitLocked(CancellationToken token)
    {
        if (gpuClockState is not null && (gpuClockState.Error is null ||
            gpuClockProbeFailedAt is long failedAt && gpuClockTimeProvider.GetElapsedTime(failedAt) < TimeSpan.FromSeconds(10)))
            return gpuClockState;
        try
        {
            gpuClockDevice = (gpuClockProbe?.Invoke(token) ?? gpuClockTransport.ProbeAsync(token)).GetAwaiter().GetResult();
            gpuClockState = new(gpuClockDevice.MinimumMhz, gpuClockDevice.MaximumMhz, null, false, null);
            gpuClockProbeFailedAt = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "GPU clock range unavailable");
            gpuClockState = new(0, 0, null, false, "gpuClockRangeUnavailable");
            gpuClockProbeFailedAt = gpuClockTimeProvider.GetTimestamp();
        }
        return gpuClockState;
    }

    private CommandResult ExecuteGpuClockLocked(SetGpuFrequencyLimitCommand command, CancellationToken token)
    {
        if (!command.RiskConfirmed || compatibilityDecision?.Mode != CompatibilityMode.Writable ||
            !compatibilityDecision.Capabilities.Items.Any(item => item.Key == "gpuFrequencyLimit" && item.State == CapabilityState.Available))
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        var state = ReadGpuClockLimitLocked(token);
        if (gpuClockDevice is null || state?.Error is not null) return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        if (command.Megahertz is int mhz && (mhz < state!.MinimumMhz || mhz > state.MaximumMhz))
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        try
        {
            gpuClockTransport.ApplyAsync(gpuClockDevice, command.Megahertz, token).GetAwaiter().GetResult();
            gpuClockState = state! with { SubmittedMhz = command.Megahertz, HasSubmission = true };
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "GPU clock command did not complete; previous locked range is unavailable");
            // Never reset an unknown pre-existing limit as an invented rollback.
            return new(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.HardwareWriteFailed, command.OperationId, false), false);
        }
    }
}
