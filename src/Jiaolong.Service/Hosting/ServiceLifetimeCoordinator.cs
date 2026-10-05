namespace Jiaolong.Service.Hosting;

using Jiaolong.Contracts.Models;
using Jiaolong.Service.Commands;

public sealed class ServiceLifetimeCoordinator(IRecoveryCoordinator? recoveryCoordinator = null)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (recoveryCoordinator is not null)
        {
            var recovery = await recoveryCoordinator.RecoverStartupStateAsync(stoppingToken);
            if (!recovery.Succeeded)
            {
                throw new InvalidOperationException($"Startup recovery failed: {recovery.ErrorCode}");
            }
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        finally
        {
            if (recoveryCoordinator is not null)
            {
                await recoveryCoordinator.ReleaseVolatileControlsAsync(
                    ReleaseReason.ServiceStopping, CancellationToken.None);
            }
        }
    }
}
