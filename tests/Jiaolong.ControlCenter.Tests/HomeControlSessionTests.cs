using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong.Contracts.Protocol;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed partial class HomeControlSessionTests
{
    [TestMethod]
    public async Task Managed_session_pipe_configures_tags_commands_and_restores_before_disposal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        string name = "Xisura-managed-session-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(name, CreateState(PerformanceMode.Turbo));
        server.CommandResultFactory = command => new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { Mutation = HardwareMutation.NoMutation };
        using var paths = new ManagedSessionTestPaths();
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths), manageClientSession: true);
        await session.StartAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.Connected, session.Status);
        var command = new SetLidLogoCommand(Guid.NewGuid(), true);
        Assert.AreEqual(CommandState.Applied, (await session.ExecuteAsync(command, deadline.Token)).State);
        Assert.IsTrue(server.Commands.All(item => item.ClientSessionId == server.Handle.SessionId));
        Assert.AreEqual(CommandState.Applied, (await session.EndControlSessionAsync(deadline.Token))!.State);
        await session.EndControlSessionAsync(deadline.Token);
        CollectionAssert.AreEqual(new[] { "beginClientSession", "configureClientSession", "endClientSession" }, server.SessionOperations);
        int count = server.Commands.Count;
        Assert.AreEqual(CommandState.Rejected, (await session.ExecuteAsync(command, deadline.Token)).State);
        Assert.AreEqual(count, server.Commands.Count);
    }

    [TestMethod]
    public async Task Managed_session_configuration_refusal_blocks_all_hardware_commands()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        string name = "Xisura-managed-refused-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(name, CreateState(PerformanceMode.Turbo)) { RefuseConfiguration = true };
        using var paths = new ManagedSessionTestPaths();
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths), manageClientSession: true);
        await session.StartAsync(deadline.Token);
        Assert.AreEqual(CommandState.Rejected, (await session.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), true), deadline.Token)).State);
        Assert.HasCount(0, server.Commands);
        Assert.AreEqual(CommandState.Applied, (await session.EndControlSessionAsync(deadline.Token))!.State);
    }

    [TestMethod]
    public async Task Managed_pending_start_stays_read_only_then_acquires_its_lease_without_replaying_saved_mode()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string name = "Xisura-managed-pending-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(name, CreateState(PerformanceMode.Turbo)) { RefuseBegin = true };
        server.CommandResultFactory = command => new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { Mutation = HardwareMutation.NoMutation };
        using var paths = new ManagedSessionTestPaths();
        var preferences = new UserPreferencesStore(paths);
        preferences.Save(preferences.Load() with { PerformanceFollowPreset = true });
        var restore = new AppliedConfigurationRestore(paths);
        var saved = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced);
        await restore.RecordAppliedAsync(saved, new(saved.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { VerifiedPerformanceMode = saved.Mode }, deadline.Token);
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            restore, new DeviceSchedulingBaselineStore(paths), manageClientSession: true);
        typeof(HomeControlSession).GetField("preferences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(session, preferences);
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<string>();
        session.NotificationRequested += notifications.Enqueue;
        session.RestoreWarning += notifications.Enqueue;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += _ => { if (session.Status == HomeSessionStatus.Connected && server.SessionOperations.Contains("configureClientSession")) ready.TrySetResult(); };
        await session.StartAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.ReadOnly, session.Status);
        Assert.IsTrue(session.IsServiceConnected);
        Assert.AreEqual(PerformanceMode.Turbo, session.State!.Controls.PerformanceMode);
        Assert.IsFalse(session.State.Capabilities.IsAvailable("performanceMode"));
        Assert.IsTrue(session.State.Capabilities.IsAvailable("monitoring"));
        Assert.AreEqual(0, server.Commands.Count);
        server.RefuseBegin = false;
        await ready.Task.WaitAsync(deadline.Token);
        while (!session.ConfigurationRestorationSettled) await Task.Delay(10, deadline.Token);
        Assert.AreEqual(0, server.Commands.Count, "Acquiring a lease must not replay the saved Balanced mode.");
        Assert.AreEqual(0, notifications.Count);
        Assert.AreEqual(PerformanceMode.Turbo, session.State!.Controls.PerformanceMode);
        Assert.AreEqual(1, server.SessionOperations.Count(operation => operation == "configureClientSession"));
        Assert.AreEqual(CommandState.Applied, (await session.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false), deadline.Token)).State);
        Assert.AreEqual(server.Handle.SessionId, server.Commands.Single().ClientSessionId);
        Assert.AreEqual(CommandState.Applied, (await session.EndControlSessionAsync(deadline.Token))?.State);
    }

    [TestMethod]
    public async Task Managed_pending_start_does_not_replace_its_lease_when_the_service_changes_before_recovery()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string name = "Xisura-managed-pending-instance-" + Guid.NewGuid().ToString("N");
        await using var first = new ReconnectServer(name, CreateState(PerformanceMode.Turbo)) { RefuseBegin = true };
        using var paths = new ManagedSessionTestPaths();
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths), manageClientSession: true);
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<string>();
        session.NotificationRequested += notifications.Enqueue;
        await session.StartAsync(deadline.Token);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state => { if (state.Controls.PerformanceMode == PerformanceMode.Quiet) read.TrySetResult(); };
        await first.DisposeAsync();
        await using var second = new ReconnectServer(name, CreateState(PerformanceMode.Quiet));
        await read.Task.WaitAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.ReadOnly, session.Status);
        Assert.IsFalse(session.State!.Capabilities.IsAvailable("performanceMode"));
        Assert.IsTrue(session.State.Capabilities.IsAvailable("monitoring"));
        Assert.AreEqual(0, second.Commands.Count);
        Assert.AreEqual(0, second.SessionOperations.Count);
        Assert.AreEqual(0, notifications.Count);
        Assert.AreEqual(CommandState.Rejected, (await session.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false), deadline.Token)).State);
        Assert.AreEqual(0, second.Commands.Count);
    }

    [TestMethod]
    public async Task Disabled_follow_update_reads_revision_at_dispatch_and_keeps_real_conflict_notifications()
    {
        foreach (var scenario in new[] { "follow", "enable", "disable", "persist-conflict" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            string pipeName = "Xisura-follow-revision-" + Guid.NewGuid().ToString("N");
            var initial = CreateState(PerformanceMode.Balanced);
            initial = initial with { Controls = initial.Controls with { AdaptiveAutomation =
                new(false, false, null, "test", null, null, null, null) { Revision = 0 } } };
            await using var server = new ReconnectServer(pipeName, initial);
            var paths = new RecordingPathProvider();
            await using var session = new HomeControlSession(new ControlCenterClient(pipeName), new RecordingRadioController(),
                new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
            var notifications = new List<string>();
            session.NotificationRequested += notifications.Add;
            await session.StartAsync(deadline.Token);
            server.State = initial with { Controls = initial.Controls with
                { AdaptiveAutomation = initial.Controls.AdaptiveAutomation! with { Revision = 1 } } };
            server.CommandResultFactory = command =>
            {
                bool applied = scenario == "follow" && command is SetAdaptiveAutomationConfigurationCommand { ExpectedRevision: 1 };
                return new(command.OperationId, applied ? CommandState.Applied : CommandState.Rejected, null, RequiredUserAction.None,
                    applied ? null : ServiceError.Create(ErrorCode.IdempotencyConflict, command.OperationId, false), false)
                    { Mutation = HardwareMutation.NoMutation };
            };
            var key = PresetKey.Create(ControlModeId.Office, 1);
            var configuration = new AdaptiveAutomationConfiguration(scenario == "enable", AdaptiveAutomationStrategyId.BalancedAdaptive,
                new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
                new(key, key, key, key, key), []);
            var intent = scenario switch { "enable" => AutomationConfigurationIntent.Enable,
                "disable" => AutomationConfigurationIntent.Disable, _ => AutomationConfigurationIntent.Update };
            var result = await session.ExecuteAsync(new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), configuration)
                { ExpectedRevision = 0, Intent = intent }, deadline.Token);
            var submitted = server.Commands.OfType<SetAdaptiveAutomationConfigurationCommand>().Single();
            long expectedRevision = scenario is "follow" or "persist-conflict" ? 1 : 0;
            Assert.AreEqual(expectedRevision, submitted.ExpectedRevision, scenario);
            Assert.AreEqual(intent, submitted.Intent);
            Assert.AreEqual(scenario == "enable", submitted.Configuration.Enabled);
            Assert.HasCount(1, server.Commands, "Revision refresh must not replay hardware or retry commands.");
            Assert.AreEqual(scenario == "follow" ? CommandState.Applied : CommandState.Rejected, result.State, scenario);
            Assert.HasCount(scenario == "follow" ? 0 : 1, notifications, scenario);
        }
    }

    [TestMethod]
    public async Task Mode_peer_and_unfollowed_catalog_bypass_blocked_monitor_poll_and_keep_newer_readback()
    {
        var errorText = typeof(HomeControlSession).GetMethod("ErrorText",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(ServiceError)])!;
        var rejection = ServiceError.Create(ErrorCode.ValidationFailed, Guid.NewGuid(), false);
        rejection.Details["stage"] = "Preflight";
        rejection.Details["cause"] = "fieldNotWritable:temperatureLimitC";
        string message = (string)errorText.Invoke(null, [rejection])!;
        StringAssert.Contains(message, "CPU 温度上限");
        StringAssert.Contains(message, "均未应用");
        StringAssert.Contains(message, "随模式应用");
        var pendingGpu = ServiceError.Create(ErrorCode.RollbackFailed, Guid.NewGuid(), false);
        pendingGpu.Details["reason"] = "gpuPersistedRecoveryRequired";
        string pendingMessage = (string)errorText.Invoke(null, [pendingGpu])!;
        StringAssert.Contains(pendingMessage, "GPU");
        StringAssert.Contains(pendingMessage, "未写入");
        StringAssert.Contains(pendingMessage, "恢复验证");
        Assert.IsFalse(pendingMessage.Contains("重启", StringComparison.Ordinal));
        foreach (bool unified in new[] { false, true })
        {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string pipeName = "Jiaolong-priority-mode-" + Guid.NewGuid().ToString("N");
        await using var normal = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var priority = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var pollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleReplySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WriteAsync(NamedPipeServerStream stream, MessageEnvelope envelope)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.MessageEnvelope);
            await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), deadline.Token);
            await stream.WriteAsync(bytes, deadline.Token);
            await stream.FlushAsync(deadline.Token);
        }
        async Task ServeAsync(NamedPipeServerStream stream, bool modeConnection)
        {
            int polls = 0;
            try
            {
                await stream.WaitForConnectionAsync(deadline.Token);
                while (!deadline.IsCancellationRequested)
                {
                    var prefix = new byte[4];
                    await stream.ReadExactlyAsync(prefix, deadline.Token);
                    var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
                    await stream.ReadExactlyAsync(bytes, deadline.Token);
                    var message = JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.MessageEnvelope)!;
                    if (message is HelloEnvelope hello)
                    {
                        await WriteAsync(stream, new HelloAckEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow, hello.MessageId, "test"));
                        continue;
                    }
                    if (message is not RequestEnvelope request) continue;
                    Assert.AreEqual((ushort)1, request.ProtocolVersion.Minor, "Requests must use the negotiated protocol, including unified Apply.");
                    JsonElement payload;
                    if (request.Operation == "executeCommand" && modeConnection)
                    {
                        payload = JsonSerializer.SerializeToElement(new CommandResult(request.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                            { VerifiedPerformanceMode = PerformanceMode.Turbo,
                              ExecutionState = new(1, 1, 0, ControlModeId.Turbo, PerformanceMode.Turbo, [], [])
                                { NativeModeApplyState = ApplyState.Confirmed } }, ProtocolJsonContext.Default.CommandResult);
                    }
                    else if (request.Operation == "getDeviceState")
                    {
                        polls++;
                        if (polls == 2) { pollEntered.TrySetResult(); await releasePoll.Task.WaitAsync(deadline.Token); }
                        payload = JsonSerializer.SerializeToElement(CreateState(polls <= 2 ? PerformanceMode.Quiet : PerformanceMode.Turbo), ProtocolJsonContext.Default.HomeStateSnapshot);
                    }
                    else payload = JsonSerializer.SerializeToElement(true);
                    await WriteAsync(stream, new ResponseEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, request.MessageId, request.OperationId, ResponseStatus.Success, payload, null));
                    if (polls == 2 && request.Operation == "getDeviceState") staleReplySent.TrySetResult();
                }
            }
            catch (Exception error) when (error is OperationCanceledException or EndOfStreamException or IOException) { }
        }
        var normalServer = ServeAsync(normal, false);
        var modeServer = ServeAsync(priority, true);
        var paths = new RecordingPathProvider();
        await using var session = new HomeControlSession(new ControlCenterClient(pipeName), new RecordingRadioController(), new AppliedConfigurationRestore(paths));
        try
        {
            await session.StartAsync(deadline.Token);
            session.SupersedeAutomaticRestore();
            await pollEntered.Task.WaitAsync(deadline.Token);
            var modePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.StateChanged += snapshot => { if (snapshot.Controls.PerformanceMode == PerformanceMode.Turbo) modePublished.TrySetResult(); };
            async Task<CommandResult> ApplyCatalogModeAsync()
            {
                var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), new UserPreferencesStore(paths));
                return await catalog.ApplyAsync(ControlModeId.Turbo, PerformanceMode.Turbo,
                    ControlOrigin.ManualMode, token: deadline.Token)
                    ?? throw new AssertFailedException("The plain mode selection was not submitted.");
            }
            var applied = unified
                ? ApplyCatalogModeAsync()
                : session.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), deadline.Token);
            await modePublished.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(PerformanceMode.Turbo, session.State!.Controls.PerformanceMode);
            Assert.IsFalse(releasePoll.Task.IsCompleted);
            if (!unified) Assert.AreEqual(ApplyState.Confirmed, session.State.Controls.ExecutionState?.NativeModeApplyState);
            using var preparation = new CancellationTokenSource();
            var queuedPreset = session.ExecuteAsync(new SetCpuTuningBatchCommand(Guid.NewGuid(),
                [new CpuTuningPlan(85, null, null, null, null, null, null, null)], true), deadline.Token, preparation.Token);
            preparation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queuedPreset);
            Assert.AreEqual(HomeSessionStatus.Connected, session.Status);
            releasePoll.TrySetResult();
            await staleReplySent.Task.WaitAsync(deadline.Token);
            Assert.AreEqual(CommandState.Applied, (await applied.WaitAsync(deadline.Token)).State);
            await Task.Delay(100, deadline.Token);
            Assert.AreEqual(PerformanceMode.Turbo, session.State.Controls.PerformanceMode);
        }
        finally
        {
            releasePoll.TrySetResult();
            deadline.Cancel();
            await Task.WhenAll(normalServer, modeServer);
        }
        }
    }

    [TestMethod]
    public async Task Lighting_follow_enable_publishes_without_reapplying_mode_or_other_pages()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Update(saved => saved with { LightingFollowPreset = true });
        string pipeName = "Jiaolong-lighting-follow-" + Guid.NewGuid().ToString("N");
        await using var normal = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
        await using var priority = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
        await using var session = new HomeControlSession(new ControlCenterClient(pipeName),
            new RecordingRadioController(), new AppliedConfigurationRestore(paths));
        var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);

        await catalog.FollowPreferenceChangedAsync(ControlPageId.Lighting, true, deadline.Token);

        var commands = normal.Commands.Concat(priority.Commands).ToArray();
        Assert.HasCount(1, commands, "Lighting applies through its own coordinator after catalog publication.");
        Assert.IsInstanceOfType<PublishPresetCatalogCommand>(commands[0]);
        Assert.IsTrue(preferences.Load().LightingFollowPreset);
    }

    [TestMethod]
    public async Task Enabling_one_follow_page_does_not_reapply_other_followed_pages_and_disabling_keeps_hardware()
    {
        foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Gpu, ControlPageId.Fan })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var paths = new RecordingPathProvider();
            var preferences = new UserPreferencesStore(paths);
            preferences.Update(saved => saved with
            {
                PerformanceFollowPreset = true, GpuFollowPreset = true,
                FanFollowPreset = true, LightingFollowPreset = true
            });
            string pipeName = "Jiaolong-single-follow-" + Guid.NewGuid().ToString("N");
            await using var normal = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
            await using var priority = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced))
                { ServiceInstanceId = normal.ServiceInstanceId };
            await using var session = new HomeControlSession(new ControlCenterClient(pipeName),
                new RecordingRadioController(), new AppliedConfigurationRestore(paths));
            var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);

            await session.StartAsync(deadline.Token);
            while (!session.ConfigurationRestorationSettled) await Task.Delay(10, deadline.Token);
            await catalog.FollowPreferenceChangedAsync(page, true, deadline.Token);
            var request = normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().Single().Request;
            Assert.HasCount(1, request.Pages, "Enabling a page must not reapply other followed hardware groups.");
            Assert.AreEqual(page, request.Pages[0].Page);
            Assert.AreEqual(ControlOrigin.ModeFollow, request.Origin);

            preferences.Update(saved => page switch
            {
                ControlPageId.Performance => saved with { PerformanceFollowPreset = false },
                ControlPageId.Gpu => saved with { GpuFollowPreset = false },
                _ => saved with { FanFollowPreset = false }
            });
            await catalog.FollowPreferenceChangedAsync(page, false, deadline.Token);
            Assert.HasCount(1, normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().ToArray(),
                "Disabling follow publishes metadata without restoring or applying hardware.");

            await catalog.ApplyAsync(ControlModeId.Office, PerformanceMode.Quiet, ControlOrigin.ManualMode, token: deadline.Token);
            var modeRequest = normal.Commands.Concat(priority.Commands).OfType<ApplyControlRequestCommand>().Last().Request;
            CollectionAssert.AreEquivalent(Enum.GetValues<ControlPageId>().Where(other => other != page).ToArray(),
                modeRequest.Pages.Select(selection => selection.Page).ToArray(),
                "A mode change must still apply every remaining followed page.");
        }
    }

    [TestMethod]
    public async Task Mode_fast_path_preserves_each_followed_page_and_explicit_preset()
    {
        foreach (ControlPageId? followedPage in Enum.GetValues<ControlPageId>().Select(page => (ControlPageId?)page).Append(null))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var paths = new RecordingPathProvider();
            var preferences = new UserPreferencesStore(paths);
            preferences.Update(saved => saved with
            {
                PerformanceFollowPreset = followedPage == ControlPageId.Performance,
                GpuFollowPreset = followedPage == ControlPageId.Gpu,
                FanFollowPreset = followedPage == ControlPageId.Fan,
                LightingFollowPreset = followedPage == ControlPageId.Lighting
            });
            string pipeName = "Jiaolong-mode-pages-" + Guid.NewGuid().ToString("N");
            await using var normal = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
            await using var priority = new ReconnectServer(pipeName, CreateState(PerformanceMode.Balanced));
            await using var session = new HomeControlSession(new ControlCenterClient(pipeName),
                new RecordingRadioController(), new AppliedConfigurationRestore(paths));
            var catalog = new PresetCatalogController(session, new ControlPresetStore(paths), preferences);
            var page = followedPage ?? ControlPageId.Gpu;
            var key = PresetKey.Create(ControlModeId.Office, followedPage is null ? 1 : 2);
            var result = await catalog.ApplyAsync(ControlModeId.Office, PerformanceMode.Quiet,
                followedPage is null ? ControlOrigin.PresetUse : ControlOrigin.ManualMode,
                followedPage is null ? page : null, followedPage is null ? key : null, deadline.Token);

            Assert.IsNotNull(result);
            Assert.AreEqual(CommandState.Rejected, result.State, "The fake peer never applies hardware commands.");
            var commands = normal.Commands.Concat(priority.Commands).ToArray();
            var publication = commands.OfType<PublishPresetCatalogCommand>().Single();
            var request = commands.OfType<ApplyControlRequestCommand>().Single().Request;
            Assert.HasCount(2, commands);
            Assert.HasCount(1, request.Pages);
            var selection = request.Pages[0];
            Assert.AreEqual(page, selection.Page);
            Assert.IsTrue(selection.Follow);
            Assert.AreEqual(key, selection.Key);
            Assert.AreEqual(publication.Items.Single(item => item.Page == page && item.Key == key).Revision, selection.Revision);
        }
    }

    [TestMethod]
    public async Task Failed_control_read_recovers_after_startup_restore_is_superseded()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string pipeName = "Jiaolong-recovery-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var recovered = new TaskCompletionSource<HomeStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        int controlReads = 0;
        int commands = 0;
        var expected = CreateState(PerformanceMode.Balanced);
        expected = expected with { Capabilities = expected.Capabilities with { SupportState = DeviceSupportState.ReadOnly } };

        async Task<MessageEnvelope> ReadAsync()
        {
            var prefix = new byte[4];
            await server.ReadExactlyAsync(prefix, deadline.Token);
            var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
            await server.ReadExactlyAsync(payload, deadline.Token);
            return JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.MessageEnvelope)!;
        }
        async Task WriteAsync(MessageEnvelope envelope)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.MessageEnvelope);
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
            await server.WriteAsync(prefix, deadline.Token);
            await server.WriteAsync(payload, deadline.Token);
        }
        var host = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync(deadline.Token);
                var hello = (HelloEnvelope)await ReadAsync();
                await WriteAsync(new HelloAckEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, hello.MessageId, "test"));
                while (!deadline.IsCancellationRequested)
                {
                    var request = (RequestEnvelope)await ReadAsync();
                    if (request.Operation == "subscribeTelemetry") continue;
                    if (request.Operation != "getDeviceState") { Interlocked.Increment(ref commands); continue; }
                    bool fail = Interlocked.Increment(ref controlReads) == 1;
                    await WriteAsync(new ResponseEnvelope(new(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow,
                        request.MessageId, request.OperationId, fail ? ResponseStatus.Error : ResponseStatus.Success,
                        fail ? null : JsonSerializer.SerializeToElement(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        fail ? ServiceError.Create(ErrorCode.ServiceUnavailable, request.OperationId, false) : null));
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }
        }, deadline.Token);
        var restorePaths = new RecordingPathProvider();
        var restore = new AppliedConfigurationRestore(restorePaths);
        var savedMode = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced);
        await restore.RecordAppliedAsync(savedMode, new(savedMode.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { VerifiedPerformanceMode = savedMode.Mode }, CancellationToken.None);
        try
        {
            await using var session = new HomeControlSession(new ControlCenterClient(pipeName), new RecordingRadioController(), restore);
            Assert.IsFalse(session.ConfigurationRestorationSettled);
            var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ConfigurationRestorationCompleted += () => settled.TrySetResult(session.ConfigurationRestorationSettled);
            session.StateChanged += snapshot => recovered.TrySetResult(snapshot);
            await session.StartAsync(deadline.Token);
            Assert.IsFalse(session.ConfigurationRestorationSettled);
            session.SupersedeAutomaticRestore();
            Assert.AreEqual(HomeSessionStatus.Disconnected, session.Status);
            var actual = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(expected.Controls.PerformanceMode, actual.Controls.PerformanceMode);
            Assert.AreEqual(HomeSessionStatus.ReadOnly, session.Status);
            Assert.IsTrue(controlReads >= 2);
            Assert.AreEqual(0, commands);
            while (!session.ConfigurationRestorationSettled) await Task.Delay(20, deadline.Token);
            Assert.IsTrue(session.ConfigurationRestorationSettled);
            Assert.IsTrue(await settled.Task.WaitAsync(deadline.Token));
            Assert.AreEqual(0, commands);
        }
        finally
        {
            deadline.Cancel(); await host;
            var root = Path.GetFullPath(restorePaths.LocalAppDataRoot);
            Assert.IsTrue(root.StartsWith(Path.Combine(Path.GetTempPath(), "JiaolongTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Control_state_refresh_is_fast_enough_for_hardware_hotkeys()
    {
        Assert.IsTrue(HomeControlSession.ControlStateRefreshInterval <= TimeSpan.FromMilliseconds(250));
    }

    [TestMethod]
    public async Task External_mode_change_reaches_the_session_without_a_full_second_wait_or_hardware_commands()
    {
        var name = "Xisura-hotkey-sync-" + Guid.NewGuid().ToString("N");
        var paths = new RecordingPathProvider();
        await using var server = new ReconnectServer(name, CreateState(PerformanceMode.Turbo));
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths));
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state => { if (state.Controls.PerformanceMode == PerformanceMode.Quiet) updated.TrySetResult(); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(deadline.Token);
        await Task.Delay(50, deadline.Token);
        server.State = CreateState(PerformanceMode.Quiet);
        await updated.Task.WaitAsync(TimeSpan.FromMilliseconds(400), deadline.Token);
        Assert.AreEqual(PerformanceMode.Quiet, session.State?.Controls.PerformanceMode);
        Assert.AreEqual(0, server.HardwareCommands);
    }

    [TestMethod]
    public async Task Disconnected_client_releases_the_native_pipe_handle()
    {
        string pipeName = "Xisura-dispose-test-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(pipeName, CreateState(PerformanceMode.Turbo));
        await using var client = new ControlCenterClient(pipeName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(deadline.Token);
        var pipe = (NamedPipeClientStream)typeof(ControlCenterClient).GetField("pipe",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        var handle = pipe.SafePipeHandle;
        await server.DisposeAsync();
        for (int i = 0; i < 50 && !handle.IsClosed; i++) await Task.Delay(20, deadline.Token);
        Assert.IsTrue(handle.IsClosed, "Remote closure must release the OS handle before the next connection.");
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public async Task Silent_handshake_times_out_without_requiring_caller_cancellation()
    {
        string pipeName = "Xisura-handshake-test-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(pipeName, CreateState(PerformanceMode.Turbo), silentHandshake: true);
        await using var client = new ControlCenterClient(pipeName);
        var connect = client.ConnectAsync(CancellationToken.None);
        try
        {
            var completed = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.AreSame(connect, completed, "An accepted pipe without HelloAck must not hold the connection gate indefinitely.");
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => connect);
            Assert.IsFalse(client.IsConnected);
        }
        finally
        {
            await server.DisposeAsync();
            try { await connect; } catch { }
        }
    }

    [TestMethod]
    public async Task Session_reconnects_after_service_restart_without_replaying_saved_mode()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string pipeName = "Xisura-reconnect-test-" + Guid.NewGuid().ToString("N");
        var paths = new RecordingPathProvider();
        var preferences = new UserPreferencesStore(paths);
        preferences.Save(preferences.Load() with { PerformanceFollowPreset = false });
        var restore = new AppliedConfigurationRestore(paths);
        var saved = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced);
        await restore.RecordAppliedAsync(saved, new(saved.OperationId, CommandState.Applied, null,
            RequiredUserAction.None, null, false) { VerifiedPerformanceMode = saved.Mode }, deadline.Token);
        var first = new ReconnectServer(pipeName, CreateState(PerformanceMode.Turbo));
        ReconnectServer? second = null;
        try
        {
            await using var session = new HomeControlSession(new ControlCenterClient(pipeName), new RecordingRadioController(),
                restore, new DeviceSchedulingBaselineStore(paths));
            typeof(HomeControlSession).GetField("preferences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(session, preferences);
            var recovered = new TaskCompletionSource<HomeStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = new System.Collections.Concurrent.ConcurrentQueue<string>();
            session.NotificationRequested += notifications.Enqueue;
            session.StateChanged += state => { if (state.Controls.PerformanceMode == PerformanceMode.Quiet) recovered.TrySetResult(state); };
            await session.StartAsync(deadline.Token);
            Assert.AreEqual(PerformanceMode.Turbo, session.State?.Controls.PerformanceMode);
            await first.DisposeAsync();
            second = new ReconnectServer(pipeName, CreateState(PerformanceMode.Quiet));
            var state = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token);
            Assert.AreEqual(PerformanceMode.Quiet, state.Controls.PerformanceMode);
            Assert.AreEqual(HomeSessionStatus.Connected, session.Status);
            Assert.AreEqual(0, first.HardwareCommands + second.HardwareCommands);
            Assert.IsTrue(notifications.IsEmpty, "Automatic reconnect must not leave an operation-failure dialog open.");
            session.ReportOperationFailure("Manual command failed");
            Assert.IsTrue(notifications.TryDequeue(out var failure));
            Assert.AreEqual("Manual command failed", failure);
        }
        finally
        {
            await first.DisposeAsync();
            if (second is not null) await second.DisposeAsync();
            var root = Path.GetFullPath(paths.LocalAppDataRoot);
            Assert.IsTrue(root.StartsWith(Path.Combine(Path.GetTempPath(), "JiaolongTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Caller_cancellation_of_a_silent_handshake_is_preserved()
    {
        string pipeName = "Xisura-cancel-test-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(pipeName, CreateState(PerformanceMode.Turbo), silentHandshake: true);
        await using var client = new ControlCenterClient(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ConnectAsync(cancellation.Token));
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsFalse(client.IsConnected);
    }

    private sealed class ManagedSessionTestPaths : IUserPreferencesPathProvider, IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "XisuraManagedSessionTests", Guid.NewGuid().ToString("N"));
        public string LocalAppDataRoot => Path.Combine(root, "LocalAppData");
        public string ProgramDataRoot => Path.Combine(root, "ProgramData");
        public void RecordWrite(string path) { }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Managed_session_keeps_reading_after_service_restart_and_allows_only_authenticated_exit_recovery()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string name = "Xisura-managed-restart-" + Guid.NewGuid().ToString("N");
        await using var first = new ReconnectServer(name, CreateState(PerformanceMode.Turbo));
        using var paths = new ManagedSessionTestPaths();
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths), manageClientSession: true);
        await session.StartAsync(deadline.Token);
        var notifications = new List<string>();
        session.NotificationRequested += notifications.Add;
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state => { if (state.Controls.PerformanceMode == PerformanceMode.Quiet) read.TrySetResult(); };
        await first.DisposeAsync();
        await using var second = new ReconnectServer(name, CreateState(PerformanceMode.Quiet));
        await read.Task.WaitAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.ReadOnly, session.Status);
        Assert.AreEqual(PerformanceMode.Quiet, session.State!.Controls.PerformanceMode);
        Assert.IsFalse(session.State.Capabilities.IsAvailable("performanceMode"));
        Assert.IsTrue(session.State.Capabilities.IsAvailable("monitoring"));
        Assert.AreEqual(0, notifications.Count, "A service restart must not add a recurring home-page notification.");
        await session.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false), deadline.Token);
        Assert.AreEqual(0, second.Commands.Count);
        Assert.AreEqual(0, second.SessionOperations.Count, "A restarted service must not receive a replacement Begin or the old lease.");
        var exit = await session.EndControlSessionAsync(deadline.Token);
        Assert.AreEqual(CommandState.Applied, exit?.State);
        CollectionAssert.AreEqual(new[] { "endClientSession" }, second.SessionOperations,
            "Only the restarted service may authorize recovery of the original capture; never begin or write with the old lease.");
        Assert.AreEqual(0, second.Commands.Count);
    }

    [TestMethod]
    public async Task Read_only_service_startup_is_quiet_and_does_not_begin_a_control_session()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var original = CreateState(PerformanceMode.Turbo);
        original = original with { Capabilities = original.Capabilities with { SupportState = DeviceSupportState.ReadOnly } };
        string name = "Xisura-readonly-startup-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(name, original);
        using var paths = new ManagedSessionTestPaths();
        await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
            new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths), manageClientSession: true);
        var notifications = new List<string>();
        session.NotificationRequested += notifications.Add;
        await session.StartAsync(deadline.Token);
        Assert.AreEqual(HomeSessionStatus.ReadOnly, session.Status);
        Assert.AreEqual(PerformanceMode.Turbo, session.State!.Controls.PerformanceMode);
        Assert.AreEqual(0, notifications.Count, "A successful read-only startup is not a failed user operation.");
        Assert.AreEqual(0, server.SessionOperations.Count);
        Assert.AreEqual(0, server.Commands.Count);
    }

    [TestMethod]
    public async Task Managed_session_without_service_identity_disables_writes_preserving_readback_and_local_controls()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var original = CreateState(PerformanceMode.Balanced);
        var field = new FieldCapability("acMaxFrequencyMhz", CapabilityState.Available, true, true,
            400, 6000, 1, "MHz", "fake", "PreservesSelectedMode", DateTimeOffset.UtcNow, 7);
        original = original with
        {
            Capabilities = original.Capabilities with
            {
                Items = [.. original.Capabilities.Items,
                    new("quickSetting:wifi", CapabilityState.Available, null),
                    new("quickSetting:bluetooth", CapabilityState.Available, null),
                    new("fanControl", CapabilityState.Unavailable, "unsupported")],
                Fields = [field], IsOnAcPower = true
            },
            Telemetry = new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", 42, 40, 12, 8)
        };
        foreach (Guid? identity in new Guid?[] { null, Guid.Empty })
        {
            string name = "Xisura-missing-home-identity-" + Guid.NewGuid().ToString("N");
            await using var server = new ReconnectServer(name, original) { ServiceInstanceId = identity };
            using var paths = new ManagedSessionTestPaths();
            await using var session = new HomeControlSession(new ControlCenterClient(name), new RecordingRadioController(),
                new AppliedConfigurationRestore(paths), new DeviceSchedulingBaselineStore(paths), manageClientSession: true);
            var notifications = new List<string>();
            session.NotificationRequested += notifications.Add;
            await session.StartAsync(deadline.Token);
            Assert.AreEqual(HomeSessionStatus.ReadOnly, session.Status);
            var actual = session.State!;
            Assert.AreEqual(original.Controls.PerformanceMode, actual.Controls.PerformanceMode);
            Assert.AreEqual(original.Controls.StrongCooling, actual.Controls.StrongCooling);
            CollectionAssert.AreEqual(original.Controls.QuickSettings, actual.Controls.QuickSettings);
            Assert.AreEqual(original.Telemetry, actual.Telemetry);
            Assert.AreEqual(original.Capabilities.Identity, actual.Capabilities.Identity);
            Assert.AreEqual(true, actual.Capabilities.IsOnAcPower);
            Assert.IsFalse(actual.Capabilities.IsAvailable("performanceMode"));
            Assert.IsTrue(actual.Capabilities.IsAvailable("monitoring"));
            Assert.IsTrue(actual.Capabilities.IsAvailable("quickSetting:wifi"));
            Assert.IsTrue(actual.Capabilities.IsAvailable("quickSetting:bluetooth"));
            Assert.AreEqual(original.Capabilities.Items[^1], actual.Capabilities.Items[^1]);
            Assert.AreEqual(field with { Writable = false, Availability = CapabilityState.ReadOnly }, actual.Capabilities.Fields.Single());
            Assert.AreEqual(DeviceSupportState.Ready, original.Capabilities.SupportState);
            Assert.IsTrue(original.Capabilities.Fields.Single().Writable, "Read-only presentation must not mutate the received snapshot.");
            Assert.AreEqual(0, notifications.Count);
            Assert.AreEqual(0, server.SessionOperations.Count);
            Assert.AreEqual(0, server.Commands.Count);
        }
    }

    [TestMethod]
    public async Task Managed_transport_accepts_same_service_reconnect_and_blocks_peer_after_instance_change()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string name = "Xisura-bound-reconnect-" + Guid.NewGuid().ToString("N");
        await using var first = new ReconnectServer(name, CreateState(PerformanceMode.Turbo));
        await using var client = new ControlCenterClient(name);
        client.RequireControlServiceIdentity();
        await client.GetHomeStateAsync(deadline.Token);
        var command = new SetLidLogoCommand(Guid.NewGuid(), false) { ClientSessionId = Guid.NewGuid() };
        await client.SendCommandAsync(command, deadline.Token);
        Assert.AreEqual(1, first.Commands.Count);
        await first.DisposeAsync();
        await using var second = new ReconnectServer(name, CreateState(PerformanceMode.Turbo)) { ServiceInstanceId = first.ServiceInstanceId };
        await client.GetHomeStateAsync(deadline.Token);
        await client.SendCommandAsync(command with { OperationId = Guid.NewGuid() }, deadline.Token);
        Assert.AreEqual(1, second.Commands.Count);
        await using var peer = client.CreatePeer();
        await using var changed = new ReconnectServer(name, CreateState(PerformanceMode.Turbo));
        await peer.GetHomeStateAsync(deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SendCommandAsync(command, deadline.Token));
        Assert.AreEqual(0, changed.Commands.Count);
    }

    [TestMethod]
    public async Task Missing_service_instance_keeps_managed_transport_read_only_and_legacy_reads_compatible()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string name = "Xisura-missing-instance-" + Guid.NewGuid().ToString("N");
        await using var server = new ReconnectServer(name, CreateState(PerformanceMode.Turbo)) { ServiceInstanceId = null };
        await using var client = new ControlCenterClient(name);
        client.RequireControlServiceIdentity();
        Assert.IsNotNull(await client.GetHomeStateAsync(deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.BeginClientSessionAsync(deadline.Token));
        Assert.AreEqual(0, server.SessionOperations.Count);
        Assert.AreEqual(0, server.Commands.Count);
    }

    private sealed class ReconnectServer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly CancellationTokenSource stopping = new();
        private readonly Task task;
        private int disposed;
        public HomeStateSnapshot State { get; set; }
        public Func<HomeStateSnapshot>? StateFactory { get; set; }
        public int HardwareCommands { get; private set; }
        public List<HardwareCommand> Commands { get; } = [];
        public Func<HardwareCommand, CommandResult>? CommandResultFactory { get; set; }
        public Func<RequestEnvelope, CancellationToken, Task<bool>>? BeforeResponseAsync { get; set; }
        public ClientControlSessionHandle Handle { get; } = new(Guid.NewGuid(), 1, new string('A', 64), DateTimeOffset.UtcNow);
        public List<string> SessionOperations { get; } = [];
        public bool RefuseConfiguration { get; set; }
        public bool RefuseBegin { get; set; }
        public Guid? ServiceInstanceId { get; set; } = Guid.NewGuid();

        public ReconnectServer(string name, HomeStateSnapshot state, bool silentHandshake = false)
        {
            State = state;
            pipe = new(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            task = ServeAsync(state, silentHandshake);
        }

        private async Task ServeAsync(HomeStateSnapshot state, bool silentHandshake)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stopping.Token);
                var hello = (HelloEnvelope)await ReadAsync();
                if (silentHandshake) { await Task.Delay(Timeout.Infinite, stopping.Token); return; }
                await WriteAsync(new HelloAckEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow, hello.MessageId, "test")
                    { ServiceInstanceId = ServiceInstanceId });
                while (!stopping.IsCancellationRequested)
                {
                    var request = (RequestEnvelope)await ReadAsync();
                    if (request.Operation == "subscribeTelemetry") continue;
                    if (request.Operation != "getDeviceState") HardwareCommands++;
                    var payload = JsonSerializer.SerializeToElement(StateFactory?.Invoke() ?? State, ProtocolJsonContext.Default.HomeStateSnapshot);
                    if (request.Operation is "beginClientSession" or "configureClientSession" or "endClientSession")
                    {
                        SessionOperations.Add(request.Operation);
                        bool refusedBegin = request.Operation == "beginClientSession" && RefuseBegin;
                        var outcome = new CommandResult(request.OperationId,
                            refusedBegin ? CommandState.RecoveryRequired : request.Operation == "configureClientSession" && RefuseConfiguration ? CommandState.Rejected : CommandState.Applied,
                            null, RequiredUserAction.None, refusedBegin ? ServiceError.Create(ErrorCode.RollbackFailed, request.OperationId, false) : null, false)
                            { ClientSession = request.Operation == "beginClientSession" && !refusedBegin ? Handle : null, Mutation = HardwareMutation.NoMutation };
                        payload = JsonSerializer.SerializeToElement(outcome, ProtocolJsonContext.Default.CommandResult);
                    }
                    if (request.Operation == "executeCommand")
                    {
                        var command = request.Payload.Deserialize(ProtocolJsonContext.Default.HardwareCommand)!;
                        Commands.Add(command);
                        var result = CommandResultFactory?.Invoke(command) ?? (command is PublishPresetCatalogCommand
                            ? new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                                { CatalogRevision = 1, Mutation = HardwareMutation.NoMutation }
                            : new CommandResult(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                                ServiceError.Create(ErrorCode.ValidationFailed, command.OperationId, false), false)
                                { Mutation = HardwareMutation.NoMutation });
                        payload = JsonSerializer.SerializeToElement(result, ProtocolJsonContext.Default.CommandResult);
                    }
                    if (BeforeResponseAsync is { } beforeResponse && !await beforeResponse(request, stopping.Token))
                    {
                        await pipe.DisposeAsync();
                        return;
                    }
                    await WriteAsync(new ResponseEnvelope(new(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow,
                        request.MessageId, request.OperationId, ResponseStatus.Success,
                        payload, null));
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
        }

        private async Task<MessageEnvelope> ReadAsync()
        {
            var prefix = new byte[4];
            await pipe.ReadExactlyAsync(prefix, stopping.Token);
            var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
            await pipe.ReadExactlyAsync(bytes, stopping.Token);
            return JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.MessageEnvelope)!;
        }

        private async Task WriteAsync(MessageEnvelope envelope)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.MessageEnvelope);
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            await pipe.WriteAsync(prefix, stopping.Token);
            await pipe.WriteAsync(bytes, stopping.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping.Cancel();
            await pipe.DisposeAsync();
            await task;
            stopping.Dispose();
        }
    }

    [TestMethod]
    public void Equivalent_state_snapshots_do_not_trigger_a_second_visual_refresh()
    {
        var first = CreateState(PerformanceMode.Balanced);
        Assert.IsFalse(HomeStateEquivalence.AreEquivalent(first, first with { Controls = first.Controls with { KeyboardLightingNativeCycleAvailable = true } }));
        var second = CreateState(PerformanceMode.Balanced);

        Assert.IsTrue(HomeStateEquivalence.AreEquivalent(first, second));
        Assert.IsFalse(HomeStateEquivalence.AreEquivalent(first, CreateState(PerformanceMode.Quiet)));
    }

    [TestMethod]
    public void Telemetry_only_changes_do_not_repaint_control_state_but_mode_and_power_changes_do()
    {
        var baseline = CreateState(PerformanceMode.Balanced) with
        {
            Telemetry = new HardwareSnapshot(DateTimeOffset.UtcNow, "Connected", 65, 50, 35, 20) { AcPowerConnected = true }
        };
        var sample = baseline with { Telemetry = baseline.Telemetry! with { CpuTemperatureC = 66, CpuFrequencyMhz = 3200, GpuUsagePercent = 8 } };
        Assert.IsTrue(HomeStateEquivalence.HaveSameControls(baseline, sample));
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(baseline, sample with { Controls = sample.Controls with { PerformanceMode = PerformanceMode.Quiet } }));
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(baseline, sample with { Telemetry = sample.Telemetry! with { AcPowerConnected = false } }));
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(baseline, sample with { Telemetry = sample.Telemetry! with { HardwareState = "Unavailable" } }));
        var auto = new AdaptiveAutomationStatus(true, true, null, "observing", null, null, null, null) { Revision = 2 };
        var automatic = sample with { Controls = sample.Controls with { AdaptiveAutomation = auto } };
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(sample, automatic));
        Assert.IsTrue(HomeStateEquivalence.HaveSameControls(automatic, automatic with
            { Controls = automatic.Controls with { AdaptiveAutomation = auto with { LastEvaluationUtc = DateTimeOffset.UtcNow } } }));
        Assert.IsFalse(HomeStateEquivalence.HaveSameControls(automatic, automatic with
            { Controls = automatic.Controls with { AdaptiveAutomation = auto with { Enabled = false, Revision = 3 } } }));
    }

    [TestMethod]
    public void Gpu_vf_readback_change_refreshes_the_curve()
    {
        var first = CreateState(PerformanceMode.Balanced);
        var before = first with { Controls = first.Controls with { GpuVf = new GpuVfState([new GpuVfNode(750, 1800, 75_000)], -200_000, 200_000, null) } };
        var same = first with { Controls = first.Controls with { GpuVf = new GpuVfState([new GpuVfNode(750, 1800, 75_000)], -200_000, 200_000, null) } };
        var changed = first with { Controls = first.Controls with { GpuVf = new GpuVfState([new GpuVfNode(750, 1800, 90_000)], -200_000, 200_000, null) } };
        Assert.IsTrue(HomeStateEquivalence.AreEquivalent(before, same));
        Assert.IsFalse(HomeStateEquivalence.AreEquivalent(before, changed));
    }

    [TestMethod]
    public void Read_only_message_is_compact()
    {
        StringAssert.Contains(HomeSessionMessages.ReadOnlyMode, "安全只读模式");
        Assert.AreEqual(9, HomeSessionMessages.ReadOnlyMode.Length);
    }

    [TestMethod]
    public async Task Wifi_toggle_uses_interactive_radio_controller()
    {
        var radio = new RecordingRadioController();
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(QuickSettingKind.Wifi, radio.LastSetting);
        Assert.IsTrue(radio.LastValue);
    }

    [TestMethod]
    public async Task Bluetooth_toggle_uses_interactive_radio_controller()
    {
        var radio = new RecordingRadioController();
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Bluetooth, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(QuickSettingKind.Bluetooth, radio.LastSetting);
        Assert.IsTrue(radio.LastValue);
    }

    [TestMethod]
    public async Task Radio_readback_mismatch_restores_and_verifies_previous_state()
    {
        var radio = new MismatchedRadioController(restoreSucceeds: true);
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(ErrorCode.ReadBackMismatch, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { true, false }, radio.Writes);
        Assert.IsFalse(radio.WifiEnabled);
    }

    [TestMethod]
    public async Task Radio_restore_mismatch_reports_recovery_required()
    {
        var radio = new MismatchedRadioController(restoreSucceeds: false);
        await using var session = new HomeControlSession(radioController: radio);

        var result = await session.ExecuteAsync(
            new SetQuickSettingCommand(Guid.NewGuid(), QuickSettingKind.Wifi, true),
            CancellationToken.None);

        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(ErrorCode.RollbackFailed, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { true, false }, radio.Writes);
    }

    private static HomeStateSnapshot CreateState(PerformanceMode mode) => new(
        new CapabilitySnapshot([
            new CapabilityDescriptor("performanceMode", CapabilityState.Available, null),
            new CapabilityDescriptor("monitoring", CapabilityState.Available, null)
        ])
        {
            Identity = new HardwareIdentity("board", "bios", "cpu", "gpu"),
            SupportState = DeviceSupportState.Ready,
            Reason = null
        },
        null,
        new HomeControlState(mode, false, [
            new QuickSettingStatus(QuickSettingKind.Wifi, true, null),
            new QuickSettingStatus(QuickSettingKind.Bluetooth, false, null)
        ]));

    private sealed class RecordingRadioController : IInteractiveRadioController
    {
        private bool wifiEnabled = true;
        private bool bluetoothEnabled;
        public QuickSettingKind LastSetting { get; private set; }
        public bool LastValue { get; private set; }

        public Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HomeRadioSnapshot(wifiEnabled, bluetoothEnabled, null));

        public Task<bool> SetAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken)
        {
            LastSetting = setting;
            LastValue = enabled;
            if (setting == QuickSettingKind.Wifi) wifiEnabled = enabled;
            if (setting == QuickSettingKind.Bluetooth) bluetoothEnabled = enabled;
            return Task.FromResult(true);
        }
    }

    private sealed class MismatchedRadioController(bool restoreSucceeds) : IInteractiveRadioController
    {
        public bool WifiEnabled { get; private set; }
        public List<bool> Writes { get; } = [];

        public Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HomeRadioSnapshot(WifiEnabled, false, null));

        public Task<bool> SetAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken)
        {
            Writes.Add(enabled);
            WifiEnabled = enabled ? false : !restoreSucceeds;
            return Task.FromResult(true);
        }
    }
}
