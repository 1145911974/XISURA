using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong_ControlCenter.Services;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Explicit_preset_keeps_previous_slot_for_unconfirmed_or_mismatched_page_results()
    {
        foreach (string scenario in new[] { "partial", "failed", "unverified", "wrong key", "wrong revision", "missing page" })
            await VerifyPresetPageCommitAsync(scenario, shouldRemember: false);
    }

    [TestMethod]
    public async Task Explicit_preset_commits_only_its_confirmed_page_despite_unrelated_follow_or_native_uncertainty()
    {
        await VerifyPresetPageCommitAsync("confirmed", shouldRemember: true);
    }

    private static async Task VerifyPresetPageCommitAsync(string scenario, bool shouldRemember)
    {
        using var paths = new ManagedSessionTestPaths();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var preferences = new UserPreferencesStore(paths);
        string office = ControlModeId.Office.ToString();
        preferences.Update(saved => saved with
        {
            PerformanceFollowPreset = false, GpuFollowPreset = true, FanFollowPreset = false,
            LightingFollowPreset = false, FanPresetSlots = new() { [office] = 3 }
        });
        string path = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "preferences.json");
        byte[] original = File.ReadAllBytes(path);
        var key = PresetKey.Create(ControlModeId.Office, 1);
        CommandResult? acknowledged = null;
        CommandResult ResultFor(HardwareCommand command)
        {
            var result = new CommandResult(command.OperationId, CommandState.Applied, null,
                RequiredUserAction.None, null, false)
            { CatalogRevision = command is PublishPresetCatalogCommand ? 1 : null, Mutation = HardwareMutation.NoMutation };
            if (command is not ApplyControlRequestCommand apply) return result;
            var selected = apply.Request.Pages.Single(page => page.Page == ControlPageId.Fan);
            var state = scenario switch
            {
                "partial" => ApplyState.Partial,
                "failed" => ApplyState.Failed,
                "unverified" => ApplyState.SubmittedUnverified,
                _ => ApplyState.Confirmed
            };
            PageApplyResult requested = new(ControlPageId.Fan,
                scenario == "wrong key" ? PresetKey.Create(ControlModeId.Office, 2) : selected.Key,
                scenario == "wrong revision" ? selected.Revision + 1 : selected.Revision, state,
                state == ApplyState.Confirmed ? ["strategy"] : [],
                state == ApplyState.SubmittedUnverified ? ["strategy"] : [],
                state == ApplyState.Partial ? ["strategy"] : [],
                state == ApplyState.Failed ? "HardwareUnavailable" : null);
            // A different follow page and native acknowledgement cannot invalidate this page's exact result.
            var gpu = apply.Request.Pages.Single(page => page.Page == ControlPageId.Gpu);
            PageApplyResult other = new(ControlPageId.Gpu, gpu.Key, gpu.Revision,
                ApplyState.Failed, [], [], ["coreFrequencyLimitMhz"], "HardwareUnavailable");
            return acknowledged = result with
            {
                ExecutionState = new(1, 1, 1, apply.Request.LogicalMode, apply.Request.NativeMode,
                    scenario == "missing page" ? [other] : [requested, other], [])
                { NativeModeApplyState = ApplyState.SubmittedUnverified }
            };
        }
        string pipe = "Jiaolong-preset-page-" + Guid.NewGuid().ToString("N");
        await using var normal = new ReconnectServer(pipe, CreateState(PerformanceMode.Balanced))
            { CommandResultFactory = ResultFor };
        await using var priority = new ReconnectServer(pipe, CreateState(PerformanceMode.Balanced))
            { CommandResultFactory = ResultFor };
        await using var session = new HomeControlSession(new ControlCenterClient(pipe),
            new RecordingRadioController(), new AppliedConfigurationRestore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);

        var actual = await catalog.ApplyAsync(ControlModeId.Office, PerformanceMode.Balanced,
            ControlOrigin.PresetUse, ControlPageId.Fan, key, deadline.Token);

        Assert.IsNotNull(actual, scenario);
        Assert.AreEqual(CommandState.Applied, actual.State, scenario);
        Assert.IsNull(actual.Error, scenario);
        Assert.AreEqual(JsonSerializer.Serialize(acknowledged!.ExecutionState), JsonSerializer.Serialize(actual.ExecutionState), scenario);
        Assert.AreEqual(1, normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().Count(), scenario);
        Assert.AreEqual(shouldRemember ? 1 : 3, preferences.Load().FanPresetSlots[office], scenario);
        if (!shouldRemember) CollectionAssert.AreEqual(original, File.ReadAllBytes(path), scenario);
    }
}
