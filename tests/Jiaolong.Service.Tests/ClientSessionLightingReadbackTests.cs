using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Static_alias_and_rounded_hardware_brightness_confirm_and_exit_to_original()
    {
        foreach (int level in new[] { 1, 2 })
        {
            using var fixture = new Fixture();
            fixture.Provider.LightingWriteFault = plan => plan with
            {
                Effect = plan.Effect == "Static" ? "Fixed" : plan.Effect,
                Brightness = (int)Math.Round(plan.BrightnessLevel!.Value * 100d / 3)
            };
            var handle = await fixture.Begin(configureCurve: null);
            var result = await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), Fixture.OriginalLight with
            { Effect = "Static", Brightness = (int)Math.Round(level * 100d / 3), BrightnessLevel = level, LogoEnabled = null }), handle);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.IsTrue(result.ExecutionState!.Fields.Values.All(field => field.WriteState == ApplyState.Confirmed));
            var journal = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
            Assert.AreEqual(0, journal!.Unresolved.Length);
            Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
            Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
            journal = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
            Assert.AreEqual(0, journal!.Unresolved.Length);
            Assert.AreEqual(0, journal.Current!.Fields.Count);
        }
    }

    [TestMethod]
    public async Task Equivalent_lighting_representations_do_not_hide_external_color_or_level_changes()
    {
        foreach (bool colorChanged in new[] { false, true })
        {
            using var fixture = new Fixture();
            var handle = await fixture.Begin(configureCurve: null);
            var edited = Fixture.OriginalLight with { Brightness = 67, BrightnessLevel = 2 };
            Assert.AreEqual(CommandState.Applied,
                (await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), edited), handle)).State);
            var external = colorChanged ? edited with { Red = 1 } : edited with { Brightness = 33, BrightnessLevel = 1 };
            await fixture.Provider.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), external), CancellationToken.None);
            fixture.Provider.BeforeWrite = command =>
            {
                Assert.IsFalse(command is SetKeyboardLightingCommand, "External lighting must not be overwritten");
                return Task.CompletedTask;
            };
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
            Assert.AreEqual(external, fixture.Provider.Light);
        }
    }
}
