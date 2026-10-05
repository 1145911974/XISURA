using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public interface IMuxTransport
{
    Task<MuxMode> ReadMuxAsync(CancellationToken cancellationToken);
    Task WriteMuxAsync(MuxMode mode, CancellationToken cancellationToken);
}

public sealed record GpuFrequencyRange(int MinimumMhz, int MaximumMhz);

public interface IGpuLimitTransport
{
    Task<GpuFrequencyRange> ReadRangeAsync(CancellationToken cancellationToken);
    Task ApplyCoreLimitAsync(int minimumMhz, int maximumMhz, CancellationToken cancellationToken);
    Task ResetCoreLimitAsync(CancellationToken cancellationToken);
    Task<int?> ReadCoreLimitAsync(CancellationToken cancellationToken);
}

public sealed record GpuLimitCommandResult(
    Guid OperationId,
    CommandState State,
    int? VerifiedLimitMhz,
    RequiredUserAction RequiredAction,
    ServiceError? Error);

public sealed record VerifiedMuxState(MuxMode MuxMode);

public sealed record MuxCommandResult(
    Guid OperationId,
    CommandState State,
    VerifiedMuxState? VerifiedState,
    RequiredUserAction RequiredAction,
    ServiceError? Error);

public sealed class GpuController(
    IMuxTransport? transport = null,
    IGpuLimitTransport? limitTransport = null)
{
    public async Task<MuxCommandResult> ApplyMuxAsync(MuxMode mode, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        if (!Enum.IsDefined(mode) || transport is null)
        {
            return Rejected(operationId, ErrorCode.CapabilityUnavailable);
        }

        MuxMode previous;
        try
        {
            previous = await transport.ReadMuxAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Rejected(operationId, ErrorCode.HardwareReadFailed);
        }

        try
        {
            await transport.WriteMuxAsync(mode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = await RestoreMuxAsync(transport, operationId, previous, ErrorCode.HardwareWriteFailed, null);
            throw;
        }
        catch
        {
            return await RestoreMuxAsync(transport, operationId, previous, ErrorCode.HardwareWriteFailed, null);
        }

        MuxMode readBack;
        try
        {
            readBack = await transport.ReadMuxAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = await RestoreMuxAsync(transport, operationId, previous, ErrorCode.HardwareReadFailed, null);
            throw;
        }
        catch
        {
            return await RestoreMuxAsync(transport, operationId, previous, ErrorCode.HardwareReadFailed, null);
        }

        if (readBack != mode)
            return await RestoreMuxAsync(transport, operationId, previous, ErrorCode.ReadBackMismatch, readBack);

        return new MuxCommandResult(
            operationId,
            CommandState.Applied,
            new VerifiedMuxState(readBack),
            RequiredUserAction.Restart,
            null);
    }

    public async Task<GpuLimitCommandResult> ApplyFrequencyLimitAsync(
        GpuLimitPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var operationId = Guid.NewGuid();
        if (limitTransport is null)
        {
            return RejectedLimit(operationId, ErrorCode.CapabilityUnavailable);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        GpuFrequencyRange? range = null;
        int? previous = null;
        int? lastKnown = null;
        var writeAttempted = false;
        try
        {
            range = await limitTransport.ReadRangeAsync(deadline.Token);
            if (range.MinimumMhz < 0 || range.MaximumMhz < range.MinimumMhz)
            {
                return RejectedLimit(operationId, ErrorCode.ValidationFailed);
            }

            if (plan.CoreFrequencyLimitMhz is { } requested
                && (requested < range.MinimumMhz || requested > range.MaximumMhz))
            {
                return RejectedLimit(operationId, ErrorCode.ValidationFailed);
            }

            previous = await limitTransport.ReadCoreLimitAsync(deadline.Token);
            if (previous == plan.CoreFrequencyLimitMhz)
            {
                return new GpuLimitCommandResult(
                    operationId, CommandState.Applied, previous, RequiredUserAction.None, null);
            }

            writeAttempted = true;
            if (plan.CoreFrequencyLimitMhz is null)
            {
                await limitTransport.ResetCoreLimitAsync(deadline.Token);
            }
            else
            {
                await limitTransport.ApplyCoreLimitAsync(
                    range.MinimumMhz, plan.CoreFrequencyLimitMhz.Value, deadline.Token);
            }

            var readBack = await limitTransport.ReadCoreLimitAsync(deadline.Token);
            lastKnown = readBack;
            if (readBack != plan.CoreFrequencyLimitMhz)
            {
                return await RestoreGpuLimitAsync(
                    operationId, limitTransport, range, previous, ErrorCode.ReadBackMismatch, readBack);
            }

            return new GpuLimitCommandResult(
                operationId, CommandState.Applied, readBack, RequiredUserAction.None, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (writeAttempted && range is not null)
            {
                _ = await RestoreGpuLimitAsync(
                    operationId, limitTransport, range, previous, ErrorCode.HardwareWriteFailed, lastKnown);
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            return writeAttempted && range is not null
                ? await RestoreGpuLimitAsync(
                    operationId, limitTransport, range, previous, ErrorCode.DeadlineExceeded, lastKnown)
                : RejectedLimit(operationId, ErrorCode.DeadlineExceeded);
        }
        catch (TimeoutException)
        {
            return writeAttempted && range is not null
                ? await RestoreGpuLimitAsync(
                    operationId, limitTransport, range, previous, ErrorCode.DeadlineExceeded, lastKnown)
                : RejectedLimit(operationId, ErrorCode.DeadlineExceeded);
        }
        catch
        {
            return writeAttempted && range is not null
                ? await RestoreGpuLimitAsync(
                    operationId, limitTransport, range, previous, ErrorCode.HardwareWriteFailed, lastKnown)
                : RejectedLimit(operationId, ErrorCode.HardwareWriteFailed);
        }
    }

    private static async Task<GpuLimitCommandResult> RestoreGpuLimitAsync(
        Guid operationId,
        IGpuLimitTransport transport,
        GpuFrequencyRange range,
        int? previous,
        ErrorCode cause,
        int? lastKnown)
    {
        try
        {
            using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (previous is null)
                await transport.ResetCoreLimitAsync(recoveryDeadline.Token);
            else
                await transport.ApplyCoreLimitAsync(range.MinimumMhz, previous.Value, recoveryDeadline.Token);

            var restored = await transport.ReadCoreLimitAsync(recoveryDeadline.Token);
            return new GpuLimitCommandResult(
                operationId,
                restored == previous ? CommandState.RolledBack : CommandState.RecoveryRequired,
                restored,
                RequiredUserAction.None,
                ServiceError.Create(
                    restored == previous ? cause : ErrorCode.RollbackFailed,
                    operationId,
                    false));
        }
        catch
        {
            return new GpuLimitCommandResult(
                operationId,
                CommandState.RecoveryRequired,
                lastKnown,
                RequiredUserAction.None,
                ServiceError.Create(ErrorCode.RollbackFailed, operationId, false));
        }
    }

    private static MuxCommandResult Rejected(Guid operationId, ErrorCode code) =>
        new(operationId, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, operationId, false));

    private static async Task<MuxCommandResult> RestoreMuxAsync(
        IMuxTransport transport,
        Guid operationId,
        MuxMode previous,
        ErrorCode restoredError,
        MuxMode? lastKnown)
    {
        try
        {
            await transport.WriteMuxAsync(previous, CancellationToken.None);
            var restored = await transport.ReadMuxAsync(CancellationToken.None);
            return new MuxCommandResult(
                operationId,
                restored == previous ? CommandState.RolledBack : CommandState.RecoveryRequired,
                new VerifiedMuxState(restored),
                RequiredUserAction.None,
                ServiceError.Create(restored == previous ? restoredError : ErrorCode.RollbackFailed, operationId, false));
        }
        catch
        {
            return new MuxCommandResult(
                operationId,
                CommandState.RecoveryRequired,
                lastKnown is { } mode ? new VerifiedMuxState(mode) : null,
                RequiredUserAction.None,
                ServiceError.Create(ErrorCode.RollbackFailed, operationId, false));
        }
    }

    private static GpuLimitCommandResult RejectedLimit(Guid operationId, ErrorCode code) =>
        new(operationId, CommandState.Rejected, null, RequiredUserAction.None,
            ServiceError.Create(code, operationId, false));
}
