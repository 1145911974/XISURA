using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<Guid> SeedCompoundCpu(Fixture fixture, bool lightingFirst, bool changeMode = false)
    {
        var handle = await fixture.Begin(null);
        if (lightingFirst)
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(),
                fixture.Provider.Light with { Red = 17, Green = 34, Blue = 51 }), handle)).State);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var cpu = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key, "compound cpu",
            JsonSerializer.SerializeToElement(new { temperatureLimitC = 85, splWatts = 55, spptWatts = 65,
                acMaxFrequencyMhz = 4600, dcMaxFrequencyMhz = 4500,
                negativeCurveOptimizer = -5, isBoostEnabled = false, windowsPowerSchemeId = Fixture.Scheme,
                acMinActiveCoresPercent = (int?)null, dcMinActiveCoresPercent = (int?)null,
                advancedCpuTuning = (AdvancedCpuTuningPlan?)null }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = SettingRequest.Set(4600),
            ["dcMaxFrequencyMhz"] = SettingRequest.Set(4500), ["negativeCurveOptimizer"] = SettingRequest.Set(-5),
            ["isBoostEnabled"] = SettingRequest.Set(false) } };
        var light = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Lighting, key, "compound light",
            JsonSerializer.SerializeToElement(new { effect = "Static", brightness = 2, hex = "#112233", logo = true, speed = 1 }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["hex"] = SettingRequest.Set("#112233") } };
        Assert.IsTrue(PresetValidation.Validate(cpu).IsValid, string.Join(";", PresetValidation.Validate(cpu).Errors));
        Assert.IsTrue(PresetValidation.Validate(light).IsValid, string.Join(";", PresetValidation.Validate(light).Errors));
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [cpu, light]), handle)).State);
        fixture.Provider.InterruptCpuBatchAfterPlans = 1;
        var id = Guid.NewGuid();
        PageSelection[] pages = lightingFirst ? [new(ControlPageId.Lighting, true, key, 1), new(ControlPageId.Performance, true, key, 1)]
            : [new(ControlPageId.Performance, true, key, 1), new(ControlPageId.Lighting, true, key, 1)];
        var result = await fixture.Write(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
            ControlModeId.Gaming, changeMode ? PerformanceMode.Balanced : PerformanceMode.Turbo, pages)), handle);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State, result.Error?.Details.GetValueOrDefault("cause"));
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        return id;
    }

    [TestMethod]
    public async Task Compound_cpu_pending_preserves_previous_lighting_and_does_not_run_later_pages_or_replay_preset()
    {
        foreach (bool lightingFirst in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = fixture.Provider.ReadScheme(Fixture.Scheme);
            var id = await SeedCompoundCpu(fixture, lightingFirst);
            if (lightingFirst) Assert.AreEqual((byte)17, fixture.Provider.Light.Red);
            else Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light, "Stop subsequent pages immediately after an unknown mutation.");
            fixture.Provider.BeforeWrite = command =>
            {
                Assert.IsFalse(command is ApplyControlRequestCommand);
                Assert.AreNotEqual(id, command.OperationId, "Never replay the original preset.");
                return Task.CompletedTask;
            };
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
            Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
            Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
            Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Compound_cpu_pending_same_service_exit_recovers_without_waiting_for_service_restart()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var first = fixture.Provider.ReadScheme(Fixture.Scheme);
        await SeedCompoundCpu(fixture, true);
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        var sessionId = saved.Current!.Fields["performance.negativeCurveOptimizer"].ClientSessionId!.Value;
        var result = await fixture.Runtime.EndClientControlSessionAsync(Guid.NewGuid(), sessionId, fixture.Owner, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    [TestMethod]
    public async Task Compound_cpu_pending_verified_mode_side_effect_has_exact_before_and_partial_restore_can_retry()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.JournalEveryModeStep = true;
        fixture.Provider.ModeWriteEffects = () => fixture.Provider.SetExternalCurve(-10);
        var id = await SeedCompoundCpu(fixture, true, true);
        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        Assert.IsTrue(saved.Current!.Fields["performance.negativeCurveOptimizer"].WriteBeforeDetails!.PerCoreCurveOptimizer!.Values.All(value => value == -10));
        fixture.Provider.ModeWriteEffects = null;
        fixture.Provider.FailDcBoostRestore = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(id, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        fixture.Provider.FailDcBoostRestore = false;
        var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    [TestMethod]
    public async Task Compound_cpu_pending_child_and_windows_proofs_exist_before_first_cpu_dispatch()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        bool inspected = false;
        fixture.Provider.BeforeWrite = async command =>
        {
            if (command is not SetCpuTuningBatchCommand) return;
            var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "control-execution.json")))!;
            Assert.IsNotNull(node["current"]!["pendingStep"]);
            Assert.IsNotNull(saved.Current!.WindowsWriteProofHash);
            Assert.AreEqual(nameof(ApplyControlRequestCommand), saved.Unresolved.Single().CommandKind);
            Assert.AreEqual(saved.Unresolved.Single().OperationId, saved.Current.CpuWriteBatch!.OperationId);
            Assert.IsNotNull(saved.Current.Fields["performance.negativeCurveOptimizer"].WriteTargetDetails);
            inspected = true;
        };
        await SeedCompoundCpu(fixture, true);
        Assert.IsTrue(inspected);
    }

    [TestMethod]
    public async Task Compound_cpu_pending_missing_or_changed_child_proof_and_external_value_retain_original_parent_without_writes()
    {
        foreach (string fault in new[] { "missing", "fields", "target", "actual" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var id = await SeedCompoundCpu(fixture, true);
            string path = Path.Combine(fixture.Root, "control-execution.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var state in new[] { node["current"]!, node["unresolved"]![0]!["state"]! })
            {
                if (fault == "missing") state.AsObject().Remove("pendingStep");
                if (fault == "fields") state["pendingStep"]!["fields"] = new JsonArray("lighting.hex");
                if (fault == "target") state["pendingStep"]!["command"]!["plans"]![0]!["acMaxFrequencyMhz"] = 4321;
            }
            File.WriteAllText(path, node.ToJsonString());
            if (fault == "actual") fixture.Provider.SetExternalCurve(-7);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(id, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        }
    }
}
