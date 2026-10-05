using Jiaolong.Hardware.Mechrevo.Telemetry;

namespace Jiaolong.Service.Telemetry;

public sealed record TelemetryUpdatedEventArgs(TelemetrySample Sample);

public sealed class TelemetryPublisher
{
    public const string EventName = "telemetry.updated";

    private readonly object gate = new();
    private readonly Func<CancellationToken, Task<TelemetrySample>>? reader;
    private readonly Dictionary<Guid, Subscription> subscribers = new();

    public TelemetryPublisher(Func<CancellationToken, Task<TelemetrySample>>? reader = null) => this.reader = reader;

    public IDisposable Subscribe(Func<TelemetrySample, Task> callback, TimeSpan minimumInterval)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (minimumInterval < TimeSpan.FromSeconds(1)) minimumInterval = TimeSpan.FromSeconds(1);
        var id = Guid.NewGuid();
        lock (gate) subscribers[id] = new Subscription(callback, minimumInterval);
        return new SubscriptionHandle(this, id);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (reader is null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var now = DateTimeOffset.UtcNow;
            Subscription[] due;
            lock (gate)
            {
                due = subscribers.Values.Where(subscription =>
                        subscription.LastPublishedAtUtc is null ||
                        now - subscription.LastPublishedAtUtc >= subscription.MinimumInterval)
                    .ToArray();
            }

            if (due.Length == 0) continue;

            TelemetrySample sample;
            try
            {
                sample = await reader(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            foreach (var subscription in due)
            {
                subscription.LastPublishedAtUtc = now;
                try { await subscription.Callback(sample); } catch { }
            }
        }
    }

    private void Remove(Guid id)
    {
        lock (gate) subscribers.Remove(id);
    }

    private sealed class Subscription(Func<TelemetrySample, Task> callback, TimeSpan minimumInterval)
    {
        public Func<TelemetrySample, Task> Callback { get; } = callback;
        public TimeSpan MinimumInterval { get; } = minimumInterval;
        public DateTimeOffset? LastPublishedAtUtc { get; set; }
    }

    private sealed class SubscriptionHandle(TelemetryPublisher publisher, Guid id) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) publisher.Remove(id);
        }
    }
}
