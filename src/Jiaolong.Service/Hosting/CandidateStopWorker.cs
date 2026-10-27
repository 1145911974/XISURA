namespace Jiaolong.Service.Hosting;

internal sealed class CandidateStopWorker(string stopFile, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!File.Exists(stopFile)) continue;
            lifetime.StopApplication();
            return;
        }
    }
}
