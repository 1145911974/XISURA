using System.Reflection;
using Microsoft.Extensions.Logging;
using Jiaolong.Service.Home;
using LibreHardwareMonitor.Hardware;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class SensorLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void Queued_samples_stop_after_pause_or_any_intervening_transition()
    {
        foreach (string transition in new[] { "pause", "sharedCycle", "legacyCycle", "scopeRevoked", "scopeResume" })
        {
            int opens = 0, updates = 0;
            var device = Hardware(() => updates++);
            using var provider = new WindowsHomeHardwareProvider(() => opens++, () => { }, () => [device]);
            var lifecycle = new PowerLifecycleState(provider);
            object original = Field(provider, "sensorSnapshot")!;
            object sensorGate = Field(provider, "sensorGate")!;
            Task sample;
            Monitor.Enter(sensorGate);
            try
            {
                IDisposable? scope = null;
                if (transition.StartsWith("scope")) scope = lifecycle.AllowRecoveryReads(lifecycle.Pause());
                Queue(provider);
                sample = (Task)Field(provider, "sensorSamplingTask")!;
                if (transition == "pause") lifecycle.Pause();
                if (transition == "sharedCycle") Assert.IsTrue(lifecycle.Resume(lifecycle.Pause()));
                if (transition == "legacyCycle") { provider.SetBackgroundWorkPaused(true); provider.SetBackgroundWorkPaused(false); }
                scope?.Dispose();
                if (transition == "scopeResume") Assert.IsTrue(lifecycle.Resume(lifecycle.Version));
            }
            finally { Monitor.Exit(sensorGate); }
            Assert.IsTrue(sample.Wait(TimeSpan.FromSeconds(5)), transition);
            Assert.AreEqual(0, opens, transition);
            Assert.AreEqual(0, updates, transition);
            Assert.AreSame(original, Field(provider, "sensorSnapshot"), transition);
        }
    }

    [TestMethod]
    public void Interruption_during_update_stops_children_and_never_publishes_stale_samples()
    {
        foreach (string transition in new[] { "pause", "sharedCycle", "legacyCycle", "failure" })
        {
            int children = 0, later = 0;
            WindowsHomeHardwareProvider? current = null;
            var child = Hardware(() => children++);
            var device = Hardware(() =>
            {
                if (transition == "sharedCycle")
                {
                    var lifecycle = (PowerLifecycleState)Field(current!, "powerLifecycle")!;
                    Assert.IsTrue(lifecycle.Resume(lifecycle.Pause()));
                }
                else
                {
                    current!.SetBackgroundWorkPaused(true);
                    if (transition == "legacyCycle") current.SetBackgroundWorkPaused(false);
                }
                if (transition == "failure") throw new IOException("fakeUpdateFailed");
            }, child);
            using var provider = current = new WindowsHomeHardwareProvider(() => { }, () => { }, () => [device, Hardware(() => later++)]);
            _ = new PowerLifecycleState(provider);
            object original = Field(provider, "sensorSnapshot")!;
            Queue(provider);
            Assert.IsTrue(((Task)Field(provider, "sensorSamplingTask")!).Wait(TimeSpan.FromSeconds(5)), transition);
            Assert.AreEqual(0, children, transition);
            Assert.AreEqual(0, later, transition);
            Assert.AreSame(original, Field(provider, "sensorSnapshot"), transition);
        }
    }

    [TestMethod]
    public void Recovery_reads_reuse_open_devices_without_cold_initialization_and_can_be_revoked()
    {
        foreach (bool alreadyOpen in new[] { false, true })
        {
            int opens = 0, updates = 0;
            using var provider = new WindowsHomeHardwareProvider(() => opens++, () => { }, () => [Hardware(() => updates++)]);
            Set(provider, "opened", alreadyOpen);
            var lifecycle = new PowerLifecycleState(provider);
            long version = lifecycle.Pause();
            using (lifecycle.AllowRecoveryReads(version))
            {
                provider.ReinitializeSensorComputer(CancellationToken.None);
                Assert.AreEqual(!alreadyOpen, (bool)Field(provider, "sensorInitializationDeferred")!);
                Queue(provider);
                var sample = Field(provider, "sensorSamplingTask") as Task;
                Assert.IsTrue(sample is null || sample.Wait(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(0, opens);
                Assert.AreEqual(alreadyOpen ? 1 : 0, updates);
            }
            object original = Field(provider, "sensorSnapshot")!;
            Queue(provider);
            var after = Field(provider, "sensorSamplingTask") as Task;
            Assert.IsTrue(after is null || after.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreSame(original, Field(provider, "sensorSnapshot"));
            Assert.IsTrue(lifecycle.Resume(version));
            Queue(provider);
            Assert.IsTrue(((Task)Field(provider, "sensorSamplingTask")!).Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(alreadyOpen ? 0 : 1, opens, "Cold initialization is allowed only after recovery verification releases the pause.");
            Assert.AreEqual(1, updates);
            Assert.IsFalse((bool)Field(provider, "sensorInitializationDeferred")!);
        }

        int closes = 0, reopened = 0, resumedUpdates = 0;
        PowerLifecycleState? interruptedLifecycle = null;
        using var interrupted = new WindowsHomeHardwareProvider(() => reopened++, () =>
        {
            closes++;
            interruptedLifecycle!.Pause();
        }, () => [Hardware(() => resumedUpdates++)]);
        interruptedLifecycle = new PowerLifecycleState(interrupted);
        Set(interrupted, "opened", true);
        Assert.ThrowsExactly<IOException>(() => interrupted.ReinitializeSensorComputer(CancellationToken.None));
        Assert.IsFalse((bool)Field(interrupted, "opened")!);
        Assert.IsFalse((bool)Field(interrupted, "sensorLifecycleUnknown")!);
        long interruptedVersion = interruptedLifecycle.Version;
        using (interruptedLifecycle.AllowRecoveryReads(interruptedVersion))
        {
            interrupted.ReinitializeSensorComputer(CancellationToken.None);
            Assert.AreEqual(0, reopened);
            Assert.AreEqual(1, closes);
        }
        Assert.IsTrue(interruptedLifecycle.Resume(interruptedVersion));
        Queue(interrupted);
        Assert.IsTrue(((Task)Field(interrupted, "sensorSamplingTask")!).Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, reopened);
        Assert.AreEqual(1, resumedUpdates);
        Assert.IsFalse((bool)Field(interrupted, "sensorInitializationDeferred")!);
    }

    [TestMethod]
    public void Close_is_deferred_while_paused_and_failed_initialization_is_not_reissued()
    {
        int closes = 0;
        var provider = new WindowsHomeHardwareProvider(() => { }, () => closes++, () => []);
        Set(provider, "opened", true);
        provider.SetBackgroundWorkPaused(true);
        provider.Dispose();
        Assert.AreEqual(0, closes);
        Assert.IsTrue((bool)Field(provider, "opened")!);
        provider.SetBackgroundWorkPaused(false);
        provider.Dispose();
        provider.Dispose();
        Assert.AreEqual(1, closes);
        Assert.IsFalse((bool)Field(provider, "opened")!);

        int opens = 0;
        using var failed = new WindowsHomeHardwareProvider(() => { opens++; throw new IOException("fakeOpenUnknown"); }, () => closes++, () => []);
        _ = failed.ReinitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _ = failed.ReinitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreEqual(1, opens, "An opaque failed initialization must not be blindly repeated.");

        foreach (bool throws in new[] { false, true })
        {
            int attempts = 0;
            WindowsHomeHardwareProvider? closing = null;
            var owner = closing = new WindowsHomeHardwareProvider(() => { }, () =>
            {
                attempts++;
                if (throws) throw new IOException("fakeCloseUnknown");
                closing!.SetBackgroundWorkPaused(true);
            }, () => [], throws ? new ThrowingLogger() : null);
            Set(owner, "opened", true);
            owner.Dispose();
            Assert.AreEqual(1, attempts);
            Assert.AreEqual(throws, (bool)Field(owner, "opened")!);
            Assert.AreEqual(throws, (bool)Field(owner, "sensorLifecycleUnknown")!);
            Assert.IsFalse((bool)Field(owner, "sensorDependencyCleanupStarted")!);
            owner.SetBackgroundWorkPaused(false);
            owner.Dispose();
            owner.Dispose();
            Assert.AreEqual(1, attempts, "Known close success and unknown close failure both prevent redispatch.");
            Assert.AreEqual(!throws, (bool)Field(owner, "sensorDependencyCleanupStarted")!);
        }

        var deferredLogger = new ThrowingLogger();
        using (var deferred = new WindowsHomeHardwareProvider(() => { }, () => { Assert.Fail("Unknown lifecycle must not close"); }, () => [], deferredLogger))
        {
            Set(deferred, "opened", true);
            Set(deferred, "sensorLifecycleUnknown", true);
            deferred.Dispose();
            Assert.IsTrue((bool)Field(deferred, "sensorLifecycleUnknown")!);
            Assert.IsTrue((bool)Field(deferred, "opened")!);
            Assert.IsFalse((bool)Field(deferred, "sensorDependencyCleanupStarted")!);
            Assert.IsTrue(deferredLogger.Calls > 0);
        }

        using var updating = new ManualResetEventSlim();
        using var releaseUpdate = new ManualResetEventSlim();
        WindowsHomeHardwareProvider? sampling = null;
        int concurrentCloses = 0;
        using var concurrent = sampling = new WindowsHomeHardwareProvider(() => { }, () => concurrentCloses++, () => [Hardware(() =>
        {
            updating.Set();
            if (!releaseUpdate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fakeUpdateTimeout");
            sampling!.SetBackgroundWorkPaused(true);
        })]);
        Set(concurrent, "opened", true);
        Queue(concurrent);
        Assert.IsTrue(updating.Wait(TimeSpan.FromSeconds(5)));
        Task reinitialize = Task.Run(() => concurrent.ReinitializeAsync(CancellationToken.None));
        object controlGate = Field(concurrent, "gate")!;
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(controlGate)) return true;
                Monitor.Exit(controlGate);
                return false;
            }, TimeSpan.FromSeconds(2)), "Reinitialize holds the control gate while waiting for the sensor owner.");
            Assert.AreEqual(0, concurrentCloses, "Cannot close while a device update owns sensorGate.");
        }
        finally { releaseUpdate.Set(); }
        Assert.ThrowsExactly<IOException>(() => reinitialize.GetAwaiter().GetResult());
        Assert.IsTrue(((Task)Field(concurrent, "sensorSamplingTask")!).Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, concurrentCloses, "A pause during the update revokes the waiting reinitialize call.");
    }

    [TestMethod]
    public async Task Normal_shutdown_closes_sensors_once_without_reopening_hardware_access()
    {
        int closes = 0;
        using var provider = new WindowsHomeHardwareProvider(() => Assert.Fail("Shutdown must not open sensors"), () => closes++, () => []);
        Set(provider, "opened", true);
        var curve = new PawnIoCurveOptimizerTransport((_, _) => 0);
        var pbo = new PawnIoCurveOptimizerTransport((_, _) => 0);
        Set(provider, "curveOptimizer", curve);
        Set(provider, "pboScalarTransport", pbo);
        var runtime = new HomeServiceRuntime(new ReadOnlyHomeHardwareProvider(provider));

        await runtime.HandleShutdownAsync(CancellationToken.None);
        Assert.IsTrue(runtime.IsSuspended);
        var lifecycle = (PowerLifecycleState)Field(provider, "powerLifecycle")!;
        Assert.IsFalse(lifecycle.IsReadAllowed);
        provider.Dispose();
        provider.Dispose();

        Assert.AreEqual(1, closes);
        Assert.IsFalse((bool)Field(provider, "opened")!);
        Assert.IsTrue((bool)Field(curve, "disposed")!);
        Assert.IsTrue((bool)Field(pbo, "disposed")!);
        Assert.IsTrue((bool)Field(Field(provider, "packagePowerReader")!, "disposed")!);
        Assert.IsFalse(lifecycle.IsReadAllowed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => provider.ReinitializeSensorComputer(CancellationToken.None));
    }

    [TestMethod]
    public async Task Shutdown_does_not_authorize_existing_pause_or_unknown_sensors()
    {
        foreach (var reason in new[] { "powerPause", "legacyPause", "unknown" })
        {
            using var provider = new WindowsHomeHardwareProvider(() => { }, () => Assert.Fail(reason), () => []);
            Set(provider, "opened", true);
            var runtime = new HomeServiceRuntime(provider);
            if (reason == "powerPause") runtime.PreparePowerTransition(false);
            if (reason == "legacyPause") provider.SetBackgroundWorkPaused(true);
            if (reason == "unknown") Set(provider, "sensorLifecycleUnknown", true);

            await runtime.HandleShutdownAsync(CancellationToken.None);
            provider.Dispose();
            provider.Dispose();

            Assert.IsTrue(runtime.IsSuspended);
            Assert.IsTrue((bool)Field(provider, "opened")!);
            Assert.IsFalse((bool)Field(provider, "sensorDependencyCleanupStarted")!);
        }
    }

    [TestMethod]
    public async Task Any_intervening_transition_revokes_shutdown_cleanup()
    {
        using (var provider = new WindowsHomeHardwareProvider(() => { }, () => { }, () => []))
        {
            var lifecycle = new PowerLifecycleState(provider);
            long before = lifecycle.ReadState;
            lifecycle.Pause();
            long paused = lifecycle.ReadState;
            Assert.IsNull(lifecycle.TryPauseForShutdown(before));
            Assert.AreEqual(paused, lifecycle.ReadState, "A failed CAS must not authorize a newer suspend version.");
            Assert.IsNull(lifecycle.TryPauseForShutdown(paused));
        }
        foreach (var transition in new[] { "pause", "powerCycle", "legacyCycle", "replacement" })
        {
            using var provider = new WindowsHomeHardwareProvider(() => { }, () => Assert.Fail(transition), () => []);
            Set(provider, "opened", true);
            var runtime = new HomeServiceRuntime(provider);
            await runtime.HandleShutdownAsync(CancellationToken.None);
            var lifecycle = (PowerLifecycleState)Field(provider, "powerLifecycle")!;
            if (transition == "pause") lifecycle.Pause();
            if (transition == "powerCycle") Assert.IsTrue(lifecycle.Resume(lifecycle.Pause()));
            if (transition == "legacyCycle") { provider.SetBackgroundWorkPaused(true); provider.SetBackgroundWorkPaused(false); }
            if (transition == "replacement") _ = new PowerLifecycleState(provider);

            provider.Dispose();
            provider.Dispose();

            Assert.IsTrue((bool)Field(provider, "opened")!);
            Assert.IsFalse((bool)Field(provider, "sensorDependencyCleanupStarted")!);
        }
    }

    [TestMethod]
    public async Task Interrupted_shutdown_close_records_its_outcome_without_redispatch()
    {
        foreach (var interruption in new[] { "failure", "powerPause", "legacyCycle" })
        {
            int closes = 0;
            WindowsHomeHardwareProvider? current = null;
            using var provider = current = new WindowsHomeHardwareProvider(() => { }, () =>
            {
                closes++;
                if (interruption == "failure") throw new IOException("fakeShutdownCloseUnknown");
                if (interruption == "powerPause") ((PowerLifecycleState)Field(current!, "powerLifecycle")!).Pause();
                if (interruption == "legacyCycle") { current!.SetBackgroundWorkPaused(true); current.SetBackgroundWorkPaused(false); }
            }, () => []);
            Set(provider, "opened", true);
            var runtime = new HomeServiceRuntime(provider);
            await runtime.HandleShutdownAsync(CancellationToken.None);

            provider.Dispose();
            provider.Dispose();

            Assert.AreEqual(1, closes, interruption);
            Assert.AreEqual(interruption == "failure", (bool)Field(provider, "opened")!, interruption);
            Assert.AreEqual(interruption == "failure", (bool)Field(provider, "sensorLifecycleUnknown")!, interruption);
            Assert.IsFalse((bool)Field(provider, "sensorDependencyCleanupStarted")!, interruption);
        }
    }

    private static object? Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Queue(WindowsHomeHardwareProvider provider) =>
        typeof(WindowsHomeHardwareProvider).GetMethod("ReadSensorSnapshotLocked", Private)!.Invoke(provider, null);

    private static IHardware Hardware(Action update, params IHardware[] children)
    {
        var hardware = DispatchProxy.Create<IHardware, SensorHardwareProxy>();
        var fake = (SensorHardwareProxy)(object)hardware;
        fake.Update = update;
        fake.Children = children;
        fake.Sensors = [Sensor(SensorType.Load, "CPU Total", 25), Sensor(SensorType.Clock, "Core", 4000)];
        return hardware;
    }

    private static ISensor Sensor(SensorType type, string name, float value)
    {
        var sensor = DispatchProxy.Create<ISensor, SensorProxy>();
        var fake = (SensorProxy)(object)sensor;
        fake.Type = type; fake.Name = name; fake.Value = value;
        return sensor;
    }

    public class SensorHardwareProxy : DispatchProxy
    {
        public Action Update = () => { };
        public IHardware[] Children = [];
        public ISensor[] Sensors = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "Update") { Update(); return null; }
            return method?.Name switch
            {
                "get_HardwareType" => HardwareType.Cpu,
                "get_Name" => "fake CPU",
                "get_Sensors" => Sensors,
                "get_SubHardware" => Children,
                _ => throw new NotSupportedException(method?.Name)
            };
        }
    }

    public class SensorProxy : DispatchProxy
    {
        public SensorType Type;
        public string Name = "";
        public float? Value;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_SensorType" => Type,
            "get_Name" => Name,
            "get_Value" => Value,
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    private sealed class ThrowingLogger : ILogger<WindowsHomeHardwareProvider>
    {
        public int Calls { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls++;
            throw new IOException("fakeLoggerFailure");
        }
    }
}
