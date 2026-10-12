using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using System.Text.Json;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ControlExecutionCoordinatorTests
{
    [TestMethod]
    public async Task Suspend_during_preset_capture_or_write_stops_next_hardware_step_and_preserves_mutation_truth()
    {
        foreach (string stage in new[] { "capture", "mode", "cpu", "direct-mode", "rejected-mode" })
        {
            string journalFolder = Path.Combine(Path.GetTempPath(), "xisura-preset-suspend-" + Guid.NewGuid().ToString("N"));
            var journal = new ControlExecutionJournal(journalFolder);
            try
            {
                var provider = new PipelineProvider { RejectModeWithoutMutation = stage == "rejected-mode" };
                var runtime = new HomeServiceRuntime(provider, journal: journal);
                await runtime.InitializeAsync(CancellationToken.None);
                var key = PresetKey.Create(ControlModeId.Office, 1);
                var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance,
                    key, "Suspend boundary", JsonSerializer.SerializeToElement(new { useOfficialCpuPolicy = false, temperatureLimitC = 95,
                        splWatts = 45, spptWatts = 65, acMaxFrequencyMhz = 3000, dcMaxFrequencyMhz = 4500,
                        isBoostEnabled = true, windowsPowerSchemeId = Guid.Empty, acMinActiveCoresPercent = (int?)null,
                        dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null, advancedCpuTuning = (object?)null }), DateTimeOffset.UtcNow)
                { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = SettingRequest.Set(3000) } };
                var published = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
                    CancellationToken.None, userSid: "boundary-user");
                Assert.AreEqual(CommandState.Applied, published.State);
                int readsAtSuspend = -1;
                void Suspend()
                {
                    readsAtSuspend = provider.BaselineReads;
                    runtime.PreparePowerTransition(false);
                }
                if (stage == "capture") provider.AfterBaseline = () => { if (readsAtSuspend < 0) Suspend(); };
                else provider.AfterWrite = command =>
                {
                    if (readsAtSuspend < 0 && (stage is "mode" or "direct-mode" or "rejected-mode" && command is SetPerformanceModeCommand ||
                        stage == "cpu" && command is SetCpuTuningBatchCommand)) Suspend();
                };
                Guid id = Guid.NewGuid();
                HardwareCommand command = stage is "direct-mode" or "rejected-mode" ? new SetPerformanceModeCommand(id, PerformanceMode.Quiet)
                    : new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                        ControlModeId.Office, PerformanceMode.Quiet, [new(ControlPageId.Performance, true, key, 1)]));
                var result = await runtime.ExecuteAsync(command, CancellationToken.None, userSid: "boundary-user");
                Assert.IsTrue(runtime.IsSuspended);
                bool noMutation = stage is "capture" or "rejected-mode";
                Assert.AreEqual(noMutation ? 0 : stage == "cpu" ? 2 : 1, provider.Writes,
                    "The next write must stop at the suspend boundary: " + stage);
                Assert.AreEqual(readsAtSuspend, provider.BaselineReads, "No follow-up read during suspend: " + stage);
                Assert.AreEqual(noMutation ? CommandState.Rejected : CommandState.RecoveryRequired, result.State);
                Assert.AreEqual(noMutation ? HardwareMutation.NoMutation : HardwareMutation.Unknown, result.Mutation);
                Assert.IsNotNull(result.Error);
                Assert.AreEqual("LifecycleGuard", result.Error.Details["stage"]);
                var saved = await journal.ReadAsync(CancellationToken.None);
                Assert.AreEqual(noMutation ? 0 : 1, saved.Unresolved.Length,
                    "Only zero-write failures may clear their pending journal entry.");
                Assert.AreEqual(result, await runtime.ExecuteAsync(command, CancellationToken.None, userSid: "boundary-user"),
                    "Same operation retries must not repeat a partial write.");
            }
            finally { if (Directory.Exists(journalFolder)) Directory.Delete(journalFolder, true); }
        }
    }

    [TestMethod]
    public async Task Unreadable_official_gpu_limit_without_owned_baseline_is_not_confirmed()
    {
        var provider = new PipelineProvider { ConfirmModes = true };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Office, 1);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "GPU official",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = (int?)null, resetCoreFrequencyLimit = true,
                memoryOffsetKhz = (int?)null, coreOffsetKhz = (int?)null, vfOffsetsKhz = (int[]?)null }),
            DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Official } };
        var published = await runtime.ExecuteAsync(
            new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, published.State, published.Error?.Code + ": " +
            string.Join(", ", published.Error?.Details?.Select(pair => pair.Key + "=" + pair.Value) ?? []));
        Guid id = Guid.NewGuid();
        var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, long.MaxValue,
            ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
            [new(ControlPageId.Gpu, true, key, 1)])), CancellationToken.None, userSid: "user-a");
        var page = result.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu);
        Assert.AreEqual(ApplyState.Partial, page.State);
        CollectionAssert.Contains(page.SkippedFields, "coreFrequencyLimitMhz");
        CollectionAssert.DoesNotContain(page.UnverifiedFields, "coreFrequencyLimitMhz");
        CollectionAssert.DoesNotContain(page.ConfirmedFields, "coreFrequencyLimitMhz");
        Assert.AreEqual(1, provider.Writes, "No GPU reset is safe without a baseline.");
    }

    [TestMethod]
    public async Task Unreadable_gpu_clock_limit_is_skipped_for_follow_and_explicit_preset()
    {
        var provider = new PipelineProvider { ConfirmModes = true };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Office, 1);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "Old GPU cap",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = 2280, resetCoreFrequencyLimit = false,
                memoryOffsetKhz = (int?)null, coreOffsetKhz = (int?)null, vfOffsetsKhz = (int[]?)null }),
            DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Set(2280) } };
        var published = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
            CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, published.State, published.Error?.Code.ToString());
        Guid id = Guid.NewGuid();
        var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, long.MaxValue,
            ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
            [new(ControlPageId.Gpu, true, key, 1)])), CancellationToken.None, userSid: "user-a");
        var page = result.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu);
        Assert.AreEqual(1, provider.Writes, "Only the native mode may be written on a followed transition.");
        Assert.AreEqual(ApplyState.Partial, page.State, JsonSerializer.Serialize(new { result.State, result.Mutation,
            page.SkippedFields, page.UnverifiedFields, page.ErrorCode, provider.Writes }));
        CollectionAssert.Contains(page.SkippedFields, "coreFrequencyLimitMhz");
        Assert.AreEqual(0, provider.GpuClockWrites);

        Guid explicitId = Guid.NewGuid();
        var explicitResult = await runtime.ExecuteAsync(new ApplyControlRequestCommand(explicitId, new(explicitId, long.MaxValue,
            ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
            [new PageSelection(ControlPageId.Gpu, true, key, 1)])),
            CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(0, provider.GpuClockWrites, "An explicit selection cannot make a write-only GPU cap recoverable.");
        CollectionAssert.Contains(explicitResult.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu).SkippedFields,
            "coreFrequencyLimitMhz");
    }

    [TestMethod]
    public async Task Unavailable_gpu_cap_in_saved_preset_does_not_block_native_mode()
    {
        var provider = new PipelineProvider { ConfirmModes = true, AllowGpuClock = false };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Turbo, 1);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "Old GPU cap",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = 2280, resetCoreFrequencyLimit = false,
                memoryOffsetKhz = (int?)null, coreOffsetKhz = (int?)null, vfOffsetsKhz = (int[]?)null }),
            DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Set(2280) } };
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(
            new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), CancellationToken.None, userSid: "user-a")).State);
        Guid operation = Guid.NewGuid();
        var applied = await runtime.ExecuteAsync(new ApplyControlRequestCommand(operation, new(operation,
            long.MaxValue, ControlOrigin.PresetUse, ControlModeId.Turbo, PerformanceMode.Turbo,
            [new(ControlPageId.Gpu, true, key, 1)])), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, applied.State, applied.Error?.Details.GetValueOrDefault("cause"));
        Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
        Assert.AreEqual(0, provider.GpuClockWrites);
        var page = applied.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu);
        Assert.AreEqual(ApplyState.Partial, page.State);
        CollectionAssert.Contains(page.SkippedFields, "coreFrequencyLimitMhz");
    }

    [TestMethod]
    public async Task Official_after_service_restart_reports_lost_ownership_instead_of_silent_success()
    {
        string root = Path.Combine(Path.GetTempPath(), "xisura-restart-ownership-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new PipelineProvider { ConfirmModes = true };
            var first = new HomeServiceRuntime(provider, journal: new ControlExecutionJournal(root));
            await first.InitializeAsync(CancellationToken.None);
            var key = PresetKey.Create(ControlModeId.Office, 1);
            PagePresetEnvelope Preset(SettingRequest setting) => new(PagePresetEnvelope.CurrentSchemaVersion,
                ControlPageId.Performance, key, "CPU", JsonSerializer.SerializeToElement(new
                { useOfficialCpuPolicy = false, temperatureLimitC = 95, splWatts = 45, spptWatts = 65,
                    acMaxFrequencyMhz = 3000, dcMaxFrequencyMhz = 4500, isBoostEnabled = true,
                    windowsPowerSchemeId = Guid.Empty, acMinActiveCoresPercent = (int?)null,
                    dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null,
                    advancedCpuTuning = (object?)null }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = setting } };
            async Task<CommandResult> Apply(HomeServiceRuntime runtime, SettingRequest setting)
            {
                Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(
                    new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [Preset(setting)]),
                    CancellationToken.None, userSid: "user-a")).State);
                Guid operation = Guid.NewGuid();
                return await runtime.ExecuteAsync(new ApplyControlRequestCommand(operation, new(operation,
                    long.MaxValue, ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
                    [new(ControlPageId.Performance, true, key, 1)])), CancellationToken.None, userSid: "user-a");
            }
            Assert.AreEqual(CommandState.Applied, (await Apply(first, SettingRequest.Set(3000))).State);
            Assert.AreEqual(3000, provider.Ac);
            var second = new HomeServiceRuntime(provider, journal: new ControlExecutionJournal(root));
            await second.InitializeAsync(CancellationToken.None);
            var restored = await Apply(second, SettingRequest.Official);
            Assert.AreEqual(3000, provider.Ac, "A new process cannot assume ownership from an old hardware epoch.");
            var page = restored.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Performance);
            Assert.AreEqual(ApplyState.Partial, page.State);
            CollectionAssert.Contains(page.SkippedFields, "acMaxFrequencyMhz");
            CollectionAssert.DoesNotContain(page.ConfirmedFields, "acMaxFrequencyMhz");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Latest_intent_is_serial_and_duplicate_operation_never_repeats_a_write()
    {
        var provider = new HeldProvider();
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var initial = runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), CancellationToken.None);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var latest = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet);
        var pending = Enumerable.Range(0, 19).Select(index => runtime.ExecuteAsync(
            index == 18 ? latest : new SetPerformanceModeCommand(Guid.NewGuid(), index % 2 == 0 ? PerformanceMode.Balanced : PerformanceMode.Turbo),
            CancellationToken.None)).ToArray();
        provider.Release.TrySetResult();
        await Task.WhenAll(pending.Append(initial)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, provider.MaximumConcurrent, "The same hardware cannot receive concurrent writes.");
        Assert.AreEqual(2, provider.Writes, "Only the in-flight write and the latest queued intent execute.");
        Assert.AreEqual(PerformanceMode.Quiet, provider.Mode);
        var original = await pending[^1];
        var repeated = await runtime.ExecuteAsync(latest, CancellationToken.None);
        Assert.AreEqual(original, repeated);
        Assert.AreEqual(2, provider.Writes);
        var key = PresetKey.Create(ControlModeId.Office, 2);
        var envelope = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance,
            key, "Official", JsonSerializer.SerializeToElement(new { useOfficialCpuPolicy = true, temperatureLimitC = 95,
                splWatts = 45, spptWatts = 65, acMaxFrequencyMhz = 4500, dcMaxFrequencyMhz = 4500,
                isBoostEnabled = true, windowsPowerSchemeId = Guid.Empty, acMinActiveCoresPercent = (int?)null,
                dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null, advancedCpuTuning = (object?)null }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = SettingRequest.Official } };
        var publication = new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [envelope]);
        Assert.IsTrue(PresetValidation.Validate(envelope).IsValid, string.Join(";", PresetValidation.Validate(envelope).Errors));
        var published = await runtime.ExecuteAsync(publication, CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, published.State, published.Error?.Code.ToString());
        Assert.AreEqual(1L, published.CatalogRevision);
        Assert.AreEqual(2, provider.Writes, "Catalog publication is a pure configuration operation.");
        Assert.AreEqual(0, runtime.Execution.Catalog.Resolve("user-b", []).Length);
        Assert.ThrowsExactly<InvalidDataException>(() => runtime.Execution.Catalog.Resolve("user-b", [new(ControlPageId.Performance, true, key, 1)]));
        var conflict = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [envelope]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Rejected, conflict.State);
        Assert.AreEqual(1L, conflict.CatalogRevision);
        Assert.AreEqual("CatalogPublish", conflict.Error!.Details.GetValueOrDefault("stage"));
        Assert.AreEqual("expectedCatalogRevisionMismatch", conflict.Error.Details.GetValueOrDefault("cause"));
        var advancedPublication = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 1, [envelope with { Revision = 2 }]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, advancedPublication.State);
        var olderPublication = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 2, [envelope]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual("presetRevisionRegressed", olderPublication.Error!.Details.GetValueOrDefault("cause"));
        Assert.AreEqual("2", olderPublication.Error.Details[$"presetRevision:{(int)envelope.Page}:{(int)key.Mode}:{key.Slot}"]);
        Assert.AreEqual(HardwareMutation.NoMutation, olderPublication.Mutation);
        Assert.AreEqual(2, provider.Writes, "Catalog conflicts must never execute hardware commands.");
        var recoveryCatalog = new ControlPresetCatalog();
        var otherPage = envelope with { Key = PresetKey.Create(ControlModeId.Office, 3), Revision = 7 };
        Assert.AreEqual(CommandState.Applied, recoveryCatalog.Publish(new(Guid.NewGuid(), 0, [envelope with { Revision = 5 }, otherPage]), "user-a").State);
        var floors = recoveryCatalog.Publish(new(Guid.NewGuid(), 1, [envelope, otherPage with { Revision = 1 }]), "user-a");
        Assert.AreEqual("5", floors.Error!.Details[$"presetRevision:{(int)envelope.Page}:{(int)key.Mode}:{key.Slot}"]);
        Assert.AreEqual("7", floors.Error.Details[$"presetRevision:{(int)otherPage.Page}:{(int)otherPage.Key.Mode}:{otherPage.Key.Slot}"]);
        Assert.AreEqual(1L, recoveryCatalog.Revision("user-a"));
        Assert.AreEqual(CommandState.Applied, recoveryCatalog.Publish(new(Guid.NewGuid(), 0, [envelope]), "user-b").State, "Other user revisions remain isolated.");
        Assert.AreEqual(CommandState.Rejected, recoveryCatalog.Publish(new(Guid.NewGuid(), 0, [envelope with { Revision = 6 }]), "user-a").State, "Rebasing never bypasses catalog CAS.");
        Assert.AreEqual(CommandState.Applied, recoveryCatalog.Publish(new(Guid.NewGuid(), 1, [envelope with { Revision = 6 }, otherPage with { Revision = 8 }]), "user-a").State);
        var future = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 1, [envelope with { SchemaVersion = 99 }]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Rejected, future.State);
        Assert.AreEqual(2, provider.Writes);
        var changedReplay = await runtime.ExecuteAsync(latest with { Mode = PerformanceMode.Turbo }, CancellationToken.None);
        Assert.AreEqual(CommandState.Rejected, changedReplay.State);
        Assert.AreEqual(HardwareMutation.NoMutation, changedReplay.Mutation);
        Assert.AreEqual(2, provider.Writes);
        var pipelineProvider = new PipelineProvider();
        var pipeline = new HomeServiceRuntime(pipelineProvider);
        await pipeline.InitializeAsync(CancellationToken.None);
        long revision = 0;
        PagePresetEnvelope Fixture(SettingRequest setting, int payloadFrequency = 3000) => envelope with
        { Settings = new() { ["acMaxFrequencyMhz"] = setting }, Payload = JsonSerializer.SerializeToElement(
            envelope.Payload.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Name == "acMaxFrequencyMhz" ? JsonSerializer.SerializeToElement(payloadFrequency) : property.Value.Clone())) };
        async Task<CommandResult> Apply(PagePresetEnvelope preset)
        {
            preset = preset with { Revision = ++revision };
            var publishedPreset = await pipeline.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), revision - 1, [preset]), CancellationToken.None, userSid: "user-a");
            Assert.AreEqual(CommandState.Applied, publishedPreset.State, publishedPreset.Error?.Code.ToString());
            Guid id = Guid.NewGuid();
            return await pipeline.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, long.MaxValue, ControlOrigin.PresetUse,
                ControlModeId.Office, PerformanceMode.Quiet, [new(ControlPageId.Performance, true, key, revision)])), CancellationToken.None, userSid: "user-a");
        }
        var tuned = await Apply(Fixture(SettingRequest.Set(3000)));
        Assert.AreEqual(CommandState.Applied, tuned.State);
        Assert.AreEqual(3000, pipelineProvider.Ac);
        Assert.AreEqual(2400, pipelineProvider.Dc, "An AC-only request preserves DC.");
        Assert.IsNull(tuned.VerifiedPerformanceMode, "Interface acknowledgement cannot become effective-mode evidence.");
        Assert.AreEqual(1L, tuned.ExecutionState!.Generation, "Generation is assigned by the service.");
        int writes = pipelineProvider.Writes;
        pipelineProvider.ConfirmModes = true;
        await Apply(Fixture(SettingRequest.Set(3000)));
        Assert.AreEqual(writes + 1, pipelineProvider.Writes, "An unconfirmed acknowledgement cannot suppress a fresh native-mode request.");
        writes = pipelineProvider.Writes;
        await Apply(Fixture(SettingRequest.Set(3000)));
        Assert.AreEqual(writes, pipelineProvider.Writes, "Fresh independent mode confirmation and unchanged fields avoid repeat writes.");
        await Apply(Fixture(SettingRequest.Set(2200), 2200));
        var restored = await Apply(Fixture(SettingRequest.Official));
        Assert.AreEqual(3200, pipelineProvider.Ac, "Repeated own writes preserve the first baseline.");
        Assert.AreEqual(ApplyState.Confirmed, restored.ExecutionState!.Pages[0].State);
        await Apply(Fixture(SettingRequest.Set(3000)));
        pipelineProvider.Ac = 4400;
        writes = pipelineProvider.Writes;
        var external = await Apply(Fixture(SettingRequest.Official));
        Assert.AreEqual(writes, pipelineProvider.Writes, "Official cannot restore over external changes.");
        Assert.AreEqual(4400, pipelineProvider.Ac);
        Assert.AreEqual(ApplyState.Partial, external.ExecutionState!.Pages[0].State);
        writes = pipelineProvider.Writes;
        Guid forgedId = Guid.NewGuid();
        var forged = await pipeline.ExecuteAsync(new ApplyControlRequestCommand(forgedId, new(forgedId, 0, ControlOrigin.Adaptive,
            ControlModeId.Office, PerformanceMode.Turbo, [])), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Rejected, forged.State);
        Assert.AreEqual(writes, pipelineProvider.Writes, "Client origins cannot acquire service policy ownership.");
        var wrongCapability = Fixture(SettingRequest.Set(3000));
        pipelineProvider.AllowFrequency = false;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var denied = await Apply(wrongCapability);
        Assert.AreEqual(CommandState.Rejected, denied.State);
        Assert.AreEqual("Preflight", denied.Error!.Details["stage"]);
        Assert.AreEqual("fieldNotWritable:acMaxFrequencyMhz", denied.Error.Details["cause"]);
        Assert.AreEqual(writes, pipelineProvider.Writes, "Required capability failures precede every hardware write.");
        pipelineProvider.AllowFrequency = true;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var optional = Fixture(SettingRequest.Official);
        var advanced = new AdvancedCpuTuningPlan(PboScalar: 2);
        var optionalPayload = optional.Payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(advanced);
        optional = optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(advanced) }, Payload = JsonSerializer.SerializeToElement(optionalPayload) };
        var partial = await Apply(optional);
        Assert.AreEqual(CommandState.Applied, partial.State, "Unsupported optional advanced settings do not prevent native mode.");
        Assert.AreEqual(ApplyState.Partial, partial.ExecutionState!.Pages[0].State);
        Assert.IsTrue(partial.ExecutionState.Pages[0].SkippedFields.Contains("pboScalar"));
        pipelineProvider.AllowPbo = true; pipelineProvider.AllowCurve = false;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var mixedAdvanced = new AdvancedCpuTuningPlan(FastPptWatts: 65, PboScalar: 2, CurveOptimizerAll: -15,
            PerCoreCurveOptimizer: new Dictionary<int, int> { [0] = -15 });
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(mixedAdvanced);
        var mixed = await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(mixedAdvanced) },
            Payload = JsonSerializer.SerializeToElement(optionalPayload) });
        Assert.AreEqual(2, pipelineProvider.Pbo, "An unavailable SMU/CO field cannot suppress independently verified PBO.");
        Assert.AreEqual(ApplyState.Partial, mixed.ExecutionState!.Pages[0].State);
        CollectionAssert.AreEquivalent(new[] { "advancedCpuTuning", "negativeCurveOptimizer" }, mixed.ExecutionState.Pages[0].SkippedFields);
        int writesBeforeConflict = pipelineProvider.Writes;
        var conflicting = mixedAdvanced with { PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -14 } };
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(conflicting);
        var conflictResult = await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(conflicting) },
            Payload = JsonSerializer.SerializeToElement(optionalPayload) });
        Assert.AreEqual(CommandState.Rejected, conflictResult.State);
        Assert.AreEqual("conflictingAdvancedField:negativeCurveOptimizer", conflictResult.Error!.Details["cause"]);
        Assert.AreEqual(writesBeforeConflict, pipelineProvider.Writes, "Genuinely different targets remain a zero-write rejection.");
        await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Official } });
        Assert.AreEqual(1, pipelineProvider.Pbo, "Official restores the owned scalar independently of unsupported SMU fields.");
        pipelineProvider.AllowCurve = true;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var perCorePlan = new AdvancedCpuTuningPlan(PerCoreCurveOptimizer: new Dictionary<int, int> { [0] = -15 });
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(perCorePlan);
        await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(perCorePlan) },
            Payload = JsonSerializer.SerializeToElement(optionalPayload) });
        Assert.AreEqual(-15, pipelineProvider.Cores[0]); Assert.AreEqual(5, pipelineProvider.Cores[1], "Untouched per-core baseline is preserved.");
        await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Official } });
        Assert.AreEqual(-10, pipelineProvider.Cores[0]); Assert.AreEqual(5, pipelineProvider.Cores[1]);
        await Apply(Fixture(SettingRequest.Set(3000)));
        pipeline.Execution.InvalidateHardware();
        await pipeline.ReinitializeAsync(CancellationToken.None);
        await Apply(Fixture(SettingRequest.Official));
        Assert.AreEqual(3000, pipelineProvider.Ac, "A prior hardware epoch cannot restore a baseline.");
        Assert.IsNull(CommandValidation.Validate(new SetCpuTuningBatchCommand(Guid.NewGuid(), [new(null, null, null, null, null, null, null, -30)], true)));
        int uncertainWrites = 0;
        var uncertain = new ControlExecutionCoordinator((request, token, connection) =>
        { uncertainWrites++; return Task.FromResult(new CommandResult(request.OperationId, CommandState.RecoveryRequired, null,
              RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Unknown }); },
            (_, _) => Task.CompletedTask, () => Task.CompletedTask);
        var unresolved = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo);
        var unresolvedResult = await uncertain.ExecuteAsync(unresolved, CancellationToken.None);
        Assert.AreEqual(unresolvedResult, await uncertain.ExecuteAsync(unresolved, CancellationToken.None));
        Assert.AreEqual(1, uncertainWrites, "Unknown operations remain queryable and do not retry hardware.");
        FanPoint[] curvePoints = [new(30, 30), new(60, 60), new(90, 100)];
        var fanBaseline = new HomeControlState(PerformanceMode.Balanced, false, [])
        { ActiveFanControlPlan = new(curvePoints) { Strategy = "Curve" } };
        var fanCommands = ControlPlanCompiler.Commands(ControlPageId.Fan,
            new() { ["maximumRpm"] = SettingRequest.Set(5500) }, fanBaseline);
        var preservedFan = (SetFanControlCommand)fanCommands.Single();
        CollectionAssert.AreEqual(curvePoints, preservedFan.Plan.Points);
        Assert.IsNull(preservedFan.Plan.GpuPoints, "A shared curve remains shared when only its RPM limit changes.");
        Assert.IsNull(CommandValidation.Validate(preservedFan));
        var sharedCommands = ControlPlanCompiler.Commands(ControlPageId.Fan, new()
        { ["strategy"] = SettingRequest.Set("Curve"), ["isShared"] = SettingRequest.Set(true),
          ["shared"] = SettingRequest.Set(curvePoints.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray()) }, fanBaseline);
        Assert.IsNull(CommandValidation.Validate(sharedCommands.Single()));

        var boostFixture = Fixture(SettingRequest.Official) with { Settings = new() { ["isBoostEnabled"] = SettingRequest.Set(false) } };
        var boostPayload = boostFixture.Payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        boostPayload["isBoostEnabled"] = JsonSerializer.SerializeToElement(false);
        await Apply(boostFixture with { Payload = JsonSerializer.SerializeToElement(boostPayload) });
        Assert.AreEqual(0, pipelineProvider.AcBoost);
        await Apply(boostFixture with { Settings = new() { ["isBoostEnabled"] = SettingRequest.Official } });
        Assert.AreEqual(4, pipelineProvider.AcBoost); Assert.AreEqual(1, pipelineProvider.DcBoost);
        var coFixture = Fixture(SettingRequest.Official) with { Settings = new() { ["negativeCurveOptimizer"] = SettingRequest.Set(-25) } };
        var coPayload = coFixture.Payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        coPayload["negativeCurveOptimizer"] = JsonSerializer.SerializeToElement(-25);
        await Apply(coFixture with { Payload = JsonSerializer.SerializeToElement(coPayload) });
        await Apply(coFixture with { Settings = new() { ["negativeCurveOptimizer"] = SettingRequest.Official } });
        Assert.AreEqual(-10, pipelineProvider.Cores[0]); Assert.AreEqual(5, pipelineProvider.Cores[1], "Exact BIOS CO baseline may include positive offsets.");

        var branchProvider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
        var branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        await branchRuntime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: "branch-a");
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        var branchResult = await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, branchResult.State, branchResult.Error?.Code.ToString());
        Assert.AreEqual(4500, branchProvider.Ac); Assert.AreEqual(3300, branchProvider.Dc);
        Assert.IsTrue(branchResult.ExecutionState!.Pages.Single(page => page.Page == ControlPageId.Performance).SkippedFields.Contains("splWatts"));
        Assert.AreEqual(0L, branchRuntime.Execution.Catalog.Revision("branch-a"), "Transient branches never publish or change the stored catalog.");
        int branchWrites = branchProvider.Writes;
        Assert.AreEqual(branchResult, await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a"));
        Assert.AreEqual(branchWrites, branchProvider.Writes);
        Assert.AreEqual(CommandState.Rejected, (await branchRuntime.ExecuteAsync(branch with { Tier = TurboBranchTier.Extreme }, CancellationToken.None, userSid: "branch-a")).State);
        var alienRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-b");
        Assert.AreEqual(CommandState.Rejected, alienRelease.State); Assert.AreEqual(branchWrites, branchProvider.Writes);
        // Unauthorized release admission must not erase the original generation evidence.
        var released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, released.State, released.Error?.Code.ToString());
        Assert.AreEqual(3200, branchProvider.Ac); Assert.AreEqual(2400, branchProvider.Dc);
        Assert.AreEqual(PerformanceMode.Balanced, branchProvider.Mode);
        Assert.AreEqual(4200, branchProvider.FanPlan!.FixedRpm, "Confirmed fan baseline is restored on the service gate.");
        var repeatedRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, repeatedRelease.State, "A successfully released branch remains idempotent within this service process.");
        var alienRepeatedRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-b");
        Assert.AreEqual(CommandState.Rejected, alienRepeatedRelease.State);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, alienRepeatedRelease.Error?.Code);
        var unknownRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.RecoveryRequired, unknownRelease.State);
        Assert.AreEqual(ErrorCode.ConflictDetected, unknownRelease.Error?.Code);
        Assert.AreEqual(HardwareMutation.NoMutation, unknownRelease.Mutation);
        await branchRuntime.ExecuteAsync(new SetCpuTuningBatchCommand(Guid.NewGuid(),
            [new(null, null, null, null, null, null, null, null) { AcMaxFrequencyMhz = 3000 }], true), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(3000, branchProvider.Ac);
        branch = new(Guid.NewGuid(), TurboBranchTier.Quiet);
        await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        var restoredBranch = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(3000, branchProvider.Ac, "A temporary branch restores the actual pre-branch override, not its older OEM baseline.");
        Assert.IsTrue(restoredBranch.ExecutionState!.Fields.Values.Any(field => field.Origin == ControlOrigin.ManualLive),
            "Prior still-matching ownership survives a temporary branch.");

        branch = new(Guid.NewGuid(), TurboBranchTier.Quiet);
        await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        await branchRuntime.ExecuteAsync(new SetCpuTuningBatchCommand(Guid.NewGuid(), [new(null, null, null, null, null, null, null, null)
            { AcMaxFrequencyMhz = 4400 }], true), CancellationToken.None, userSid: "branch-a");
        branchProvider.FanPlan = new FanControlPlan([]) { Strategy = "Fixed", FixedRpm = 4700 };
        released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(4400, branchProvider.Ac, "Later user CPU edits survive branch exit.");
        Assert.AreEqual(2400, branchProvider.Dc, "Unchanged branch-owned CPU fields restore independently.");
        Assert.AreEqual(4700, branchProvider.FanPlan!.FixedRpm, "Later fan edits survive branch exit.");
        Assert.AreEqual(PerformanceMode.Turbo, branchProvider.Mode, "An intervening generation retires native-mode restoration.");

        foreach (bool changeEpoch in new[] { false, true })
        {
            branchProvider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
            branchRuntime = new HomeServiceRuntime(branchProvider);
            await branchRuntime.InitializeAsync(CancellationToken.None);
            branch = new(Guid.NewGuid(), TurboBranchTier.Quiet);
            await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
            if (changeEpoch) branchRuntime.Execution.InvalidateHardware(); else branchProvider.Scheme = Guid.NewGuid();
            branchWrites = branchProvider.Writes;
            released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
            Assert.AreEqual(branchWrites, branchProvider.Writes, "Epoch/scheme changes prohibit stale branch restoration.");
            Assert.IsTrue(released.ExecutionState!.Pages.Any(page => page.State == ApplyState.Partial));
        }
        branchProvider = new PipelineProvider { AllowBranch = true, ConfirmModes = false, ReadFan = false };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branch = new(Guid.NewGuid(), TurboBranchTier.Extreme);
        branchResult = await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(ApplyState.SubmittedUnverified, branchResult.ExecutionState!.NativeModeApplyState);
        Assert.AreEqual(ApplyState.SubmittedUnverified, branchResult.ExecutionState.Fields["fan.strongCooling"].WriteState);
        released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.IsTrue(branchProvider.StrongCooling, "Submission-only fan evidence never authorizes blind restoration.");
        Assert.AreEqual(PerformanceMode.Turbo, branchProvider.Mode, "WMI acknowledgements cannot restore an unknown native baseline.");
        branchProvider = new PipelineProvider { AllowBranch = true, AllowBoost = false };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branchResult = await branchRuntime.ExecuteAsync(new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Rejected, branchResult.State); Assert.AreEqual(0, branchProvider.Writes, "Required dependencies fail before the first mode write.");
        branchProvider = new PipelineProvider { AllowBranch = true, CancelBaselineOnce = true };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branchResult = await branchRuntime.ExecuteAsync(new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Rejected, branchResult.State);
        Assert.AreEqual(HardwareMutation.NoMutation, branchResult.Mutation, "Cancellation before provider dispatch cannot become Unknown.");
        Assert.AreEqual(0, branchProvider.Writes);
        branchProvider = new PipelineProvider { AllowBranch = true, CpuModel = "AMD Ryzen 9 7940HX" };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branchResult = await branchRuntime.ExecuteAsync(new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Extreme), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, branchResult.State);
        Assert.AreEqual(3200, branchProvider.Ac); Assert.AreEqual(2400, branchProvider.Dc, "Unknown CPU profiles retain official Windows frequency policy.");

        string journalRoot = Path.Combine(Path.GetTempPath(), "xisura-control-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ControlExecutionJournal(journalRoot);
            var uncertainState = tuned.ExecutionState! with { NativeModeApplyState = ApplyState.Unknown };
            var identity = new HardwareIdentity("fake", "fake", "fake", "fake");
            await journal.SaveAsync(uncertainState, identity, tuned.OperationId, true, CancellationToken.None, "user-a");
            var afterCrash = await new ControlExecutionJournal(journalRoot).ReadAsync(CancellationToken.None);
            Assert.AreEqual(1, afterCrash.Unresolved.Length);
            Assert.IsNull(afterCrash.LastConfirmedMode);
            Assert.AreEqual("user-a", afterCrash.Unresolved[0].OwnerSid);
            var zeroWriteId = Guid.NewGuid();
            await journal.SaveAsync(uncertainState, identity, zeroWriteId, true, CancellationToken.None, "user-a");
            await journal.ResolveNoMutationAsync(zeroWriteId, null, CancellationToken.None);
            var clearedZeroWrite = await journal.ReadAsync(CancellationToken.None);
            Assert.IsNull(clearedZeroWrite.Current);
            Assert.AreEqual(tuned.OperationId, clearedZeroWrite.Unresolved.Single().OperationId,
                "A zero-write operation with no prior state clears only its own marker and retains older uncertainty.");
            var restartProvider = new PipelineProvider();
            var restarted = new HomeServiceRuntime(restartProvider, journal: new ControlExecutionJournal(journalRoot));
            await restarted.InitializeAsync(CancellationToken.None);
            var replay = await restarted.ExecuteAsync(new SetCpuTuningCommand(tuned.OperationId,
                new(null, null, null, 3000, null, null, null, null), false), CancellationToken.None, userSid: "user-a");
            Assert.AreEqual(CommandState.RecoveryRequired, replay.State);
            Assert.AreEqual(HardwareMutation.NoMutation, replay.Mutation);
            Assert.AreEqual(0, restartProvider.Writes, "A durable uncertain operation must not be replayed after restart.");
            await journal.SaveAsync(tuned.ExecutionState!, identity, tuned.OperationId, false, CancellationToken.None);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
            string lifecycleRoot = Path.Combine(journalRoot, "lifecycle");
            restartProvider = new PipelineProvider { StrongCooling = true, UncertainFanRelease = true };
            restarted = new HomeServiceRuntime(restartProvider, journal: new ControlExecutionJournal(lifecycleRoot));
            await restarted.InitializeAsync(CancellationToken.None);
            await restarted.HandleLifecycleAsync(false, CancellationToken.None);
            Assert.AreEqual(1, restartProvider.Writes);
            var lifecycleJournal = await new ControlExecutionJournal(lifecycleRoot).ReadAsync(CancellationToken.None);
            Assert.AreEqual(nameof(ReleaseFanControlCommand), lifecycleJournal.Unresolved.Single().CommandKind);
            restarted = new HomeServiceRuntime(restartProvider, journal: new ControlExecutionJournal(lifecycleRoot));
            await restarted.InitializeAsync(CancellationToken.None);
            await restarted.HandleLifecycleAsync(true, CancellationToken.None);
            Assert.AreEqual(1, restartProvider.Writes, "Resume/restart must not repeat an uncertain EC handoff.");
            Assert.IsFalse(restarted.IsSuspended);
            pipelineProvider = new PipelineProvider();
            pipeline = new HomeServiceRuntime(pipelineProvider, journal: journal);
            revision = 0;
            await pipeline.InitializeAsync(CancellationToken.None);
            await Apply(Fixture(SettingRequest.Set(3000)));
            string journalPath = Path.Combine(journalRoot, "control-execution.json");
            using (var blocked = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                int beforeWrites = pipelineProvider.Writes;
                var rejectedWrite = await pipeline.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(),
                    new(null, null, null, 2000, null, null, null, null), false), CancellationToken.None);
                Assert.AreEqual(HardwareMutation.NoMutation, rejectedWrite.Mutation);
                Assert.AreEqual(beforeWrites, pipelineProvider.Writes);
            }
            await Apply(Fixture(SettingRequest.Set(2500), 2500));
            string diagnosticSummary = JsonSerializer.Serialize(await pipeline.ReadControlDiagnosticsAsync(CancellationToken.None, "user-b"));
            Assert.IsFalse(diagnosticSummary.Contains("Baseline", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(diagnosticSummary.Contains("user-a", StringComparison.OrdinalIgnoreCase));
            await Apply(Fixture(SettingRequest.Official));
            Assert.AreEqual(3200, pipelineProvider.Ac, "Failed pre-write journaling must retain the original 3200 baseline.");
        }
        finally { if (Directory.Exists(journalRoot)) Directory.Delete(journalRoot, true); }
    }

    private sealed class HeldProvider : IHomeHardwareProvider
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes;
        public int MaximumConcurrent;
        private int active;
        public PerformanceMode Mode = PerformanceMode.Balanced;
        private HomeHardwareState Snapshot => new(new([new("performanceMode", CapabilityState.Available, null)]),
            DeviceSupportState.Ready, null, new("fake", "fake", "fake", "fake"), null)
        { Controls = new(Mode, false, []) };
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(Snapshot.Controls);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", 40, 40, 10, 10));
        public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            int concurrency = Interlocked.Increment(ref active);
            MaximumConcurrent = Math.Max(MaximumConcurrent, concurrency);
            int sequence = Interlocked.Increment(ref Writes);
            try
            {
                if (sequence == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
                Mode = ((SetPerformanceModeCommand)command).Mode;
                return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed, VerifiedPerformanceMode = Mode };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private sealed class PipelineProvider : IHomeHardwareProvider
    {
        public int Ac = 3200, Dc = 2400, Writes, GpuClockWrites;
        public int BaselineReads;
        public Action? AfterBaseline;
        public Action<HardwareCommand>? AfterWrite;
        public bool RejectModeWithoutMutation;
        public bool AllowFrequency = true;
        public bool AllowGpuClock = true;
        public bool AllowBranch, AllowBoost = true, ReadFan = true;
        public bool CancelBaselineOnce;
        public bool UncertainFanRelease;
        public string CpuModel = "AMD Ryzen 9 7745HX";
        public bool StrongCooling;
        public FanControlPlan? FanPlan = new([new(40, 60), new(95, 100)]) { Strategy = "Fixed", FixedRpm = 4200 };
        public bool AllowPbo, AllowCurve = true;
        public int Pbo = 1;
        public bool ConfirmModes;
        public int AcBoost = 4, DcBoost = 1;
        public Dictionary<int, int> Cores = new() { [0] = -10, [1] = 5 };
        public PerformanceMode Mode = PerformanceMode.Balanced;
        public Guid Scheme = Guid.NewGuid();
        private HomeControlState Controls => new(Mode, StrongCooling, [])
        { ActiveFanControlPlan = FanPlan, CpuTuning = new(null, null, null, Ac, AcBoost != 0 && DcBoost != 0, Cores.Count, Scheme,
            Cores.Values.Distinct().Count() == 1 ? Cores.Values.First() : null, "hardwareReadback")
          { AcMaxFrequencyMhz = Ac, DcMaxFrequencyMhz = Dc, AcBoostMode = AcBoost, DcBoostMode = DcBoost,
            PerCoreCurveOptimizer = new Dictionary<int, int>(Cores), PboScalar = Pbo },
          GpuClockLimit = new(210, 3105, null, false, null) };
        private HomeHardwareState Snapshot => new(new([new("performanceMode", CapabilityState.Available, null), new("cpuTuning", CapabilityState.Available, null), new("gpuFrequencyLimit", CapabilityState.Available, null), new("fanControl", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, null), new("strongCooling", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, null)])
        { Fields = [new("dcMaxFrequencyMhz", AllowFrequency ? CapabilityState.Available : CapabilityState.ReadOnly, true, AllowFrequency,
            0, 6000, 1, "MHz", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("strategy", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("strongCooling", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("cpu", CapabilityState.Available, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("shared", CapabilityState.Available, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("fixedRpm", CapabilityState.Available, ReadFan, AllowBranch, 1500, 6000, 1, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("acMaxFrequencyMhz", AllowFrequency ? CapabilityState.Available : CapabilityState.ReadOnly, true, AllowFrequency,
            0, 6000, 1, "MHz", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("isBoostEnabled", AllowBoost ? CapabilityState.Available : CapabilityState.Unavailable, true, AllowBoost, null, null, null, "", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("negativeCurveOptimizer", AllowCurve ? CapabilityState.Available : CapabilityState.Unavailable, AllowCurve, AllowCurve, -30, 0, 1, "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("pboScalar", AllowPbo ? CapabilityState.Available : CapabilityState.Unavailable, AllowPbo, AllowPbo, 1, 10, 1, "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("coreFrequencyLimitMhz", AllowGpuClock ? CapabilityState.Available : CapabilityState.Unavailable, false, AllowGpuClock,
                210, 3105, 1, "MHz", "driverSubmission", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)] },
            DeviceSupportState.Ready, null, new("fake", "fake", AllowBranch ? CpuModel : "fake", "fake"), null) { Controls = Controls };
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(Controls);
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false)
        {
            BaselineReads++;
            if (CancelBaselineOnce) { CancelBaselineOnce = false; throw new OperationCanceledException(); }
            AfterBaseline?.Invoke();
            return ReadControlsAsync(token);
        }
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", 40, 40, 10, 10));
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            if (RejectModeWithoutMutation && command is SetPerformanceModeCommand)
            {
                AfterWrite?.Invoke(command);
                return Task.FromResult(new CommandResult(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                    Jiaolong.Contracts.Errors.ServiceError.Create(Jiaolong.Contracts.Errors.ErrorCode.HardwareWriteFailed, command.OperationId, false), false)
                    { Mutation = HardwareMutation.NoMutation });
            }
            Writes++;
            if (command is ReleaseFanControlCommand && UncertainFanRelease)
                return Task.FromResult(new CommandResult(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                    Jiaolong.Contracts.Errors.ServiceError.Create(Jiaolong.Contracts.Errors.ErrorCode.HardwareWriteFailed, command.OperationId, false), false)
                    { Mutation = HardwareMutation.Unknown });
            switch (command)
            { case SetPerformanceModeCommand request: Mode = request.Mode; break;
              case SetGpuFrequencyLimitCommand: GpuClockWrites++; break;
              case SetFanControlCommand fan: FanPlan = fan.Plan; StrongCooling = false; break;
              case ReleaseFanControlCommand: FanPlan = null; StrongCooling = false; break;
              case SetStrongCoolingCommand cooling: StrongCooling = cooling.Enabled; break;
              case SetCpuTuningBatchCommand batch: foreach (var plan in batch.Plans)
              { Ac = plan.AcMaxFrequencyMhz ?? Ac; Dc = plan.DcMaxFrequencyMhz ?? Dc;
                if (plan.BoostEnabled is bool boost) { AcBoost = boost ? AcBoost == 0 ? 2 : AcBoost : 0; DcBoost = boost ? DcBoost == 0 ? 2 : DcBoost : 0; }
                if (plan.NegativeCurveOptimizer is int offset) Cores = Cores.Keys.ToDictionary(core => core, _ => offset);
                if (plan.Advanced?.PboScalar is int scalar) Pbo = scalar;
                if (plan.Advanced?.PerCoreCurveOptimizer is { } perCore)
                    foreach (var pair in perCore) { Assert.IsTrue(pair.Value <= 0); Cores[pair.Key] = pair.Value; } } break; }
            AfterWrite?.Invoke(command);
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { Mutation = HardwareMutation.Changed, VerifiedPerformanceMode = command is SetPerformanceModeCommand && ConfirmModes ? Mode : null });
        }
        public Task<CommandResult> RestoreCpuFieldAsync(Guid id, string field, CpuFieldRestorationValue baseline, CpuFieldRestorationValue expected, CancellationToken token)
        {
            Assert.IsTrue(HomeServiceRuntime.SameRestoration(HomeServiceRuntime.CaptureCpuRestoration(field, Controls.CpuTuning), expected));
            if (field == "isBoostEnabled") { AcBoost = baseline.AcBoostMode!.Value; DcBoost = baseline.DcBoostMode!.Value; }
            else Cores = new(baseline.PerCoreCurveOptimizer!);
            Writes++;
            return Task.FromResult(new CommandResult(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed });
        }
    }
}
