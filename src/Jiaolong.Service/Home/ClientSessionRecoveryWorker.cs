using Jiaolong.Contracts.Commands;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jiaolong.Service.Home;

public sealed class ClientSessionRecoveryWorker(HomeServiceRuntime runtime, IClientProcessProbe probe,
    ILogger<ClientSessionRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        DateTimeOffset retryAfter = default;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (DateTimeOffset.UtcNow < retryAfter) continue;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                var result = await runtime.RecoverExitedClientSessionAsync(probe, deadline.Token);
                if (result is { State: not CommandState.Applied })
                {
                    logger.LogWarning("Client exit recovery retained evidence: operation={operation}, state={state}, error={error}, reason={reason}",
                        result.OperationId, result.State, result.Error?.Code, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
                    retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Client process recovery could not complete; original records retained");
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }
    }
}
