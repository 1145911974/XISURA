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
        SystemPowerNotifications? power = null;
        try
        {
            // Accept the IPC handshake before slow firmware/driver discovery starts.
            var pipeTask = pipeHost.RunAsync(stoppingToken);
            try
            {
                power = new SystemPowerNotifications((resume, version, token) => homeRuntime.HandleLifecycleAsync(resume, token, version),
                    logger, stoppingToken, prepare: homeRuntime.PreparePowerTransition);
            }
            catch (Exception error) { logger.LogWarning(error, "Service suspend/resume notification registration failed."); }
            try
            {
                var state = await Task.Run(() => homeRuntime.InitializeAsync(stoppingToken), stoppingToken);
                logger.LogInformation("Homepage hardware state: {state}, reason: {reason}", state.SupportState, state.Reason);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Homepage hardware initialization failed; IPC will expose the failure.");
            }

            await Task.WhenAll(
                lifetimeCoordinator.RunAsync(stoppingToken),
                pipeTask);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await TryWriteEventAsync(DiagnosticEventNames.ServiceStopping, CancellationToken.None);
            if (power is not null) await power.DisposeAsync();
            using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await homeRuntime.HandleShutdownAsync(releaseDeadline.Token); }
            catch (Exception error) { TryLogShutdown(() => logger.LogWarning(error, "Service fan release failed; pending state retained."), $"Service fan release failed; pending state retained: {error}"); }
            await pipeHost.DisposeAsync();
            TryLogShutdown(() => logger.LogInformation("Jiaolong control service stopping."), "Jiaolong control service stopping.");
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
                ServiceVersion = typeof(ServiceWorker).Assembly.GetName().Version?.ToString(),
                CorrelationId = homeRuntime.SessionId,
                Fields = new Dictionary<string, string> { ["sessionId"] = homeRuntime.SessionId.ToString("N") }
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            TryLogShutdown(() => logger.LogWarning(exception, "Could not write diagnostic event {eventName}.", eventName),
                $"Could not write diagnostic event {eventName}: {exception}");
        }
    }

    private static void TryLogShutdown(Action log, string fallback)
    {
        try { log(); }
        catch (Exception error)
        {
            try { System.Diagnostics.Trace.WriteLine($"{fallback}; logger failed: {error}"); }
            catch { }
        }
    }
}
