using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<(Guid Parent, ClientControlSessionHandle Handle)> SeedPendingLighting(Fixture fixture, bool compound, bool keepLogo = false)
    {
        var before = fixture.Provider.Light;
        fixture.Provider.FailNextLightingWrite = true;
        fixture.Provider.LightingWriteFault = target => target with { Red = before.Red, BrightnessLevel = before.BrightnessLevel };
        if (compound)
        {
            var result = await SeedCompoundGpuFan(fixture, "lightingWithFan");
            fixture.Provider.LightingWriteFault = null;
            return result;
        }
        var handle = await fixture.Begin(null);
        var command = new SetKeyboardLightingCommand(Guid.NewGuid(), before with
            { Red = 17, Green = 34, Blue = 51, BrightnessLevel = 2, Speed = 2, LogoEnabled = keepLogo ? null : false });
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(command, handle)).State);
        fixture.Provider.LightingWriteFault = null;
        return (command.OperationId, handle);
    }

    [TestMethod]
    public async Task Pending_lighting_known_partial_frame_recovers_manual_and_full_compound_prefix_without_replay()
    {
        foreach (bool compound in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = fixture.Provider.ReadScheme(Fixture.Scheme);
            var prior = await SeedPendingLighting(fixture, compound);
            var journal = compound ? new ControlExecutionJournal(fixture.Root) : fixture.Journal;
            fixture.BindModeJournal(journal); fixture.BindGpuJournal(journal);
            fixture.Provider.FanRecovery = (id, scope, token) => CompleteRuntimeFan(fixture, journal, id, scope, token);
            var runtime = compound ? new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
                readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower) : fixture.Runtime;
            var result = compound ? await runtime.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None) : await fixture.End(prior.Handle);
            Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
            Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
            Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
            Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
        }
    }

    [TestMethod]
    public async Task Pending_lighting_exit_lost_ack_keeps_exit_parent_and_retries_without_repeating_frame_write()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var prior = await SeedPendingLighting(fixture, false);
        fixture.Provider.FailNextLightingWrite = true;
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreNotEqual(prior.Parent, saved.Unresolved.Single().OperationId);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        int writes = fixture.Provider.WriteCount;
        Assert.AreEqual(CommandState.Applied, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(probe, CancellationToken.None))!.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
    }

    [TestMethod]
    public async Task Pending_lighting_missing_frame_proof_or_external_color_is_zero_dispatch_and_retains_parent()
    {
        foreach (string fault in new[] { "step", "before", "target", "actual" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var prior = await SeedPendingLighting(fixture, false);
            string path = Path.Combine(fixture.Root, "control-execution.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var state in new[] { node["current"]!, node["unresolved"]![0]!["state"]! })
            {
                if (fault == "step") state.AsObject().Remove("pendingStep");
                if (fault == "before") state["fields"]!["lighting.hex"]!["writeBefore"] = null;
                if (fault == "target") state["pendingStep"]!["command"]!["plan"]!["speed"] = 3;
            }
            File.WriteAllText(path, node.ToJsonString());
            if (fault == "actual") await fixture.Provider.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(),
                fixture.Provider.Light with { Red = 123 }), CancellationToken.None);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None))!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.AreEqual(prior.Parent, (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId);
        }
    }

    [TestMethod]
    public async Task Pending_lighting_full_frame_and_exact_before_are_durable_before_first_dispatch()
    {
        foreach (bool keepLogo in new[] { false, true })
        {
        using var fixture = new Fixture();
        bool inspected = false;
        fixture.Provider.BeforeWrite = async command =>
        {
            if (command is not SetKeyboardLightingCommand) return;
            var saved = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.IsNotNull(saved.Current!.PendingStep);
            var expected = new List<string> { "lighting.effect", "lighting.brightness", "lighting.speed", "lighting.hex" };
            if (!keepLogo) expected.Add("lighting.logo");
            CollectionAssert.AreEquivalent(expected.ToArray(),
                saved.Current.PendingStep.Fields);
            Assert.IsTrue(saved.Current.PendingStep.Fields.All(key => saved.Current.Fields[key].WriteBefore is not null));
            Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
            inspected = true;
        };
        var prior = await SeedPendingLighting(fixture, false, keepLogo);
        Assert.IsTrue(inspected);
        fixture.Provider.BeforeWrite = null;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(prior.Handle)).State);
        Assert.IsTrue(fixture.Provider.Light.LogoEnabled);
        }
    }
}
