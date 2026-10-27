using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Enabling_gpu_follow_marks_the_request_as_current_mode_only()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Update(saved => saved with { GpuFollowPreset = true, LightingFollowPreset = false });
        string pipeName = "Jiaolong-gpu-follow-enable-" + Guid.NewGuid().ToString("N");
        await using var normal = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
        await using var priority = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
        await using var session = new HomeControlSession(new ControlCenterClient(pipeName),
            new RecordingRadioController(), new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);

        await session.StartAsync(deadline.Token);
        await catalog.FollowPreferenceChangedAsync(ControlPageId.Gpu, true, deadline.Token);

        var request = normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().Single().Request;
        Assert.AreEqual(ControlOrigin.ModeFollow, request.Origin);
        Assert.HasCount(1, request.Pages);
        Assert.AreEqual(ControlPageId.Gpu, request.Pages[0].Page);
        Assert.IsTrue(request.Pages[0].Follow);
        Assert.AreEqual(PerformanceMode.Balanced, request.NativeMode);
    }

    [TestMethod]
    public async Task Gpu_follow_uses_the_target_mode_slot_and_emits_no_mux_or_mode_write()
    {
        foreach (var (mode, slot) in new[]
        {
            (ControlModeId.Office, 3),
            (ControlModeId.Gaming, 1)
        })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var paths = new RecordingPathProvider();
            var preferences = new UserPreferencesStore(paths);
            preferences.Update(saved => saved with
            {
                GpuFollowPreset = true,
                LightingFollowPreset = false,
                GpuPresetSlots = new() { ["Office"] = 3, ["Gaming"] = 1 }
            });
            string pipeName = "Jiaolong-gpu-follow-slot-" + Guid.NewGuid().ToString("N");
            await using var normal = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
            await using var priority = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
            await using var session = new HomeControlSession(new ControlCenterClient(pipeName),
                new RecordingRadioController(), new AppliedConfigurationRestore(paths));
            var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);

            var result = await catalog.ApplyAsync(mode, PerformanceMode.Quiet,
                ControlOrigin.ManualMode, token: deadline.Token);

            Assert.IsNotNull(result);
            Assert.AreEqual(CommandState.Rejected, result.State, "The fake peer never applies hardware commands.");
            var commands = normal.Commands.Concat(priority.Commands).ToArray();
            Assert.HasCount(2, commands, "The mode transition publishes the catalog and sends one page request.");
            Assert.IsFalse(commands.Any(command => command is SetMuxModeCommand or SetPerformanceModeCommand));
            var publication = commands.OfType<PublishPresetCatalogCommand>().Single();
            var request = commands.OfType<ApplyControlRequestCommand>().Single().Request;
            Assert.HasCount(1, request.Pages);
            var selection = request.Pages[0];
            var key = PresetKey.Create(mode, slot);
            Assert.AreEqual(ControlPageId.Gpu, selection.Page);
            Assert.IsTrue(selection.Follow);
            Assert.AreEqual(key, selection.Key);
            Assert.AreEqual(publication.Items.Single(item => item.Page == ControlPageId.Gpu && item.Key == key).Revision,
                selection.Revision);
        }
    }
}
