namespace Jiaolong_ControlCenter.Prototype;

public sealed class PrototypeStrongCoolingCommandCoordinator(
    Func<bool, CancellationToken, Task> sendAsync)
{
    private readonly object gate = new();
    private bool? pending;
    private Task? processing;

    public Task RequestAsync(bool enabled, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            pending = enabled;
            return processing ??= ProcessAsync(cancellationToken);
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                bool target;
                lock (gate)
                {
                    if (pending is not bool next)
                    {
                        // Publish the idle state while holding the same lock used by
                        // RequestAsync; a request arriving at the drain boundary can
                        // therefore start a new worker instead of being stranded.
                        processing = null;
                        return;
                    }

                    target = next;
                    pending = null;
                }

                await sendAsync(target, cancellationToken);
            }
        }
        catch
        {
            lock (gate)
            {
                pending = null;
                processing = null;
            }
            throw;
        }
    }
}
