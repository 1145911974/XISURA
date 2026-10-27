using System.Collections.Concurrent;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Polled_cpu_or_fan_mode_change_does_not_require_gpu_follow()
    {
        foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Fan })
        {
            await using var fixture = new FollowEnableFixture();
            await PreparePageFollowAsync(fixture, page);
            var commands = RecordPageFollowCommands(fixture);
            var tasks = new ConcurrentQueue<Task>();
            int observations = 0;
            void Observe(HomeStateSnapshot snapshot, long generation)
            {
                Interlocked.Increment(ref observations);
                tasks.Enqueue(fixture.Catalog.ObserveModeAsync(snapshot, generation, fixture.Deadline.Token));
            }
            fixture.Session.ModeReadbackObserved += Observe;
            try
            {
                await fixture.Session.StartAsync(fixture.Deadline.Token);
                Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= 2 &&
                    fixture.Session.ConfigurationRestorationSettled));
                Assert.IsEmpty(commands.OfType<ApplyControlRequestCommand>(), "Startup must establish a baseline without applying presets.");

                fixture.Mode = PerformanceMode.Quiet;
                Assert.IsTrue(await WaitForAsync(() => commands.OfType<ApplyControlRequestCommand>().Any()),
                    $"External mode changes must apply {page} even when GPU following is disabled.");
                int count = Volatile.Read(ref observations);
                Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= count + 3));
                fixture.Session.ModeReadbackObserved -= Observe;
                await fixture.Catalog.StopModeFollowAsync();
                await Task.WhenAll(tasks.ToArray());
                var recorded = commands.ToArray();
                var request = recorded.OfType<ApplyControlRequestCommand>().Single().Request;
                Assert.AreEqual(ControlOrigin.ModeFollow, request.Origin);
                Assert.AreEqual(PerformanceMode.Quiet, request.NativeMode);
                Assert.HasCount(1, request.Pages);
                Assert.AreEqual(page, request.Pages[0].Page);
                Assert.IsTrue(request.Pages[0].Follow);
                Assert.AreEqual(PresetKey.Create(ControlModeId.Office, page == ControlPageId.Performance ? 3 : 1), request.Pages[0].Key);
                var published = recorded
                    .OfType<PublishPresetCatalogCommand>().Last().Items
                    .Single(item => item.Page == page && item.Key == request.Pages[0].Key);
                Assert.AreEqual(published.Revision, request.Pages[0].Revision);
                Assert.IsFalse(recorded
                    .Any(command => command is SetPerformanceModeCommand or SetMuxModeCommand));
            }
            finally
            {
                fixture.Session.ModeReadbackObserved -= Observe;
                await fixture.Catalog.StopModeFollowAsync();
                await Task.WhenAll(tasks.ToArray());
            }
        }
    }

    [TestMethod]
    public async Task Polled_cpu_and_fan_follow_emit_separate_single_page_requests()
    {
        await using var fixture = new FollowEnableFixture();
        await PreparePageFollowAsync(fixture, ControlPageId.Performance, ControlPageId.Fan);
        var commands = RecordPageFollowCommands(fixture);
        var tasks = new ConcurrentQueue<Task>();
        int observations = 0;
        void Observe(HomeStateSnapshot snapshot, long generation)
        {
            Interlocked.Increment(ref observations);
            tasks.Enqueue(fixture.Catalog.ObserveModeAsync(snapshot, generation, fixture.Deadline.Token));
        }
        fixture.Session.ModeReadbackObserved += Observe;
        try
        {
            await fixture.Session.StartAsync(fixture.Deadline.Token);
            Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= 2 &&
                fixture.Session.ConfigurationRestorationSettled));
            Assert.IsEmpty(commands.OfType<ApplyControlRequestCommand>());
            fixture.Mode = PerformanceMode.Quiet;
            Assert.IsTrue(await WaitForAsync(() => commands.OfType<ApplyControlRequestCommand>().Count() >= 2),
                "Each enabled page must receive its own request; the service rejects multi-page ModeFollow.");
            int count = Volatile.Read(ref observations);
            Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= count + 3));
            fixture.Session.ModeReadbackObserved -= Observe;
            await fixture.Catalog.StopModeFollowAsync();
            await Task.WhenAll(tasks.ToArray());
            var recorded = commands.ToArray();
            var requests = recorded.OfType<ApplyControlRequestCommand>().ToArray();
            Assert.HasCount(2, requests, "Equivalent subsequent polls must not replay either page.");
            foreach (var command in requests)
            {
                Assert.AreEqual(ControlOrigin.ModeFollow, command.Request.Origin);
                Assert.AreEqual(PerformanceMode.Quiet, command.Request.NativeMode);
                Assert.HasCount(1, command.Request.Pages);
                Assert.IsTrue(command.Request.Pages[0].Follow);
                var page = command.Request.Pages[0].Page;
                Assert.AreEqual(PresetKey.Create(ControlModeId.Office, page == ControlPageId.Performance ? 3 : 1),
                    command.Request.Pages[0].Key);
            }
            CollectionAssert.AreEquivalent(new[] { ControlPageId.Performance, ControlPageId.Fan },
                requests.Select(command => command.Request.Pages.Single().Page).ToArray());
            Assert.IsFalse(recorded
                .Any(command => command is SetPerformanceModeCommand or SetMuxModeCommand));
        }
        finally
        {
            fixture.Session.ModeReadbackObserved -= Observe;
            await fixture.Catalog.StopModeFollowAsync();
            await Task.WhenAll(tasks.ToArray());
        }
    }

    [TestMethod]
    public async Task Disabling_a_page_discards_its_queued_external_follow_request()
    {
        foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Fan })
        {
            await using var fixture = new FollowEnableFixture();
            await PreparePageFollowAsync(fixture, page);
            var commands = RecordPageFollowCommands(fixture);
            var tasks = new ConcurrentQueue<Task>();
            int observations = 0;
            void Observe(HomeStateSnapshot state, long generation)
            {
                Interlocked.Increment(ref observations);
                tasks.Enqueue(fixture.Catalog.ObserveModeAsync(state, generation, fixture.Deadline.Token));
            }
            fixture.Session.ModeReadbackObserved += Observe;
            bool held = false;
            Task disabling = Task.CompletedTask;
            try
            {
                await fixture.Session.StartAsync(fixture.Deadline.Token);
                Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= 2 && fixture.Session.ConfigurationRestorationSettled));
                await fixture.Publication.WaitAsync(fixture.Deadline.Token);
                held = true;
                long version = fixture.ApplyVersion;
                fixture.Mode = PerformanceMode.Quiet;
                Assert.IsTrue(await WaitForAsync(() => fixture.ApplyVersion > version), "A follow request must be queued before cancellation is tested.");
                fixture.Preferences.Update(saved => page == ControlPageId.Performance
                    ? saved with { PerformanceFollowPreset = false } : saved with { FanFollowPreset = false });
                disabling = fixture.Catalog.FollowPreferenceChangedAsync(page, false, fixture.Deadline.Token);
                fixture.Publication.Release();
                held = false;
                await disabling;
            }
            finally
            {
                if (held) fixture.Publication.Release();
                fixture.Session.ModeReadbackObserved -= Observe;
                await fixture.Catalog.StopModeFollowAsync();
                await Task.WhenAll(tasks.ToArray().Append(disabling));
            }
            Assert.IsEmpty(commands.OfType<ApplyControlRequestCommand>(), "Disabling must invalidate a request already waiting for publication.");
        }
    }

    [TestMethod]
    public async Task Full_exit_discards_queued_page_follow_and_late_observations()
    {
        await using var fixture = new FollowEnableFixture();
        await PreparePageFollowAsync(fixture, ControlPageId.Performance, ControlPageId.Fan);
        var commands = RecordPageFollowCommands(fixture);
        var tasks = new ConcurrentQueue<Task>();
        int observations = 0;
        void Observe(HomeStateSnapshot state, long generation)
        {
            Interlocked.Increment(ref observations);
            tasks.Enqueue(fixture.Catalog.ObserveModeAsync(state, generation, fixture.Deadline.Token));
        }
        fixture.Session.ModeReadbackObserved += Observe;
        bool held = false;
        try
        {
            await fixture.Session.StartAsync(fixture.Deadline.Token);
            Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= 2 && fixture.Session.ConfigurationRestorationSettled));
            await fixture.Publication.WaitAsync(fixture.Deadline.Token);
            held = true;
            long version = fixture.ApplyVersion;
            fixture.Mode = PerformanceMode.Quiet;
            Assert.IsTrue(await WaitForAsync(() => fixture.ApplyVersion > version));
            var stopping = fixture.Catalog.StopModeFollowAsync();
            fixture.Publication.Release();
            held = false;
            await stopping;
            int count = Volatile.Read(ref observations);
            fixture.Mode = PerformanceMode.Turbo;
            Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= count + 3));
        }
        finally
        {
            if (held) fixture.Publication.Release();
            fixture.Session.ModeReadbackObserved -= Observe;
            await fixture.Catalog.StopModeFollowAsync();
            await Task.WhenAll(tasks.ToArray());
        }
        Assert.IsEmpty(commands.OfType<ApplyControlRequestCommand>(), "Exit must stop queued and later observations for every page.");
    }

    [TestMethod]
    public async Task Waiting_page_enable_yields_to_a_later_manual_request_in_the_same_mode()
    {
        await using var fixture = new FollowEnableFixture();
        await PreparePageFollowAsync(fixture, ControlPageId.Performance);
        var commands = RecordPageFollowCommands(fixture);
        await fixture.Session.StartAsync(fixture.Deadline.Token);
        Assert.IsTrue(await WaitForAsync(() => fixture.Session.ConfigurationRestorationSettled));
        await fixture.Publication.WaitAsync(fixture.Deadline.Token);
        Task enabling = Task.CompletedTask;
        Task<CommandResult?> manual = Task.FromResult<CommandResult?>(null);
        try
        {
            enabling = fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Performance, true, fixture.Deadline.Token);
            manual = fixture.Catalog.ApplyAsync(ControlModeId.Gaming, PerformanceMode.Balanced,
                ControlOrigin.ManualMode, token: fixture.Deadline.Token);
        }
        finally { fixture.Publication.Release(); }
        await Task.WhenAll(enabling, manual);
        var request = commands.OfType<ApplyControlRequestCommand>().Single().Request;
        Assert.AreEqual(ControlOrigin.ManualMode, request.Origin,
            "An earlier enable must not override a later explicit request even when the native mode did not change.");
    }

    [TestMethod]
    public async Task Follow_failures_release_the_application_gate_and_do_not_poison_later_batches()
    {
        await using (var fixture = new FollowEnableFixture())
        {
            await PreparePageFollowAsync(fixture, ControlPageId.Performance);
            await fixture.Session.StartAsync(fixture.Deadline.Token);
            try
            {
                await fixture.Catalog.ApplyAsync(ControlModeId.Gaming, PerformanceMode.Balanced,
                    ControlOrigin.ModeFollow, token: fixture.Deadline.Token,
                    isCurrent: () => throw new InvalidOperationException("injected current predicate failure"),
                    applyOnlyPage: ControlPageId.Performance);
            }
            catch (InvalidOperationException error) when (error.Message == "injected current predicate failure") { }
            await fixture.Catalog.StopModeFollowAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        await using var resumed = new FollowEnableFixture();
        await PreparePageFollowAsync(resumed, ControlPageId.Performance);
        var commands = RecordPageFollowCommands(resumed);
        var tasks = new ConcurrentQueue<Task>();
        int observations = 0;
        void Observe(HomeStateSnapshot state, long generation)
        {
            Interlocked.Increment(ref observations);
            tasks.Enqueue(resumed.Catalog.ObserveModeAsync(state, generation, resumed.Deadline.Token));
        }
        resumed.Session.ModeReadbackObserved += Observe;
        try
        {
            await resumed.Session.StartAsync(resumed.Deadline.Token);
            Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= 2 && resumed.Session.ConfigurationRestorationSettled));
            typeof(PresetCatalogController).GetField("modeFollowTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(resumed.Catalog, Task.FromException(new InvalidOperationException("injected previous batch failure")));
            resumed.Mode = PerformanceMode.Quiet;
            Assert.IsTrue(await WaitForAsync(() => commands.OfType<ApplyControlRequestCommand>().Any()),
                "A prior failed batch must not permanently disable following.");
        }
        finally
        {
            resumed.Session.ModeReadbackObserved -= Observe;
            try { await resumed.Catalog.StopModeFollowAsync(); }
            catch (InvalidOperationException error) when (error.Message == "injected previous batch failure") { }
        }
        await Task.WhenAll(tasks.ToArray());
    }

    private static async Task PreparePageFollowAsync(FollowEnableFixture fixture, params ControlPageId[] pages)
    {
        fixture.Preferences.Update(saved => saved with
        {
            PerformanceFollowPreset = pages.Contains(ControlPageId.Performance),
            FanFollowPreset = pages.Contains(ControlPageId.Fan),
            GpuFollowPreset = false,
            LightingFollowPreset = false,
            PerformancePresetSlots = new() { ["Office"] = 3, ["Gaming"] = 1 },
            FanPresetSlots = new() { ["Office"] = 1, ["Gaming"] = 1 }
        });
        foreach (var page in pages)
            foreach (var key in new[] { PresetKey.Create(ControlModeId.Gaming, 1),
                PresetKey.Create(ControlModeId.Office, page == ControlPageId.Performance ? 3 : 1) })
                await fixture.Store.SaveAsync(DefaultControlPresets.Create(page, key) with { UserModified = true },
                    fixture.Deadline.Token);
        HomeStateSnapshot State()
        {
            var state = FollowState(fixture.Mode);
            return state with { Capabilities = state.Capabilities with { Items = state.Capabilities.Items
                .Where(item => !item.Key.StartsWith("gpu", StringComparison.OrdinalIgnoreCase))
                .Concat(pages.Select(page => new CapabilityDescriptor(
                    page == ControlPageId.Performance ? "cpuTuning" : "fanControl", CapabilityState.Available, null))).ToArray() } };
        }
        fixture.Normal.StateFactory = fixture.Priority.StateFactory = State;
    }

    private static ConcurrentQueue<HardwareCommand> RecordPageFollowCommands(FollowEnableFixture fixture)
    {
        var commands = new ConcurrentQueue<HardwareCommand>();
        fixture.Normal.CommandResultFactory = fixture.Priority.CommandResultFactory = command =>
        {
            commands.Enqueue(command);
            return command is PublishPresetCatalogCommand
                ? new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                    { CatalogRevision = 1, Mutation = HardwareMutation.NoMutation }
                : new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                    ServiceError.Create(ErrorCode.ValidationFailed, command.OperationId, false), false)
                    { Mutation = HardwareMutation.NoMutation };
        };
        return commands;
    }
}
