using Jiaolong.Service.Hosting;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong.Diagnostics;

namespace Jiaolong.Service;

public sealed class ServiceWorker(
    ServiceLifetimeCoordinator lifetimeCoordinator,
    ILogger<ServiceWorker> logger,
    NamedPipeServiceHost pipeHost,
    HomeServiceRuntime homeRuntime,
    IDiagnosticEventWriter diagnosticEvents) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Jiaolong control service started in console or SCM mode.");
        await TryWriteEventAsync(DiagnosticEventNames.ServiceStarted, stoppingToken);
        try
        {
            try
            {
                var state = await homeRuntime.InitializeAsync(stoppingToken);
                logger.LogInformation("Homepage hardware state: {state}, reason: {reason}", state.SupportState, state.Reason);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Homepage hardware initialization failed; IPC will expose the failure.");
            }

            await Task.WhenAll(
                lifetimeCoordinator.RunAsync(stoppingToken),
                pipeHost.RunAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await TryWriteEventAsync(DiagnosticEventNames.ServiceStopping, CancellationToken.None);
            await pipeHost.DisposeAsync();
            logger.LogInformation("Jiaolong control service stopping.");
        }
    }

    private async Task TryWriteEventAsync(string eventName, CancellationToken cancellationToken)
    {
        try
        {
            await diagnosticEvents.WriteAsync(new DiagnosticEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                EventName = eventName,
                ServiceVersion = typeof(ServiceWorker).Assembly.GetName().Version?.ToString()
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not write diagnostic event {eventName}.", eventName);
        }
    }
}
