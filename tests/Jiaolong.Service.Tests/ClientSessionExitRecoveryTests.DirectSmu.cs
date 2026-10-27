using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task ApplyDirectPreset(Fixture fixture, ClientControlSessionHandle handle, SettingRequest advanced, int revision,
        CommandState expected = CommandState.Applied)
    {
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key, "direct",
            System.Text.Json.JsonSerializer.SerializeToElement(new { temperatureLimitC = 85, splWatts = 55, spptWatts = 65,
                acMaxFrequencyMhz = 4700, dcMaxFrequencyMhz = 4700, isBoostEnabled = true, windowsPowerSchemeId = Fixture.Scheme,
                acMinActiveCoresPercent = (int?)null, dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null,
                advancedCpuTuning = advanced.Value?.Deserialize<AdvancedCpuTuningPlan>(ControlPlanCompiler.Json) }), DateTimeOffset.UtcNow)
        { Revision = revision, Settings = new() { ["advancedCpuTuning"] = advanced } };
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new PublishPresetCatalogCommand(Guid.NewGuid(), revision - 1, [preset]), handle)).State);
        var id = Guid.NewGuid();
        var applied = await fixture.Write(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
            ControlModeId.Gaming, PerformanceMode.Turbo, [new(ControlPageId.Performance, true, key, revision)])), handle);
        Assert.AreEqual(expected, applied.State, System.Text.Json.JsonSerializer.Serialize(applied));
    }

    [TestMethod]
    public async Task Same_mode_performance_preset_preserves_native_ownership_and_restores_the_exact_cpu_baseline()
    {
        foreach (bool priorModeOwner in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Provider.DirectLimitsEnabled = true;
            var handle = await fixture.Begin(null);
            int modeWrites = 0;
            fixture.Provider.ModeWriteEffects = () => modeWrites++;
            if (priorModeOwner)
            {
                Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(),
                    PerformanceMode.Turbo), handle)).State);
                await Task.Delay(TimeSpan.FromMilliseconds(3100));
            }
            int beforeModeWrites = modeWrites;
            var prior = await fixture.Journal.ReadAsync(CancellationToken.None);
            await ApplyDirectPreset(fixture, handle, SettingRequest.Set(DirectPlan(65.25, 85).Advanced!), 1);
            var applied = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(ApplyState.SubmittedUnverified, applied.Current!.NativeModeApplyState);
            Assert.AreEqual(beforeModeWrites, modeWrites, "Applying a same-mode CPU page must not invoke OEM mode effects.");
            Assert.AreEqual(prior.Current!.NativeModeOwnerClientSessionId, applied.Current!.NativeModeOwnerClientSessionId);
            Assert.AreEqual(120.125, applied.Current.Fields["performance.pptWatts"].Baseline!.Value!.Value.GetDouble());
            Assert.AreEqual(65.25, fixture.Provider.DirectPpt);
            Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
            Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
            Assert.AreEqual(99, fixture.Provider.DirectTemperature);
        }
    }

    [TestMethod]
    public async Task DirectSmu_compiled_preset_and_official_preserve_first_baseline_after_scheme_change()
    {
        foreach (bool interrupted in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            fixture.Provider.DirectLimitsEnabled = true;
            var handle = await fixture.Begin(null);
            await ApplyDirectPreset(fixture, handle, SettingRequest.Set(new AdvancedCpuTuningPlan(PptWatts: 60.125, Mp1TemperatureC: 80)), 1);
            if (!interrupted) fixture.Provider.SetActiveScheme(Fixture.OtherScheme);
            await ApplyDirectPreset(fixture, handle, SettingRequest.Set(new AdvancedCpuTuningPlan(PptWatts: 65.25, Mp1TemperatureC: 85)), 2);
            fixture.Provider.DirectCapabilitiesVerified = false;
            var capabilities = (await fixture.Runtime.GetStateAsync(CancellationToken.None)).Capabilities.Fields;
            for (int index = 0; index < capabilities.Length; index++)
                if (capabilities[index].Key is "pptWatts" or "mp1TemperatureC") capabilities[index] = capabilities[index] with { VerifiedAtUtc = null };
            int writes = fixture.Provider.WriteCount;
            await ApplyDirectPreset(fixture, handle, SettingRequest.Official, 3);
            Assert.AreEqual(65.25, fixture.Provider.DirectPpt);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            fixture.Provider.DirectCapabilitiesVerified = true;
            for (int index = 0; index < capabilities.Length; index++)
                if (capabilities[index].Key is "pptWatts" or "mp1TemperatureC") capabilities[index] = capabilities[index] with { VerifiedAtUtc = DateTimeOffset.UtcNow };
            if (interrupted) fixture.Provider.UnverifiedDirectRestoreOnce = "pptWatts";
            await ApplyDirectPreset(fixture, handle, SettingRequest.Official, 4, interrupted ? CommandState.RecoveryRequired : CommandState.Applied);
            if (interrupted)
            {
                var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
                Assert.IsNotNull(saved.Current!.PendingStep);
                Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                    new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            }
            else
            {
                fixture.Provider.SetActiveScheme(Fixture.Scheme);
                Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
            }
            Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
            Assert.AreEqual(99, fixture.Provider.DirectTemperature);
        }
    }

    private static CpuTuningPlan DirectPlan(double ppt, int temperature) =>
        new(null, null, null, null, null, null, null, null) { Advanced = new(PptWatts: ppt, Mp1TemperatureC: temperature) };

    private static async Task<Guid> SeedDirectPending(Fixture fixture)
    {
        fixture.Provider.DirectLimitsEnabled = true;
        var handle = await fixture.Begin(null);
        var command = new SetCpuTuningBatchCommand(Guid.NewGuid(), [DirectPlan(60.125, 80), DirectPlan(65.25, 85), DirectPlan(70.5, 90)], true);
        fixture.Provider.InterruptCpuBatchAfterPlans = 1;
        var result = await fixture.Write(command, handle);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.AreEqual(60.125, fixture.Provider.DirectPpt);
        return command.OperationId;
    }

    [TestMethod]
    public async Task DirectSmu_writes_durable_scalar_markers_and_exit_restores_first_fractional_baseline_across_schemes()
    {
        using var fixture = new Fixture();
        fixture.Provider.DirectLimitsEnabled = true;
        var handle = await fixture.Begin(null);
        int checks = 0;
        fixture.Provider.BeforeWrite = async _ =>
        {
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            var ppt = saved.Current!.Fields["performance.pptWatts"];
            Assert.AreEqual(ApplyState.Unknown, ppt.WriteState);
            Assert.AreEqual(120.125, ppt.Baseline!.Value!.Value.GetDouble());
            Assert.IsNotNull(ppt.WriteBefore);
            Assert.IsFalse(saved.Current.Fields.ContainsKey("performance.advancedCpuTuning"));
            checks++;
        };
        var result = await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), DirectPlan(60.125, 80), true), handle);
        Assert.AreEqual(CommandState.Applied, result.State, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), DirectPlan(65.25, 85), true), handle)).State);
        Assert.AreEqual(2, checks);
        fixture.Provider.BeforeWrite = null;
        fixture.Provider.SetActiveScheme(Fixture.OtherScheme);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
        Assert.AreEqual(99, fixture.Provider.DirectTemperature);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
    }

    [TestMethod]
    public async Task DirectSmu_pending_batch_restores_proven_intermediate_values_after_restart()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        await SeedDirectPending(fixture);
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
            new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
        Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
        Assert.AreEqual(99, fixture.Provider.DirectTemperature);
        Assert.AreEqual(0, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Length);
    }

    [TestMethod]
    public async Task DirectSmu_pending_external_or_unproven_values_preserve_original_records_without_writes()
    {
        foreach (string fault in new[] { "external", "target", "before", "scheme" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            await SeedDirectPending(fixture);
            if (fault == "external") fixture.Provider.DirectPpt = 61.75;
            else if (fault == "scheme") fixture.Provider.SetActiveScheme(Fixture.OtherScheme);
            else
            {
                string path = Path.Combine(fixture.Root, "control-execution.json");
                var node = JsonNode.Parse(File.ReadAllText(path))!;
                foreach (var state in new[] { node["current"]!, node["unresolved"]!.AsArray()[0]!["state"]! })
                    state["fields"]!["performance.pptWatts"]![fault == "before" ? "writeBefore" : "lastWritten"] = null;
                File.WriteAllText(path, node.ToJsonString());
            }
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, records);
        }
    }

    [TestMethod]
    public async Task DirectSmu_pending_partial_restore_keeps_parent_and_retry_retains_first_baseline()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        Guid parent = await SeedDirectPending(fixture);
        fixture.Provider.FailDirectRestoreOnce = "mp1TemperatureC";
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
        Assert.AreEqual(80, fixture.Provider.DirectTemperature);
        Assert.AreEqual(parent, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(120.125, fixture.Provider.DirectPpt);
        Assert.AreEqual(99, fixture.Provider.DirectTemperature);
    }
}
