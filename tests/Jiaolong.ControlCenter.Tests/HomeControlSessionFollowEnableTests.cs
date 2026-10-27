using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Follow_enable_discards_native_or_logical_targets_changed_while_publication_waits()
    {
        foreach (bool logicalOnly in new[] { false, true })
        {
            await using var fixture = new FollowEnableFixture();
            if (logicalOnly) fixture.LogicalMode = (int)ControlModeId.Gaming;
            var observed = new ConcurrentQueue<Task>();
            if (!logicalOnly) fixture.Session.ModeReadbackObserved += (state, generation) =>
                observed.Enqueue(fixture.Catalog.ObserveModeAsync(state, generation, fixture.Deadline.Token));
            await fixture.Session.StartAsync(fixture.Deadline.Token);
            await fixture.Publication.WaitAsync(fixture.Deadline.Token);
            var enabling = fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, fixture.Deadline.Token);
            try
            {
                if (logicalOnly) fixture.LogicalMode = (int)ControlModeId.Office;
                else fixture.Mode = PerformanceMode.Quiet;
                Assert.IsTrue(await WaitForAsync(() => logicalOnly
                    ? fixture.Session.State?.Controls.ExecutionState?.LogicalMode == ControlModeId.Office
                    : fixture.Session.State?.Controls.PerformanceMode == PerformanceMode.Quiet));
            }
            finally { fixture.Publication.Release(); }
            await enabling;
            if (logicalOnly) Assert.IsEmpty(fixture.Requests(), "A publication wait must not apply an obsolete logical target.");
            else
            {
                Assert.IsTrue(fixture.Requests().All(command => command.Request.NativeMode == PerformanceMode.Quiet &&
                    command.Request.LogicalMode == ControlModeId.Office), "Never combine an old logical target with current hardware mode.");
                Assert.IsTrue(await WaitForAsync(() => fixture.Requests().Length == 1),
                    "After discarding the stale enable, two fresh polls must still follow the latest real mode.");
                await Task.WhenAll(observed.ToArray());
                Assert.AreEqual(PresetKey.Create(ControlModeId.Office, 3), fixture.Requests().Single().Request.Pages.Single().Key);
            }
        }

        await using var automatic = new FollowEnableFixture();
        await automatic.Session.StartAsync(automatic.Deadline.Token);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        automatic.Catalog.Saved += (_, _) =>
        {
            Assert.IsTrue(automatic.Publication.Wait(0), "Hold publication before the apply's flush can publish the pending edit.");
            secondFlush.TrySetResult();
        };
        int publications = 0;
        automatic.Normal.CommandResultFactory = command =>
        {
            if (command is PublishPresetCatalogCommand && Interlocked.Increment(ref publications) == 1)
            {
                automatic.Catalog.ScheduleSave(ControlPageId.Gpu, PresetKey.Create(ControlModeId.Gaming, 1),
                    "pending edit", MemoryFollowPreset(PresetKey.Create(ControlModeId.Gaming, 1)).Payload);
                saved.TrySetResult();
            }
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { CatalogRevision = 1, Mutation = HardwareMutation.NoMutation };
        };
        var pending = automatic.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, automatic.Deadline.Token);
        await saved.Task.WaitAsync(automatic.Deadline.Token);
        await secondFlush.Task.WaitAsync(automatic.Deadline.Token);
        try
        {
            automatic.Automatic = true;
            Assert.IsTrue(await WaitForAsync(() => automatic.Session.State?.Controls.AdaptiveAutomation?.Enabled == true));
        }
        finally { automatic.Publication.Release(); }
        await pending;
        Assert.IsEmpty(automatic.Requests(), "Automation enabled during the apply's own wait must invalidate the old manual follow.");

        foreach (int invalid in new[] { 1, 2 })
        {
            await using var unknown = new FollowEnableFixture();
            var observations = new ConcurrentQueue<Task>();
            unknown.Session.ModeReadbackObserved += (state, generation) =>
                observations.Enqueue(unknown.Catalog.ObserveModeAsync(state, generation, unknown.Deadline.Token));
            await unknown.Session.StartAsync(unknown.Deadline.Token);
            await unknown.Publication.WaitAsync(unknown.Deadline.Token);
            var enabling = unknown.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, unknown.Deadline.Token);
            try
            {
                unknown.InvalidEvidence = invalid;
                Assert.IsTrue(await WaitForAsync(() => invalid == 1
                    ? unknown.Session.State?.Controls.ModeEvidence is null
                    : unknown.Session.State?.Controls.ModeEvidence?.CapturedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-2)));
                unknown.Mode = PerformanceMode.Quiet;
                unknown.InvalidEvidence = 0;
                Assert.IsTrue(await WaitForAsync(() => unknown.Session.State?.Controls.PerformanceMode == PerformanceMode.Quiet));
            }
            finally { unknown.Publication.Release(); }
            await enabling;
            int count = observations.Count;
            Assert.IsTrue(await WaitForAsync(() => observations.Count >= count + 3));
            await Task.WhenAll(observations.ToArray());
            Assert.IsEmpty(unknown.Requests(), "Unknown or expired evidence during enable must reset; a later first valid mode must only seed.");
        }
    }

    [TestMethod]
    public async Task Follow_enable_does_not_revive_an_old_intent_after_off_on_and_cancelled_replacement()
    {
        await using var fixture = new FollowEnableFixture();
        await fixture.Session.StartAsync(fixture.Deadline.Token);
        await fixture.Publication.WaitAsync(fixture.Deadline.Token);
        var old = fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, fixture.Deadline.Token);
        fixture.Preferences.Update(saved => saved with { GpuFollowPreset = false });
        var disabling = fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, false, fixture.Deadline.Token);
        fixture.Preferences.Update(saved => saved with { GpuFollowPreset = true });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, cancelled.Token); }
        finally { fixture.Publication.Release(); }
        await Task.WhenAll(old, disabling);
        Assert.IsEmpty(fixture.Requests(), "An old enable remains obsolete even when the preference has returned to true.");

        await fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, fixture.Deadline.Token);
        Assert.HasCount(1, fixture.Requests(), "Cancellation must release enable suppression for a later valid request.");

        await fixture.Publication.WaitAsync(fixture.Deadline.Token);
        var exiting = fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, fixture.Deadline.Token);
        try { await fixture.Catalog.StopModeFollowAsync(); }
        finally { fixture.Publication.Release(); }
        await exiting;
        Assert.HasCount(1, fixture.Requests(), "Formal stop must invalidate a pending explicit enable as well as observed follows.");

        fixture.Normal.StateFactory = () => FollowState(PerformanceMode.Balanced) with
            { Controls = FollowState(PerformanceMode.Balanced).Controls with { PerformanceMode = null, ModeEvidence = null } };
        Assert.IsTrue(await WaitForAsync(() => fixture.Session.State?.Controls.PerformanceMode is null));
        fixture.Preferences.Update(saved => saved with { GpuFollowPreset = false });
        await fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, false, fixture.Deadline.Token);
        Assert.IsTrue(fixture.Normal.Commands.OfType<PublishPresetCatalogCommand>().Last().Selections!
            .Where(selection => selection.Page == ControlPageId.Gpu).All(selection => !selection.Follow),
            "Disabling must publish false even with unknown mode after follow admission has stopped.");
        Assert.HasCount(1, fixture.Requests());
    }

    [TestMethod]
    public async Task Follow_enable_rejects_a_target_captured_before_a_real_connection_replacement()
    {
        await using var fixture = new FollowEnableFixture();
        await fixture.Session.StartAsync(fixture.Deadline.Token);
        long generation = fixture.Session.ConnectionGeneration;
        await fixture.Publication.WaitAsync(fixture.Deadline.Token);
        var enabling = fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, fixture.Deadline.Token);
        await fixture.Normal.DisposeAsync();
        await using var replacement = new ReconnectServer(fixture.Pipe, FollowState(PerformanceMode.Balanced))
            { ServiceInstanceId = fixture.Priority.ServiceInstanceId };
        try
        {
            Assert.IsTrue(await WaitForAsync(() => fixture.Session.ConnectionGeneration > generation &&
                fixture.Session.Status == HomeSessionStatus.Connected));
        }
        finally { fixture.Publication.Release(); }
        await enabling;
        Assert.IsEmpty(fixture.Requests().Concat(replacement.Commands.OfType<ApplyControlRequestCommand>()).ToArray(),
            "A new handshake invalidates the old enable even when hardware mode is unchanged.");
    }

    [TestMethod]
    public async Task Follow_enable_seeds_polls_without_replaying_and_uses_the_live_mode_slot()
    {
        await using var fixture = new FollowEnableFixture();
        fixture.Preferences.Update(saved => saved with { GpuFollowPreset = false,
            GpuPresetSlots = new() { ["Office"] = 3, ["Gaming"] = 1 } });
        await fixture.Store.SaveAsync(MemoryFollowPreset(PresetKey.Create(ControlModeId.Gaming, 1)), fixture.Deadline.Token);
        var observations = new ConcurrentQueue<Task>();
        fixture.Session.ModeReadbackObserved += (state, generation) =>
            observations.Enqueue(fixture.Catalog.ObserveModeAsync(state, generation, fixture.Deadline.Token));
        await fixture.Session.StartAsync(fixture.Deadline.Token);
        Assert.IsTrue(await WaitForAsync(() => observations.Count >= 2));
        fixture.Preferences.Update(saved => saved with { GpuFollowPreset = true });
        await fixture.Publication.WaitAsync(fixture.Deadline.Token);
        var enabling = fixture.Catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, fixture.Deadline.Token);
        try
        {
            fixture.Mode = PerformanceMode.Quiet;
            int count = observations.Count;
            Assert.IsTrue(await WaitForAsync(() => observations.Count >= count + 2));
            fixture.Mode = PerformanceMode.Balanced;
            count = observations.Count;
            Assert.IsTrue(await WaitForAsync(() => observations.Count >= count + 3));
            Assert.IsTrue(observations.All(task => task.IsCompleted),
                "Polling during explicit enable must seed evidence without queueing another apply behind publication.");
        }
        finally { fixture.Publication.Release(); }
        await enabling;
        await Task.WhenAll(observations.ToArray());
        var request = fixture.Requests().Single().Request;
        Assert.AreEqual(ControlModeId.Gaming, request.LogicalMode, "Use live Balanced fallback, never an unrelated old Office identity.");
        Assert.AreEqual(PerformanceMode.Balanced, request.NativeMode);
        Assert.AreEqual(ControlOrigin.ModeFollow, request.Origin);
        Assert.HasCount(1, request.Pages);
        Assert.AreEqual(PresetKey.Create(ControlModeId.Gaming, 1), request.Pages[0].Key);
        var published = fixture.Normal.Commands.OfType<PublishPresetCatalogCommand>().Last().Items
            .Single(item => item.Page == ControlPageId.Gpu && item.Key == request.Pages[0].Key);
        Assert.AreEqual(-1000, published.Settings["memoryOffsetKhz"].Value!.Value.GetInt32());
        Assert.IsTrue(published.Settings.Where(pair => pair.Key != "memoryOffsetKhz")
            .All(pair => pair.Value.Intent == SettingIntent.Keep));
    }

    private static PagePresetEnvelope MemoryFollowPreset(PresetKey key) =>
        DefaultControlPresets.Create(ControlPageId.Gpu, key) with
        {
            UserModified = true, FactoryRevision = "",
            Payload = JsonSerializer.SerializeToElement(new { CoreFrequencyLimitMhz = (int?)null,
                ResetCoreFrequencyLimit = false, MemoryOffsetKhz = -1000, CoreOffsetKhz = (int?)null, VfOffsetsKhz = (int[]?)null }),
            Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Keep, ["coreOffsetKhz"] = SettingRequest.Keep,
                ["memoryOffsetKhz"] = SettingRequest.Set(-1000), ["vfOffsetsKhz"] = SettingRequest.Keep,
                ["voltageBoost"] = SettingRequest.Keep, ["powerLimit"] = SettingRequest.Keep, ["muxMode"] = SettingRequest.Keep }
        };

    private sealed class FollowEnableFixture : IAsyncDisposable
    {
        public readonly CancellationTokenSource Deadline = new(TimeSpan.FromSeconds(15));
        public readonly string Pipe = "Xisura-follow-enable-" + Guid.NewGuid().ToString("N");
        public readonly UserPreferencesStore Preferences;
        public readonly ControlPresetStore Store;
        public readonly ReconnectServer Normal;
        public readonly ReconnectServer Priority;
        public readonly HomeControlSession Session;
        public readonly PresetCatalogController Catalog;
        public volatile PerformanceMode Mode = PerformanceMode.Balanced;
        public volatile int LogicalMode = -1;
        public volatile bool Automatic;
        public volatile int InvalidEvidence;
        public long ApplyVersion => (long)typeof(PresetCatalogController)
            .GetField("applyVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Catalog)!;
        public SemaphoreSlim Publication => (SemaphoreSlim)typeof(PresetCatalogController)
            .GetField("publication", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Catalog)!;

        public FollowEnableFixture()
        {
            var paths = new RecordingPathProvider();
            Preferences = new(paths);
            Preferences.Update(saved => saved with { GpuFollowPreset = true, LightingFollowPreset = false,
                GpuPresetSlots = new() { ["Office"] = 3, ["Gaming"] = 1 } });
            Store = new(paths);
            HomeStateSnapshot State()
            {
                var state = FollowState(Mode);
                if (InvalidEvidence == 1) state = state with { Controls = state.Controls with { ModeEvidence = null } };
                else if (InvalidEvidence == 2) state = state with { Controls = state.Controls with
                    { ModeEvidence = state.Controls.ModeEvidence! with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5) } } };
                if (Automatic) state = state with { Controls = state.Controls with
                    { AdaptiveAutomation = new(true, true, null, "observing", null, null, null, null) } };
                return LogicalMode < 0 ? state : state with { Controls = state.Controls with
                { ExecutionState = new(0, 1, 0, (ControlModeId)LogicalMode, Mode, [], []) { NativeModeApplyState = ApplyState.Confirmed } } };
            }
            Normal = new(Pipe, State()) { StateFactory = State };
            Priority = new(Pipe, State()) { StateFactory = State, ServiceInstanceId = Normal.ServiceInstanceId };
            Session = new(new ControlCenterClient(Pipe), new RecordingRadioController(),
                new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
            Catalog = new(Session, Store, Preferences);
        }

        public ApplyControlRequestCommand[] Requests() => Normal.Commands.Concat(Priority.Commands).OfType<ApplyControlRequestCommand>().ToArray();
        public async ValueTask DisposeAsync()
        {
            Deadline.Cancel();
            await Session.DisposeAsync();
            await Normal.DisposeAsync();
            await Priority.DisposeAsync();
            Deadline.Dispose();
        }
    }
}
