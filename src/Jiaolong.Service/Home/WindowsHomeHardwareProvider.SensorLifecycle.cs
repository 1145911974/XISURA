namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private volatile bool sensorLifecycleUnknown;
    private volatile bool sensorInitializationDeferred;
    private bool sensorDependencyCleanupStarted;
    private bool sensorDependencyCleanupCompleted;
    private int sensorShutdownRequested;
    private SensorAccess? sensorShutdownAccess;
    private sealed record SensorAccess(PowerLifecycleState? Lifecycle, long State, long LegacyState);

    public long? PrepareShutdownCleanup()
    {
        Volatile.Write(ref sensorShutdownRequested, 1);
        Volatile.Write(ref sensorShutdownAccess, null);
        var access = CaptureSensorAccess();
        if (disposed || sensorLifecycleUnknown || (access.LegacyState & 1) != 0 || access.Lifecycle is null) return null;
        var version = access.Lifecycle.TryPauseForShutdown(access.State);
        if (version is not null)
            Volatile.Write(ref sensorShutdownAccess, access with { State = version.Value | 1 });
        return version;
    }

    private bool HasSensorShutdownAccess(SensorAccess access) =>
        (access.LegacyState & 1) == 0 && access == Volatile.Read(ref sensorShutdownAccess) && access == CaptureSensorAccess();

    private SensorAccess CaptureSensorAccess()
    {
        var lifecycle = Volatile.Read(ref powerLifecycle);
        return new(lifecycle, lifecycle?.ReadState ?? 0, Volatile.Read(ref backgroundWorkPaused));
    }

    private void EnsureSensorReadAccess(SensorAccess access, bool allowDisposed = false)
    {
        if (!allowDisposed && disposed) throw new ObjectDisposedException(nameof(WindowsHomeHardwareProvider));
        EnsureProviderReadAccess();
        if (!ReferenceEquals(access.Lifecycle, Volatile.Read(ref powerLifecycle)) ||
            access.State != (access.Lifecycle?.ReadState ?? 0) || access.LegacyState != Volatile.Read(ref backgroundWorkPaused))
            throw new OperationCanceledException("staleSensorLifecycle");
    }

    private void EnsureSensorDriverAccess(SensorAccess access, bool allowDisposed = false)
    {
        if (allowDisposed && Volatile.Read(ref sensorShutdownRequested) != 0)
        {
            if (!HasSensorShutdownAccess(access)) throw new OperationCanceledException("staleSensorShutdown");
            if (sensorLifecycleUnknown) throw new IOException("sensorLifecycleUnknown");
            return;
        }
        EnsureSensorReadAccess(access, allowDisposed);
        if (BackgroundWorkPaused) throw new IOException("hardwareAccessPaused");
        if (sensorLifecycleUnknown) throw new IOException("sensorLifecycleUnknown");
    }

    internal void ReinitializeSensorComputer(CancellationToken token)
    {
        var access = CaptureSensorAccess();
        lock (sensorGate)
        {
            EnsureSensorReadAccess(access);
            if (sensorLifecycleUnknown) throw new IOException("sensorLifecycleUnknown");
            if (BackgroundWorkPaused)
            {
                if (!opened && !sensorInitializationDeferred)
                {
                    sensorInitializationDeferred = true;
                    logger?.LogInformation("Sensor initialization deferred until recovery read verification releases the pause; no fresh sensor sample is available");
                }
            }
            else
            {
                CloseSensorComputer(access);
                token.ThrowIfCancellationRequested();
                OpenSensorComputer(access);
            }
        }
        token.ThrowIfCancellationRequested();
        EnsureSensorReadAccess(access);
    }

    // Caller owns sensorGate. Known success is recorded before checking whether permission changed in flight.
    private void OpenSensorComputer(SensorAccess access)
    {
        EnsureSensorDriverAccess(access);
        try { openSensorComputer(); }
        catch { sensorLifecycleUnknown = true; throw; }
        opened = true;
        if (sensorInitializationDeferred)
        {
            sensorInitializationDeferred = false;
            logger?.LogInformation("Deferred sensor initialization returned successfully; sampling still requires lifecycle verification");
        }
        EnsureSensorDriverAccess(access);
    }

    private void CloseSensorComputer(SensorAccess access, bool allowDisposed = false)
    {
        EnsureSensorDriverAccess(access, allowDisposed);
        if (opened)
        {
            try { closeSensorComputer(); }
            catch { sensorLifecycleUnknown = true; throw; }
            opened = false;
            Volatile.Write(ref sensorSnapshot, new([], null, null, DateTimeOffset.MinValue));
        }
        EnsureSensorDriverAccess(access, allowDisposed);
    }

    private void DisposeSensorResources()
    {
        lock (sensorGate)
        {
            if (sensorDependencyCleanupCompleted) return;
            var access = CaptureSensorAccess();
            bool shutdownAllowed = Volatile.Read(ref sensorShutdownRequested) != 0
                ? HasSensorShutdownAccess(access) : !BackgroundWorkPaused;
            if (sensorLifecycleUnknown || !shutdownAllowed)
            {
                TryLogShutdown(() => logger?.LogWarning("Sensor shutdown deferred; opened={Opened}, lifecycleUnknown={Unknown}, paused={Paused}",
                    opened, sensorLifecycleUnknown, BackgroundWorkPaused),
                    $"Sensor shutdown deferred; opened={opened}, lifecycleUnknown={sensorLifecycleUnknown}, paused={BackgroundWorkPaused}.");
                return;
            }
            try
            {
                CloseSensorComputer(access, allowDisposed: true);
                if (sensorDependencyCleanupStarted) return;
                EnsureSensorDriverAccess(access, allowDisposed: true);
                sensorDependencyCleanupStarted = true;
                DisposeCpuTuning(() => EnsureSensorDriverAccess(access, allowDisposed: true));
                EnsureSensorDriverAccess(access, allowDisposed: true);
                packagePowerReader.Dispose();
                EnsureSensorDriverAccess(access, allowDisposed: true);
                sensorDependencyCleanupCompleted = true;
                TryLogShutdown(() => logger?.LogInformation("Sensor shutdown completed; opened={Opened}, dependencyCleanupCompleted={Completed}",
                    opened, sensorDependencyCleanupCompleted), "Sensor shutdown completed; dependencies disposed.");
            }
            catch (Exception error)
            {
                // An opaque failure retains ownership; repeating Dispose must not reissue an unknown driver call.
                TryLogShutdown(() => logger?.LogWarning(error, "Sensor shutdown incomplete; opened={Opened}, lifecycleUnknown={Unknown}, dependencyCleanupStarted={Started}",
                    opened, sensorLifecycleUnknown, sensorDependencyCleanupStarted),
                    $"Sensor shutdown incomplete: {error}; opened={opened}, lifecycleUnknown={sensorLifecycleUnknown}, dependencyCleanupStarted={sensorDependencyCleanupStarted}.");
            }
        }
    }
}
