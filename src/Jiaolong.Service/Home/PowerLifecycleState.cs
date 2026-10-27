namespace Jiaolong.Service.Home;

// One atomic word keeps the pause and its transition version consistent across readers and timers.
public sealed class PowerLifecycleState
{
    private long state;
    private readonly AsyncLocal<RecoveryReadScope?> recoveryReads = new();
    internal PowerLifecycleState(IHomeHardwareProvider provider) => provider.SetBackgroundWorkState(this);
    public bool IsPaused => (Volatile.Read(ref state) & 1) != 0;
    internal long ReadState => Volatile.Read(ref state);
    internal long Version => Volatile.Read(ref state) & ~1L;

    public bool IsReadAllowed
    {
        get
        {
            long current = Volatile.Read(ref state);
            var scope = recoveryReads.Value;
            return (current & 1) == 0 || scope is { IsActive: true } && scope.Version == (current & ~1L);
        }
    }

    internal IDisposable AllowRecoveryReads(long version)
    {
        if (!IsPaused || Version != version) throw new OperationCanceledException("staleRecoveryRead");
        var scope = new RecoveryReadScope(this, version, recoveryReads.Value);
        recoveryReads.Value = scope;
        return scope;
    }

    private sealed class RecoveryReadScope(PowerLifecycleState owner, long version, RecoveryReadScope? prior) : IDisposable
    {
        private int active = 1;
        public long Version { get; } = version;
        public bool IsActive => Volatile.Read(ref active) != 0;
        public void Dispose()
        {
            // Revocation also reaches execution contexts captured inside the scope.
            Interlocked.Exchange(ref active, 0);
            owner.recoveryReads.Value = prior;
        }
    }

    internal long Pause()
    {
        long before, after;
        do
        {
            before = Volatile.Read(ref state);
            after = ((before & ~1L) + 2) | 1;
        } while (Interlocked.CompareExchange(ref state, after, before) != before);
        return after & ~1L;
    }

    internal long? TryPauseForShutdown(long expectedState)
    {
        if ((expectedState & 1) != 0) return null;
        long after = (expectedState + 2) | 1;
        return Interlocked.CompareExchange(ref state, after, expectedState) == expectedState ? after & ~1L : null;
    }

    internal bool Hold(long version)
    {
        long before = Volatile.Read(ref state);
        return (before & ~1L) == version && Interlocked.CompareExchange(ref state, version | 1, before) == before;
    }

    internal bool Resume(long version) => Interlocked.CompareExchange(ref state, version, version | 1) == (version | 1);
}
