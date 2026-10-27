using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Submit_only_gradient_can_exit_with_known_logo_but_rejects_external_logo_change()
    {
        foreach (bool external in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Provider.LightingFieldsReadable = false;
            var handle = await fixture.Begin(configureCurve: null);
            var result = await fixture.Write(new SetKeyboardLightingCommand(Guid.NewGuid(), Fixture.OriginalLight with
                { Effect = "Gradient", Speed = 1.5, LogoEnabled = null }), handle);
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.IsTrue(result.ExecutionState!.Fields.Values.All(field => field.WriteState == ApplyState.SubmittedUnverified));
            if (external)
            {
                await fixture.Provider.ExecuteAsync(new SetLidLogoCommand(Guid.NewGuid(), false), CancellationToken.None);
                fixture.Provider.BeforeWrite = command =>
                {
                    Assert.IsFalse(command is SetKeyboardLightingCommand, "External Logo must not be overwritten.");
                    return Task.CompletedTask;
                };
            }
            Assert.AreEqual(external ? CommandState.RecoveryRequired : CommandState.Applied, (await fixture.End(handle)).State);
            if (!external) Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
            else Assert.IsFalse(fixture.Provider.Light.LogoEnabled);
        }
    }
}
