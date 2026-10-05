namespace Jiaolong.Service.Commands;

public sealed class CircuitBreaker
{
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private int communicationFailures;
    private DateTimeOffset? openedUntilUtc;
    private bool readProbeInProgress;

    public CircuitBreaker(TimeProvider? timeProvider = null) => this.timeProvider = timeProvider ?? TimeProvider.System;

    public bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return openedUntilUtc is not null;
            }
        }
    }

    public bool CanWrite
    {
        get
        {
            lock (gate) return openedUntilUtc is null;
        }
    }

    public void RecordCommunicationFailure()
    {
        lock (gate)
        {
            communicationFailures++;
            if (communicationFailures >= 3) openedUntilUtc = timeProvider.GetUtcNow().Add(OpenDuration);
        }
    }

    public void RecordSuccess()
    {
        lock (gate)
        {
            communicationFailures = 0;
            openedUntilUtc = null;
            readProbeInProgress = false;
        }
    }

    public void Open()
    {
        lock (gate) openedUntilUtc = timeProvider.GetUtcNow().Add(OpenDuration);
    }

    public bool TryBeginReadProbe()
    {
        lock (gate)
        {
            if (openedUntilUtc is not { } until || until > timeProvider.GetUtcNow() || readProbeInProgress) return false;
            readProbeInProgress = true;
            return true;
        }
    }

    public void CompleteReadProbe(bool succeeded)
    {
        lock (gate)
        {
            readProbeInProgress = false;
            if (succeeded)
            {
                communicationFailures = 0;
                openedUntilUtc = null;
            }
            else
            {
                openedUntilUtc = timeProvider.GetUtcNow().Add(OpenDuration);
            }
        }
    }
}
