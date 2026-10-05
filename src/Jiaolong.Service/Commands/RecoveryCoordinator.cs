using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions;

namespace Jiaolong.Service.Commands;

public interface IRecoveryCoordinator
{
    Task<RecoveryResult> RecoverStartupStateAsync(CancellationToken cancellationToken);
    Task<RecoveryResult> ReleaseVolatileControlsAsync(ReleaseReason reason, CancellationToken cancellationToken);
}

public sealed record RecoveryResult(bool Succeeded, ErrorCode? ErrorCode = null);

public sealed class RecoveryCoordinator(IHardwareAdapter adapter) : IRecoveryCoordinator
{
    public Task<RecoveryResult> RecoverStartupStateAsync(CancellationToken cancellationToken) =>
        ReleaseVolatileControlsAsync(ReleaseReason.SafetyFallback, cancellationToken);

    public async Task<RecoveryResult> ReleaseVolatileControlsAsync(ReleaseReason reason, CancellationToken cancellationToken)
    {
        try
        {
            await adapter.ReleaseFanControlAsync(reason, cancellationToken);
            return new RecoveryResult(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RecoveryResult(false, ErrorCode.RollbackFailed);
        }
    }

    public Task<RecoveryResult> ReleaseForSuspendAsync(CancellationToken cancellationToken) =>
        ReleaseVolatileControlsAsync(ReleaseReason.SystemSuspend, cancellationToken);

    public Task<RecoveryResult> ReleaseForShutdownAsync(CancellationToken cancellationToken) =>
        ReleaseVolatileControlsAsync(ReleaseReason.ServiceStopping, cancellationToken);
}
