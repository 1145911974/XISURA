using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Static_speed_metadata_does_not_leave_unverified_ownership_and_exit_restores_baseline()
    {
        using var fixture = new Fixture();
        fixture.Provider.LightingWriteFault = plan => plan with
        {
            Effect = plan.Effect == "Static" ? "Fixed" : plan.Effect,
            Speed = plan.Effect == "Static" ? 1 : plan.Speed,
            Brightness = plan.BrightnessLevel is int level ? (int)Math.Round(level * 100d / 3) : plan.Brightness
        };
        var handle = await fixture.Begin(configureCurve: null);
        foreach (double invalid in new[] { 3d, double.NaN })
        {
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.Rejected,
                (await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), Fixture.OriginalLight with { Speed = invalid }), handle)).State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
        }
        var requestPlan = Fixture.OriginalLight with
        {
            Effect = "Static", Brightness = 67, BrightnessLevel = 2, Speed = 1.5, LogoEnabled = null
        };
        var request = new SetKeyboardLightingCommand(Guid.NewGuid(), requestPlan);

        var applied = await fixture.Write(request, handle);

        Assert.AreSame(requestPlan, request.Plan, "Session normalization must not mutate the caller's request.");
        Assert.AreEqual(CommandState.Applied, applied.State);
        Assert.AreEqual("Fixed", fixture.Provider.Light.Effect);
        Assert.AreEqual(1d, fixture.Provider.Light.Speed);
        Assert.IsTrue(applied.ExecutionState!.Fields.Values.All(field => field.WriteState == ApplyState.Confirmed));
        Assert.IsFalse(applied.ExecutionState.Fields.ContainsKey("lighting.speed"), "Unchanged static speed must not retain ownership.");

        var ended = await fixture.End(handle);
        Assert.AreEqual(CommandState.Applied, ended.State, ended.Error?.Code.ToString());
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
    }

    [TestMethod]
    public async Task Gradient_speed_changed_externally_blocks_exit_without_lighting_write()
    {
        using (var ordinary = new Fixture())
        {
            var session = await ordinary.Begin(configureCurve: null);
            await ordinary.Write(new SetKeyboardLightingCommand(Guid.NewGuid(),
                Fixture.OriginalLight with { Effect = "Gradient", Speed = 1.5 }), session);
            Assert.AreEqual(CommandState.Applied, (await ordinary.End(session)).State);
            Assert.AreEqual(Fixture.OriginalLight, ordinary.Provider.Light);
        }
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: null);
        var requested = Fixture.OriginalLight with { Effect = "Gradient", Speed = 1.5, LogoEnabled = null };
        Assert.AreEqual(CommandState.Applied,
            (await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), requested), handle)).State);
        await fixture.Provider.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), requested with { Speed = 1 }),
            CancellationToken.None);
        fixture.Provider.BeforeWrite = command =>
        {
            Assert.IsFalse(command is SetKeyboardLightingCommand, "Do not overwrite externally changed Gradient speed.");
            return Task.CompletedTask;
        };

        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(1d, fixture.Provider.Light.Speed);
    }

    [TestMethod]
    public async Task Persisted_static_speed_and_percentage_metadata_can_resume_without_rewriting_expected_values()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin(configureCurve: null);
        var edited = Fixture.OriginalLight with { Brightness = 67, BrightnessLevel = 2 };
        await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), edited), handle);
        fixture.Provider.BeforeWrite = command => command is SetKeyboardLightingCommand
            ? throw new IOException("interruptBeforeLightingRestore") : Task.CompletedTask;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
        Assert.AreEqual(edited, fixture.Provider.Light);
        string path = Path.Combine(fixture.Root, "client-session-recovery.json");
        var plan = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var lighting = plan["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "lighting")!;
        var expected = lighting["expected"]!["value"]!;
        expected["Effect"] = "Static"; expected["Speed"] = 1.5; expected["Brightness"] = 66;
        File.WriteAllText(path, plan.ToJsonString());
        fixture.Provider.BeforeWrite = null;

        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        var retained = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["steps"]!.AsArray()
            .Single(step => step!["key"]!.GetValue<string>() == "lighting")!["expected"]!["value"]!;
        Assert.AreEqual(1.5, retained["Speed"]!.GetValue<double>());
        Assert.AreEqual(66, retained["Brightness"]!.GetValue<int>());
    }
}
