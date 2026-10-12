using System.Reflection;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class NvapiLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void Successful_initialize_has_one_unload_before_free_and_repeated_dispose_is_inert()
    {
        var bus = new Lifetime();
        var api = bus.Create();
        Assert.AreEqual(0, bus.Initializes, "The owner must register the object before native initialization.");
        _ = api.OpenSingleGpu();
        _ = api.OpenSingleGpu();
        Assert.AreEqual(1, bus.Initializes);
        api.Dispose(); api.Dispose();
        CollectionAssert.AreEqual(new[] { "initialize", "unload", "free" }, bus.Events);
        Assert.IsTrue(api.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => api.OpenSingleGpu());
    }

    [TestMethod]
    public void Pause_after_initialize_or_unload_retains_resources_without_duplicate_unload()
    {
        foreach (string stage in new[] { "initialize", "beforeDispose", "unload" })
        {
            var bus = new Lifetime();
            var api = bus.Create();
            if (stage == "initialize") bus.AfterInitialize = bus.Pause;
            if (stage == "initialize") Assert.ThrowsExactly<IOException>(() => api.OpenSingleGpu());
            else _ = api.OpenSingleGpu();
            if (stage == "beforeDispose") bus.Pause();
            if (stage == "unload") bus.AfterUnload = bus.Pause;
            api.Dispose();
            Assert.IsFalse(api.IsDisposed, stage);
            Assert.IsTrue(api.DisposalRequested, stage);
            Assert.AreEqual(stage == "unload" ? 1 : 0, bus.Unloads, stage);
            Assert.AreEqual(0, bus.Frees, stage);
            bus.ReadAllowed = true;
            Assert.IsFalse(api.TryDispose(), "Recovery read permission cannot release resources.");
            bus.Allowed = true;
            Assert.IsTrue(api.TryDispose());
            Assert.AreEqual(1, bus.Initializes);
            Assert.AreEqual(1, bus.Unloads);
            Assert.AreEqual(1, bus.Frees);
        }
    }

    [TestMethod]
    public void Failed_initialize_is_retained_as_unknown_and_failed_unload_never_frees_the_library()
    {
        var initialization = new Lifetime { InitializeStatus = -1 };
        var unknown = initialization.Create();
        Assert.ThrowsExactly<NvapiCallException>(() => unknown.OpenSingleGpu());
        Assert.IsTrue(unknown.InitializationUncertain);
        unknown.Dispose();
        Assert.IsFalse(unknown.IsDisposed);
        Assert.AreEqual(0, initialization.Unloads);
        Assert.AreEqual(0, initialization.Frees);
        Assert.ThrowsExactly<IOException>(() => unknown.OpenSingleGpu());
        Assert.AreEqual(1, initialization.Initializes);

        var release = new Lifetime { UnloadStatus = -209 };
        var api = release.Create();
        _ = api.OpenSingleGpu();
        api.Dispose();
        Assert.IsFalse(api.IsDisposed);
        Assert.IsInstanceOfType<NvapiCallException>(api.CleanupError);
        Assert.IsFalse(api.UnloadUncertain, "A returned refusal is distinct from an ambiguous dispatch.");
        Assert.AreEqual(0, release.Frees);
        release.UnloadStatus = 0;
        Assert.IsTrue(api.TryDispose());
        Assert.IsNull(api.CleanupError);
        Assert.AreEqual(2, release.Unloads);
        Assert.AreEqual(1, release.Frees);

        var unloadFailure = new Lifetime();
        var uncertainUnload = unloadFailure.Create();
        _ = uncertainUnload.OpenSingleGpu();
        unloadFailure.AfterUnload = () => throw new IOException("fakeUnloadUnknown");
        uncertainUnload.Dispose();
        Assert.IsTrue(uncertainUnload.UnloadUncertain);
        Assert.IsFalse(uncertainUnload.TryDispose());
        Assert.AreEqual(1, unloadFailure.Unloads, "An ambiguous unload must never decrement the reference again.");
        Assert.AreEqual(0, unloadFailure.Frees);

        var freeFailure = new Lifetime();
        var uncertainFree = freeFailure.Create();
        _ = uncertainFree.OpenSingleGpu();
        freeFailure.AfterFree = () => throw new IOException("fakeFreeUnknown");
        uncertainFree.Dispose();
        Assert.IsTrue(uncertainFree.LibraryReleaseUncertain);
        Assert.IsFalse(uncertainFree.TryDispose());
        Assert.AreEqual(1, freeFailure.Unloads);
        Assert.AreEqual(1, freeFailure.Frees, "An ambiguous release must never retry the same module handle.");
    }

    [TestMethod]
    public void Provider_keeps_active_context_and_drains_only_requested_cleanup_after_resume_or_shutdown()
    {
        var lifetimes = new List<Lifetime>();
        var provider = new WindowsHomeHardwareProvider(() =>
        {
            var bus = new Lifetime(); lifetimes.Add(bus); return bus.Create();
        }, () => true, _ => 45);
        var first = Create(provider);
        _ = first.OpenSingleGpu();
        var second = Create(provider);
        Assert.AreEqual(0, lifetimes[0].Unloads, "A nested refresh must not dispose the active transaction.");
        provider.SetBackgroundWorkPaused(true);
        lifetimes[0].Pause();
        first.Dispose();
        provider.Dispose();
        Assert.AreEqual(0, lifetimes[0].Unloads);
        Assert.IsFalse(first.IsDisposed);
        provider.SetBackgroundWorkPaused(false);
        lifetimes[0].Allowed = lifetimes[0].ReadAllowed = true;
        provider.Dispose();
        Assert.IsTrue(first.IsDisposed);
        Assert.AreEqual(1, lifetimes[0].Unloads);
        Assert.AreEqual(1, lifetimes[0].Frees);
        Assert.IsTrue(second.IsDisposed);
        var closed = Assert.ThrowsExactly<TargetInvocationException>(() => Create(provider));
        Assert.IsInstanceOfType<ObjectDisposedException>(closed.InnerException);
        Assert.AreEqual(2, lifetimes.Count);

        var refusing = new Lifetime { UnloadStatus = -209 };
        int factoryCalls = 0;
        var blockedProvider = new WindowsHomeHardwareProvider(() =>
        {
            factoryCalls++; return refusing.Create();
        }, () => true, _ => 45);
        var retained = Create(blockedProvider);
        _ = retained.OpenSingleGpu();
        retained.Dispose();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var blocked = Assert.ThrowsExactly<TargetInvocationException>(() => Create(blockedProvider));
            Assert.IsInstanceOfType<IOException>(blocked.InnerException);
        }
        Assert.AreEqual(1, factoryCalls, "Pending cleanup must not accumulate initialized contexts.");
        Assert.AreEqual(1, refusing.Initializes);
        refusing.UnloadStatus = 0;
        var replacement = Create(blockedProvider);
        Assert.IsTrue(retained.IsDisposed);
        Assert.AreEqual(2, factoryCalls);
        replacement.Dispose();
        blockedProvider.Dispose();
    }

    [TestMethod]
    public void Recovery_diagnostic_preserves_paused_cache_without_refreshing_or_releasing_native_context()
    {
        var bus = new Lifetime();
        int factoryCalls = 0;
        var provider = new WindowsHomeHardwareProvider(() => { factoryCalls++; return bus.Create(); }, () => true, _ => 45);
        typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
            new CompatibilityDecision(CompatibilityMode.Writable,
                new CapabilitySnapshot([new CapabilityDescriptor("gpuVfCurve", CapabilityState.Available, null)]), null, []));
        var lifecycle = new PowerLifecycleState(provider);
        var context = Create(provider);
        _ = context.OpenSingleGpu();
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var cached = new GpuVfState(Enumerable.Repeat(new GpuVfNode(800, 1500, 0), 127).ToArray(), -200_000, 200_000, null)
        { MemoryOffsetKhz = 0, CoreOffsetKhz = 0 };
        typeof(WindowsHomeHardwareProvider).GetField("gpuVfCache", Private)!.SetValue(provider, cached);
        typeof(WindowsHomeHardwareProvider).GetField("gpuVfCacheAt", Private)!.SetValue(provider, observedAt);
        long version = lifecycle.Pause();
        bus.Pause();
        context.Dispose();
        using (lifecycle.AllowRecoveryReads(version))
        {
            var method = typeof(WindowsHomeHardwareProvider).GetMethod("ReadGpuVfLocked", Private)!;
            foreach (bool force in new[] { false, true })
            {
                var state = (GpuVfState)method.Invoke(provider, [CancellationToken.None, force])!;
                Assert.AreEqual(127, state.Nodes.Length);
                Assert.IsNull(state.Error);
                Assert.AreEqual("suspended-last-known", state.Source);
                Assert.AreEqual(observedAt, state.ObservedAtUtc);
                Assert.AreEqual(observedAt, typeof(WindowsHomeHardwareProvider).GetField("gpuVfCacheAt", Private)!.GetValue(provider));
            }
            Assert.AreEqual(1, factoryCalls);
            Assert.AreEqual(0, bus.Unloads);
            Assert.AreEqual(0, bus.Frees);
            typeof(WindowsHomeHardwareProvider).GetField("gpuVfCache", Private)!.SetValue(provider, null);
            var unavailable = (GpuVfState)method.Invoke(provider, [CancellationToken.None, false])!;
            Assert.AreEqual(0, unavailable.Nodes.Length);
            Assert.AreEqual("suspended-unavailable", unavailable.Source);
            Assert.IsNull(unavailable.ObservedAtUtc);
        }
        Assert.IsTrue(lifecycle.Resume(version));
        bus.Allowed = bus.ReadAllowed = true;
        provider.Dispose();
        Assert.IsTrue(context.IsDisposed);

        foreach (string failure in new[] { "refused", "unloadUnknown", "freeUnknown" })
        {
            var failedBus = new Lifetime();
            int failedFactoryCalls = 0;
            var failedProvider = new WindowsHomeHardwareProvider(() => { failedFactoryCalls++; return failedBus.Create(); }, () => true, _ => 45);
            typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(failedProvider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new CapabilityDescriptor("gpuVfCurve", CapabilityState.Available, null)]), null, []));
            var failedLifecycle = new PowerLifecycleState(failedProvider);
            var failedContext = Create(failedProvider);
            _ = failedContext.OpenSingleGpu();
            if (failure == "refused") failedBus.UnloadStatus = -209;
            if (failure == "unloadUnknown") failedBus.AfterUnload = () => throw new IOException("fakeUnloadUnknown");
            if (failure == "freeUnknown") failedBus.AfterFree = () => throw new IOException("fakeFreeUnknown");
            failedContext.Dispose();
            typeof(WindowsHomeHardwareProvider).GetField("gpuVfCache", Private)!.SetValue(failedProvider, cached);
            typeof(WindowsHomeHardwareProvider).GetField("gpuVfCacheAt", Private)!.SetValue(failedProvider, observedAt);
            long failedVersion = failedLifecycle.Pause();
            using (failedLifecycle.AllowRecoveryReads(failedVersion))
            {
                var state = (GpuVfState)typeof(WindowsHomeHardwareProvider).GetMethod("ReadGpuVfLocked", Private)!
                    .Invoke(failedProvider, [CancellationToken.None, true])!;
                Assert.AreEqual(0, state.Nodes.Length, failure);
                Assert.IsNotNull(state.Error, failure);
                Assert.AreNotEqual("suspended-last-known", state.Source, failure);
            }
            Assert.AreEqual(1, failedFactoryCalls, failure);
            Assert.AreEqual(1, failedBus.Unloads, failure);
            failedProvider.Dispose();
        }
    }

    private static NvapiVfInterop Create(object provider) =>
        (NvapiVfInterop)provider.GetType().GetMethod("CreateGpuVfInterop", Private)!.Invoke(provider, null)!;

    private sealed class Lifetime
    {
        public bool Allowed = true, ReadAllowed = true;
        public int Initializes, Unloads, Frees, InitializeStatus, UnloadStatus;
        public Action? AfterInitialize, AfterUnload, AfterFree;
        public readonly List<string> Events = [];
        public void Pause() => Allowed = ReadAllowed = false;
        public NvapiVfInterop Create() => new(_ => (_, _) => 0, () => [(nint)1], () => Allowed, () => ReadAllowed,
            new NvapiVfInterop.LifetimeHooks(
                () => { Initializes++; Events.Add("initialize"); AfterInitialize?.Invoke(); return InitializeStatus; },
                () => { Unloads++; Events.Add("unload"); AfterUnload?.Invoke(); return UnloadStatus; },
                () => { Assert.AreEqual(0, UnloadStatus); Frees++; Events.Add("free"); AfterFree?.Invoke(); }));
    }
}
