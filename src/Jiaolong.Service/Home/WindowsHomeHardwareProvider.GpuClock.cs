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
        token.ThrowIfCancellationRequested();
        EnsureProviderReadAccess();
        if (gpuClockState is not null && (gpuClockState.Error is null ||
            gpuClockProbeFailedAt is long failedAt && gpuClockTimeProvider.GetElapsedTime(failedAt) < TimeSpan.FromSeconds(10)))
            return gpuClockState;
        try
        {
            gpuClockDevice = ReadProviderHardware(() =>
                (gpuClockProbe?.Invoke(token) ?? gpuClockTransport.ProbeAsync(token)).GetAwaiter().GetResult());
            gpuClockState = new(gpuClockDevice.MinimumMhz, gpuClockDevice.MaximumMhz, null, false, null);
            gpuClockProbeFailedAt = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (CpuHardwareReadAllowed)
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
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
        if (!CreateFieldCapabilities(compatibilityDecision.Capabilities.Items, null)
            .Any(item => item.Key == "coreFrequencyLimitMhz" && item.Readable && item.Writable))
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable) with
            {
                Mutation = HardwareMutation.NoMutation,
                Error = ServiceError.Create(ErrorCode.CapabilityUnavailable, command.OperationId, false) with
                { Details = new() { ["reason"] = "gpuClockLimitReadbackUnavailable" } }
            };
        bool writeAttempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (BackgroundWorkPaused) throw new IOException("hardwareAccessPaused");
            var state = ReadGpuClockLimitLocked(token);
            if (gpuClockDevice is null || state?.Error is not null)
                return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
            if (command.Megahertz is int mhz && (mhz < state!.MinimumMhz || mhz > state.MaximumMhz))
                return Rejected(command.OperationId, ErrorCode.ValidationFailed) with { Mutation = HardwareMutation.NoMutation };
            token.ThrowIfCancellationRequested();
            if (BackgroundWorkPaused) throw new IOException("hardwareAccessPaused");
            writeAttempted = true;
            gpuClockTransport.ApplyAsync(gpuClockDevice, command.Megahertz, token).GetAwaiter().GetResult();
            if (BackgroundWorkPaused) throw new IOException("hardwareAccessPaused");
            gpuClockState = state! with { SubmittedMhz = command.Megahertz, HasSubmission = true };
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "GPU clock command did not complete; write attempted: {WriteAttempted}", writeAttempted);
            var code = writeAttempted ? ErrorCode.HardwareWriteFailed :
                ex is OperationCanceledException || ex.Message == "hardwareAccessPaused" ? ErrorCode.CancelledBeforeApply : ErrorCode.HardwareReadFailed;
            var error = ServiceError.Create(code, command.OperationId, false) with
            {
                Details = new() { ["reason"] = ex.Message, ["writeAttempted"] = writeAttempted.ToString() }
            };
            // Never reset an unknown pre-existing limit as an invented rollback.
            return new CommandResult(command.OperationId, writeAttempted ? CommandState.RecoveryRequired : CommandState.Rejected,
                null, RequiredUserAction.None, error, false)
            { Mutation = writeAttempted ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
        }
    }
}
