using System.Collections.Concurrent;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Polled_external_mode_change_emits_one_current_mode_gpu_follow_request()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Update(saved => saved with { GpuFollowPreset = true, LightingFollowPreset = false,
            GpuPresetSlots = new() { ["Office"] = 3, ["Gaming"] = 1 } });
        string pipe = "Xisura-gpu-mode-observe-" + Guid.NewGuid().ToString("N");
        var mode = PerformanceMode.Balanced;
        await using var normal = new ReconnectServer(pipe, FollowState(mode));
        await using var priority = new ReconnectServer(pipe, FollowState(mode));
        normal.StateFactory = () => FollowState(mode);
        await using var session = new HomeControlSession(new ControlCenterClient(pipe),
            new RecordingRadioController(), new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        var tasks = new ConcurrentQueue<Task>();
        int observations = 0;
        bool active = true;
        session.ModeReadbackObserved += (snapshot, generation) =>
        {
            Interlocked.Increment(ref observations);
            tasks.Enqueue(catalog.ObserveModeAsync(snapshot, generation, deadline.Token, () => active));
        };
        await session.StartAsync(deadline.Token);
        Assert.IsTrue(await WaitForAsync(() => Volatile.Read(ref observations) >= 2 && session.ConfigurationRestorationSettled),
            "Every successful poll must expose an observation, including equivalent controls.");
        Assert.IsEmpty(normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>(), "Startup must only seed.");
        mode = PerformanceMode.Quiet;
        Assert.IsTrue(await WaitForAsync(() => normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().Any()),
            "An external mode change must reach the real catalog/request producer.");
        await Task.WhenAll(tasks.ToArray());
        var request = normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().Single().Request;
        Assert.AreEqual(ControlOrigin.ModeFollow, request.Origin);
        Assert.AreEqual(PerformanceMode.Quiet, request.NativeMode);
        Assert.HasCount(1, request.Pages);
        Assert.AreEqual(ControlPageId.Gpu, request.Pages[0].Page);
        Assert.AreEqual(PresetKey.Create(ControlModeId.Office, 3), request.Pages[0].Key);
        var rejected = await catalog.ApplyAsync(ControlModeId.Turbo, PerformanceMode.Turbo,
            ControlOrigin.ManualMode, token: deadline.Token);
        Assert.AreEqual(CommandState.Rejected, rejected!.State);
        mode = PerformanceMode.Turbo;
        Assert.IsTrue(await WaitForAsync(() => normal.Commands.Concat(priority.Commands)
            .OfType<ApplyControlRequestCommand>().Count(command => command.Request.Origin == ControlOrigin.ModeFollow) == 2),
            "A rejected manual target must not consume a later real external transition.");
        active = false;
        mode = PerformanceMode.Balanced;
        int count = observations;
        Assert.IsTrue(await WaitForAsync(() => observations >= count + 3));
        await Task.WhenAll(tasks.ToArray());
        Assert.HasCount(3, normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().ToArray(), "Exit admission must stop new follows.");
    }

    [TestMethod]
    public async Task Late_observations_of_a_completed_manual_mode_request_do_not_follow_it_twice()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Update(saved => saved with { GpuFollowPreset = true, LightingFollowPreset = false });
        string pipe = "Xisura-gpu-mode-manual-" + Guid.NewGuid().ToString("N");
        var mode = PerformanceMode.Balanced;
        await using var normal = new ReconnectServer(pipe, FollowState(mode));
        await using var priority = new ReconnectServer(pipe, FollowState(mode));
        normal.StateFactory = () => FollowState(mode);
        await using var session = new HomeControlSession(new ControlCenterClient(pipe),
            new RecordingRadioController(), new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        var queued = new ConcurrentQueue<(HomeStateSnapshot Snapshot, long Generation)>();
        session.ModeReadbackObserved += (snapshot, generation) => queued.Enqueue((snapshot, generation));
        await session.StartAsync(deadline.Token);
        Assert.IsTrue(await WaitForAsync(() => queued.Count >= 2));
        while (queued.TryDequeue(out var item)) await catalog.ObserveModeAsync(item.Snapshot, item.Generation, deadline.Token);
        CommandResult Reply(HardwareCommand command)
        {
            if (command is PublishPresetCatalogCommand)
                return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false) { CatalogRevision = 1 };
            mode = PerformanceMode.Quiet;
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { VerifiedPerformanceMode = mode, Mutation = HardwareMutation.Changed };
        }
        normal.CommandResultFactory = priority.CommandResultFactory = Reply;
        await catalog.ApplyAsync(ControlModeId.Office, PerformanceMode.Quiet, ControlOrigin.ManualMode, token: deadline.Token);
        Assert.IsTrue(await WaitForAsync(() => queued.Count >= 2));
        while (queued.TryDequeue(out var item)) await catalog.ObserveModeAsync(item.Snapshot, item.Generation, deadline.Token);
        var requests = normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().ToArray();
        Assert.HasCount(1, requests, "Queued self-observations must not create another GPU request after manual completion.");
        Assert.AreEqual(ControlOrigin.ManualMode, requests[0].Request.Origin);
    }

    private static HomeStateSnapshot FollowState(PerformanceMode mode)
    {
        var state = CreateState(mode);
        return state with
        {
            Capabilities = state.Capabilities with { Items = state.Capabilities.Items.Concat(
                [new CapabilityDescriptor("gpuMemoryOffset", CapabilityState.Available, null)]).ToArray() },
            Controls = state.Controls with { ModeEvidence = new(DateTimeOffset.UtcNow, "native-test", 4,
                "read", 8, mode, null, "RequestStateUnverified") }
        };
    }

    private static async Task<bool> WaitForAsync(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            if (ready()) return true;
            await Task.Delay(20);
        }
        return ready();
    }
}
