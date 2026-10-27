using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong_ControlCenter.Services;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Custom_slot_preserves_acknowledged_apply_when_readback_is_cancelled_or_disconnected()
    {
        await VerifyCustomReplyAsync("cancel readback");
        await VerifyCustomReplyAsync("drop readback");
    }

    [TestMethod]
    public async Task Custom_slot_keeps_previous_memory_when_apply_reply_is_cancelled_or_lost()
    {
        await VerifyCustomReplyAsync("cancel reply");
        await VerifyCustomReplyAsync("drop reply");
    }

    private static async Task VerifyCustomReplyAsync(string fault)
    {
        using var paths = new ManagedSessionTestPaths();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var requestCancellation = new CancellationTokenSource();
        var preferences = new UserPreferencesStore(paths);
        string custom = ControlModeId.Custom1.ToString();
        preferences.Update(saved => saved with
        {
            PerformanceFollowPreset = false, PerformancePresetSlots = new() { [custom] = 3 },
            GpuFollowPreset = false, FanFollowPreset = false, LightingFollowPreset = false
        });
        string preferencesPath = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "preferences.json");
        byte[] original = File.ReadAllBytes(preferencesPath);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int applies = 0;
        CommandResult ResultFor(HardwareCommand command)
        {
            bool publish = command is PublishPresetCatalogCommand;
            if (command is ApplyControlRequestCommand) Interlocked.Increment(ref applies);
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { CatalogRevision = publish ? 1 : null, Mutation = publish ? HardwareMutation.NoMutation : HardwareMutation.Changed };
        }
        async Task<bool> BeforeResponseAsync(RequestEnvelope request, CancellationToken stopping)
        {
            bool atReadback = request.Operation == "getDeviceState" && Volatile.Read(ref applies) > 0;
            bool atApply = request.Operation == "executeCommand" && request.Payload.Deserialize(
                ProtocolJsonContext.Default.HardwareCommand) is ApplyControlRequestCommand;
            if (fault.EndsWith("readback", StringComparison.Ordinal) ? atReadback : atApply)
            {
                barrier.TrySetResult();
                if (fault.StartsWith("drop", StringComparison.Ordinal)) return false;
                await resume.Task.WaitAsync(stopping);
            }
            return true;
        }
        string pipeName = "Jiaolong-custom-reply-" + Guid.NewGuid().ToString("N");
        await using var normal = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced))
            { CommandResultFactory = ResultFor, BeforeResponseAsync = BeforeResponseAsync };
        await using var priority = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced))
            { CommandResultFactory = ResultFor, BeforeResponseAsync = BeforeResponseAsync };
        await using var session = new HomeControlSession(new ControlCenterClient(pipeName),
            new RecordingRadioController(), new AppliedConfigurationRestore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
        try
        {
            var applying = catalog.ApplyAsync(ControlModeId.Custom1, PerformanceMode.Balanced,
                ControlOrigin.PresetUse, ControlPageId.Performance, PresetKey.Create(ControlModeId.Custom1, 1), requestCancellation.Token);
            await barrier.Task.WaitAsync(deadline.Token);
            if (!fault.StartsWith("drop", StringComparison.Ordinal)) requestCancellation.Cancel();
            var result = await applying.WaitAsync(deadline.Token);
            Assert.AreEqual(1, applies, "Unknown replies must not trigger an automatic resend.");
            if (fault.EndsWith("readback", StringComparison.Ordinal))
            {
                Assert.IsNotNull(result, "A failed readback cannot erase an acknowledged hardware application.");
                Assert.AreEqual(CommandState.Applied, result.State);
                Assert.AreEqual(HardwareMutation.Changed, result.Mutation);
                Assert.AreEqual(1, preferences.Load().PerformancePresetSlots[custom]);
            }
            else
            {
                Assert.IsNull(result);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(preferencesPath));
                Assert.AreEqual(3, preferences.Load().PerformancePresetSlots[custom]);
            }
        }
        finally { resume.TrySetResult(); }
    }

    [TestMethod]
    public async Task Preset_use_commits_remembered_slot_only_after_control_apply_succeeds()
    {
        var paths = new RecordingPathProvider();
        try
        {
            var preferences = new UserPreferencesStore(paths);
            string office = ControlModeId.Office.ToString();
            preferences.Update(saved => saved with
            {
                PerformanceFollowPreset = false,
                PerformancePresetSlots = new() { [office] = 3 },
                GpuFollowPreset = false,
                GpuPresetSlots = new() { [office] = 2 },
                FanFollowPreset = true,
                FanPresetSlots = new() { [office] = 3 },
                LightingFollowPreset = false,
                LightingPresetSlots = new() { [office] = 1 }
            });
            string preferencesPath = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "preferences.json");
            byte[] originalPreferences = File.ReadAllBytes(preferencesPath);
            var key = PresetKey.Create(ControlModeId.Office, 1);
            var scenarios = new[]
            {
                (Name: "publish rejected", RejectPublish: true, RejectApply: false),
                (Name: "apply rejected", RejectPublish: false, RejectApply: true),
                (Name: "both applied", RejectPublish: false, RejectApply: false)
            };

            foreach (var scenario in scenarios)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string pipeName = "Jiaolong-preset-slot-" + Guid.NewGuid().ToString("N");
                CommandResult ResultFor(HardwareCommand command)
                {
                    bool publication = command is PublishPresetCatalogCommand;
                    bool rejected = publication ? scenario.RejectPublish : scenario.RejectApply;
                    return new CommandResult(command.OperationId, rejected ? CommandState.Rejected : CommandState.Applied,
                        null, RequiredUserAction.None,
                        rejected ? ServiceError.Create(ErrorCode.ValidationFailed, command.OperationId, false) : null, false)
                    {
                        CatalogRevision = publication && !rejected ? 1 : null,
                        Mutation = HardwareMutation.NoMutation
                    };
                }

                var resultFactory = (Func<HardwareCommand, CommandResult>)ResultFor;
                await using var normal = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced))
                    { CommandResultFactory = resultFactory };
                await using var priority = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced))
                    { CommandResultFactory = resultFactory };
                await using var session = new HomeControlSession(new ControlCenterClient(pipeName),
                    new RecordingRadioController(), new AppliedConfigurationRestore(paths));
                var notifications = new List<string>();
                session.NotificationRequested += notifications.Add;
                var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);

                var result = await catalog.ApplyAsync(ControlModeId.Office, PerformanceMode.Balanced,
                    ControlOrigin.PresetUse, ControlPageId.Fan, key, deadline.Token);
                var commands = normal.Commands.Concat(priority.Commands).ToArray();
                var publication = commands.OfType<PublishPresetCatalogCommand>().Single();
                var publishedFan = publication.Selections.Single(selection => selection.Page == ControlPageId.Fan &&
                    selection.Key.Mode == ControlModeId.Office);
                Assert.AreEqual(key, publishedFan.Key, $"{scenario.Name}: publish selection must use this explicit slot.");
                Assert.IsTrue(publishedFan.Follow, $"{scenario.Name}: publish selection must retain Fan follow.");

                var applyCommands = commands.OfType<ApplyControlRequestCommand>().ToArray();
                if (scenario.RejectPublish)
                {
                    Assert.IsNull(result, "A publish rejection without a catalog revision must remain visible as failure.");
                    Assert.AreEqual(0, applyCommands.Length);
                }
                else
                {
                    Assert.AreEqual(1, applyCommands.Length);
                    var appliedFan = applyCommands[0].Request.Pages.Single(selection => selection.Page == ControlPageId.Fan);
                    Assert.AreEqual(key, appliedFan.Key);
                    Assert.IsNotNull(result);
                    Assert.AreEqual(scenario.RejectApply ? CommandState.Rejected : CommandState.Applied, result.State);
                    if (scenario.RejectApply)
                        Assert.AreEqual(ErrorCode.ValidationFailed, result.Error?.Code);
                    else
                        Assert.IsNull(result.Error);
                }

                if (scenario.RejectPublish || scenario.RejectApply)
                    Assert.IsTrue(notifications.Any(message => !string.IsNullOrWhiteSpace(message)),
                        $"{scenario.Name}: rejection must remain visible through NotificationRequested.");
                else
                    Assert.AreEqual(0, notifications.Count);

                var saved = preferences.Load();
                Assert.IsTrue(saved.FanFollowPreset);
                Assert.AreEqual(scenario.RejectPublish || scenario.RejectApply ? 3 : 1, saved.FanPresetSlots[office]);
                Assert.IsFalse(saved.PerformanceFollowPreset);
                Assert.AreEqual(3, saved.PerformancePresetSlots[office]);
                Assert.IsFalse(saved.GpuFollowPreset);
                Assert.AreEqual(2, saved.GpuPresetSlots[office]);
                Assert.IsFalse(saved.LightingFollowPreset);
                Assert.AreEqual(1, saved.LightingPresetSlots[office]);
                if (scenario.RejectPublish || scenario.RejectApply)
                    CollectionAssert.AreEqual(originalPreferences, File.ReadAllBytes(preferencesPath),
                        $"{scenario.Name}: rejected apply must not rewrite preferences.");
            }
        }
        finally
        {
            string VerifiedGuidRoot(string generatedRoot, string expectedLeaf)
            {
                string absolute = Path.GetFullPath(generatedRoot);
                if (!string.Equals(Path.GetFileName(absolute), expectedLeaf, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing cleanup for unexpected fixture root: {absolute}");
                string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "JiaolongTests"));
                string guidRoot = Path.GetDirectoryName(absolute)!;
                string relative = Path.GetRelativePath(tempRoot, guidRoot);
                if (!Guid.TryParseExact(relative, "N", out _))
                    throw new InvalidOperationException($"Refusing cleanup outside {tempRoot}/<guid>: {absolute}");
                return guidRoot;
            }

            string[] roots =
            [
                VerifiedGuidRoot(paths.LocalAppDataRoot, "LocalAppData"),
                VerifiedGuidRoot(paths.ProgramDataRoot, "ProgramData")
            ];
            foreach (string root in roots)
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
