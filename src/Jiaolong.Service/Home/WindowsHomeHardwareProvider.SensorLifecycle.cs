namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private volatile bool sensorLifecycleUnknown;
    private volatile bool sensorInitializationDeferred;
    private bool sensorDependencyCleanupStarted;
    private sealed record SensorAccess(PowerLifecycleState? Lifecycle, long State, long LegacyState);

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
            if (sensorLifecycleUnknown || BackgroundWorkPaused)
            {
                TryLogShutdown(() => logger?.LogWarning("Sensor shutdown deferred; opened={Opened}, lifecycleUnknown={Unknown}, paused={Paused}",
                    opened, sensorLifecycleUnknown, BackgroundWorkPaused),
                    $"Sensor shutdown deferred; opened={opened}, lifecycleUnknown={sensorLifecycleUnknown}, paused={BackgroundWorkPaused}.");
                return;
            }
            try
            {
                var access = CaptureSensorAccess();
                CloseSensorComputer(access, allowDisposed: true);
                if (sensorDependencyCleanupStarted) return;
                EnsureSensorDriverAccess(access, allowDisposed: true);
                sensorDependencyCleanupStarted = true;
                DisposeCpuTuning();
                packagePowerReader.Dispose();
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
