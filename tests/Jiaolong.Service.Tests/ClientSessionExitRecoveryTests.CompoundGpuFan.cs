using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(Guid Parent, ClientControlSessionHandle Handle)> SeedCompoundGpuFan(Fixture fixture, string kind)
    {
        fixture.Provider.FirmwareStrongCooling = kind is "fan" or "lightingWithFan";
        var handle = await fixture.Begin(null);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var cpu = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key, "cpu",
            JsonSerializer.SerializeToElement(new { temperatureLimitC = 85, splWatts = 55, spptWatts = 65,
                acMaxFrequencyMhz = 4600, dcMaxFrequencyMhz = 4500, isBoostEnabled = true, windowsPowerSchemeId = Fixture.Scheme,
                acMinActiveCoresPercent = (int?)null, dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = -5,
                advancedCpuTuning = (AdvancedCpuTuningPlan?)null }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = SettingRequest.Set(4600),
            ["dcMaxFrequencyMhz"] = SettingRequest.Set(4500), ["negativeCurveOptimizer"] = SettingRequest.Set(-5) } };
        var gpu = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "gpu",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = (int?)null, resetCoreFrequencyLimit = false,
                coreOffsetKhz = 10000, memoryOffsetKhz = 1000,
                vfOffsetsKhz = Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 75000).ToArray() }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["coreOffsetKhz"] = SettingRequest.Set(10000), ["memoryOffsetKhz"] = SettingRequest.Set(1000),
            ["vfOffsetsKhz"] = SettingRequest.Set(Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 75000).ToArray()) } };
        var light = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Lighting, key, "light",
            JsonSerializer.SerializeToElement(new { effect = "Static", brightness = 3, hex = "#112233", logo = true, speed = 1 }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["hex"] = SettingRequest.Set("#112233") } };
        var fan = FanPreset(key, false);
        foreach (var preset in new[] { cpu, gpu, fan, light })
            Assert.IsTrue(PresetValidation.Validate(preset).IsValid, string.Join(";", PresetValidation.Validate(preset).Errors));
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [cpu, gpu, fan, light]), handle)).State);
        fixture.Provider.JournalEveryGpuStep = fixture.Provider.KeepKnownGpuIntermediate = true;
        fixture.Provider.RecoverOnRequest = true;
        fixture.Provider.JournalEveryModeStep = true;
        fixture.Provider.BeforeWrite = async command =>
        {
            if (GpuCommandKind(command) == kind) fixture.Provider.FailNextGpuWrite = true;
            if (kind is not ("fan" or "lightingWithFan") || command is not SetFanControlCommand) return;
            var identity = (await fixture.Journal.ReadAsync(CancellationToken.None)).Identity!;
            var scope = new FanEcRecoveryScope(fixture.Runtime.SessionId, handle.HardwareEpoch, handle.SessionId, fixture.Owner.UserSid, identity);
            await fixture.Journal.RecordFanEcCheckpointAsync(scope, new(new()
                { [0x1060] = 0, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 }, new()
                { [0x1060] = 0, [0x0B20] = 0x0A, [0xC83C] = 40, [0xC83D] = 40 }, new(), false), CancellationToken.None);
            await fixture.Journal.RecordFanFirmwareAsync(scope, 1, 0, null, false, CancellationToken.None);
            fixture.Provider.FailNextFanWrite = kind == "fan";
        };
        var id = Guid.NewGuid();
        var pages = new List<PageSelection> { new(ControlPageId.Performance, true, key, 1), new(ControlPageId.Gpu, true, key, 1) };
        if (kind is "fan" or "lightingWithFan") pages.Add(new(ControlPageId.Fan, true, key, 1));
        pages.Add(new(ControlPageId.Lighting, true, key, 1));
        var result = await fixture.Write(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
            ControlModeId.Gaming, kind == "fan" ? PerformanceMode.Turbo : PerformanceMode.Balanced, pages.ToArray())), handle);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State, result.Error?.Details.GetValueOrDefault("cause"));
        fixture.Provider.BeforeWrite = null;
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        if (!kind.StartsWith("lighting"))
            Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light, "Do not execute later lighting after the failed child.");
        Assert.AreEqual(id, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        return (id, handle);
    }

    [TestMethod]
    public async Task Compound_gpu_fan_gpu_children_restore_prior_cpu_and_verified_mode_without_replaying_completed_children()
    {
        foreach (string kind in new[] { "core", "memory", "vf" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = fixture.Provider.ReadScheme(Fixture.Scheme);
            var prior = await SeedCompoundGpuFan(fixture, kind);
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, kind + " " + JsonSerializer.Serialize(result.Error));
            Assert.AreEqual(prior.Parent, fixture.Provider.RecoveredOperationId);
            Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
            Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
            Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
            Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
            Assert.IsTrue(fixture.Provider.Offsets().All(value => value == 0));
            Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        }
    }

    [TestMethod]
    public async Task Compound_gpu_fan_gpu_recovery_lost_confirmation_keeps_original_parent_and_retries()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var prior = await SeedCompoundGpuFan(fixture, "vf");
        fixture.Provider.FailGpuReconcileAfterRecoveryOnce = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(prior.Parent, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        int recoveryWrites = fixture.Provider.GpuRestorationWrites;
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(recoveryWrites, fixture.Provider.GpuRestorationWrites, "A lost acknowledgement must not repeat native recovery writes.");
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
    }

    [TestMethod]
    public async Task Compound_gpu_fan_fan_child_restores_completed_cpu_gpu_in_same_and_new_service()
    {
        foreach (bool restart in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = fixture.Provider.ReadScheme(Fixture.Scheme);
            var prior = await SeedCompoundGpuFan(fixture, "fan");
            var journal = restart ? new ControlExecutionJournal(fixture.Root) : fixture.Journal;
            var runtime = restart ? new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower) : fixture.Runtime;
            fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
            fixture.Provider.FanRecovery = (id, scope, token) => CompleteRuntimeFan(fixture, journal, id, scope, token);
            var result = restart ? await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None) : await fixture.End(prior.Handle);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
            Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
            Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
            Assert.IsTrue(fixture.Provider.Offsets().All(value => value == 0));
            Assert.IsTrue(fixture.Provider.FirmwareStrongCooling);
            Assert.IsTrue(fixture.Provider.Controls.FanEcControl!.CpuTarget == 0);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Compound_gpu_fan_fan_failed_recovery_preserves_original_parent_then_retries()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var prior = await SeedCompoundGpuFan(fixture, "fan");
        var runtime = RestartForPartialCpuEnd(fixture);
        fixture.Provider.FanRecovery = (id, _, _) => Task.FromResult(new CommandResult(id, CommandState.RecoveryRequired,
            null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.NoMutation });
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(prior.Parent, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        var journal = new ControlExecutionJournal(fixture.Root);
        fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
        fixture.Provider.FanRecovery = (id, scope, token) => CompleteRuntimeFan(fixture, journal, id, scope, token);
        var retry = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        Assert.AreEqual(CommandState.Applied, (await retry.RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
    }

    [TestMethod]
    public async Task Compound_gpu_fan_corrupt_child_or_completed_gpu_checkpoint_is_zero_write_and_keeps_parent()
    {
        foreach (string fault in new[] { "gpuFields", "gpuTarget", "fanFields", "completedGpu" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var prior = await SeedCompoundGpuFan(fixture, fault.StartsWith("gpu") ? "memory" : "fan");
            string path = Path.Combine(fixture.Root, "control-execution.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var state in new[] { node["current"]!, node["unresolved"]![0]!["state"]! })
            {
                if (fault.EndsWith("Fields")) state["pendingStep"]!["fields"] = new JsonArray("performance.acMaxFrequencyMhz");
                if (fault == "gpuTarget") state["pendingStep"]!["command"]!["offsetKhz"] = 2000;
            }
            if (fault == "completedGpu") node["unresolved"]![0]!["gpuAttempt"]!["vfOffsetsKhz"]![1] = 76000;
            File.WriteAllText(path, node.ToJsonString());
            Assert.AreEqual(prior.Parent, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId,
                "The fault must be well-formed journal data, not a deserialization failure.");
            int writes = fixture.Provider.WriteCount;
            int fanCalls = 0;
            fixture.Provider.FanRecovery = (id, _, _) =>
            {
                fanCalls++;
                return Task.FromResult(new CommandResult(id, CommandState.Rejected, null, RequiredUserAction.None, null, false)
                    { Mutation = HardwareMutation.NoMutation });
            };
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(0, fixture.Provider.GpuRecoveries);
            Assert.AreEqual(0, fanCalls, "Reject inconsistent evidence before dispatching native fan recovery.");
            Assert.AreEqual(prior.Parent, JsonNode.Parse(File.ReadAllText(path))!["unresolved"]![0]!["operationId"]!.GetValue<Guid>());
        }
    }
}
