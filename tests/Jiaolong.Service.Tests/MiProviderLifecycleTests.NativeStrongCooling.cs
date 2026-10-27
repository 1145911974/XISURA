using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class MiProviderLifecycleTests
{
    private static CommandResult NativeCooling(WindowsHomeHardwareProvider provider, bool enabled) =>
        (CommandResult)typeof(WindowsHomeHardwareProvider).GetMethod("ExecuteStrongCoolingLocked", Private)!
            .Invoke(provider, [new SetStrongCoolingCommand(Guid.NewGuid(), enabled), CancellationToken.None])!;

    [TestMethod]
    public async Task Native_cooling_both_directions_use_firmware_wal_without_ec_or_temperature_dependencies()
    {
        foreach (byte original in new byte[] { 0, 1 })
        {
            using var fixture = await FirmwareFixture.Create(original, (_, _, _) => throw new AssertFailedException("Native boost must not open EC."));
            fixture.Bus.BeforeWrite = () =>
            {
                var pending = new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None).GetAwaiter().GetResult().FanFirmwareRecovery!;
                Assert.AreEqual(original, pending.OriginalBefore);
                Assert.IsNull(pending.Observed);
            };
            Assert.AreEqual(CommandState.Applied, NativeCooling(fixture.Provider, original == 0).State);
            Assert.AreEqual((byte)(1 - original), fixture.Bus.Value);
            Assert.AreEqual(original, (await fixture.Journal.ReadAsync(CancellationToken.None)).FanFirmwareRecovery!.OriginalBefore);
            fixture.Restore();
            Assert.AreEqual(original, fixture.Bus.Value);
            Assert.AreEqual(2, fixture.Bus.Writes);
            Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete((await fixture.Journal.ReadAsync(CancellationToken.None)).FanFirmwareRecovery!));
        }
    }

    [TestMethod]
    public async Task Native_cooling_reopens_both_original_values_and_restores_without_mapping_checkpoint()
    {
        foreach (byte original in new byte[] { 0, 1 })
        {
            using var fixture = await FirmwareFixture.Create(original, (_, _, _) => throw new AssertFailedException("Native recovery must not open EC."));
            Assert.AreEqual(CommandState.Applied, NativeCooling(fixture.Provider, original == 0).State);
            var store = new ClientControlSessionStore(fixture.Root);
            await store.WriteAsync((await store.ReadAsync(CancellationToken.None))! with { Phase = ClientControlSessionPhase.Closing },
                CancellationToken.None, requireExisting: true);
            var reopenedJournal = new ControlExecutionJournal(fixture.Root);
            using var reopened = new WindowsHomeHardwareProvider(reopenedJournal, (_, _, _) => throw new AssertFailedException("EC open"), () => false);
            foreach (string name in new[] { "identity", "compatibilityDecision", "miInterface" })
            {
                var field = typeof(WindowsHomeHardwareProvider).GetField(name, Private)!;
                field.SetValue(reopened, field.GetValue(fixture.Provider));
            }
            var operation = (await reopenedJournal.ReadAsync(CancellationToken.None)).Unresolved.Single().OperationId;
            using var admission = reopenedJournal.BindFanRecovery(new(operation, fixture.Scope));
            Assert.AreEqual(CommandState.Applied, (await reopened.RecoverSessionFanAsync(operation, fixture.Scope, CancellationToken.None)).State);
            Assert.AreEqual(original, fixture.Bus.Value);
            var saved = await reopenedJournal.ReadAsync(CancellationToken.None);
            Assert.IsNull(saved.FanEcRecovery);
            Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete(saved.FanFirmwareRecovery!));
        }
    }

    [TestMethod]
    public async Task Native_cooling_unknown_readback_and_external_state_keep_recovery_evidence_without_overwrite()
    {
        using var fixture = await FirmwareFixture.Create(0, (_, _, _) => throw new AssertFailedException("EC open"));
        fixture.Bus.FailReadAfterWrite = true;
        Assert.AreEqual(CommandState.RecoveryRequired, NativeCooling(fixture.Provider, true).State);
        Assert.IsNull((await fixture.Journal.ReadAsync(CancellationToken.None)).FanFirmwareRecovery!.Observed);
        fixture.Bus.FailReadAfterWrite = fixture.Bus.ReadUnavailable = false;
        fixture.Bus.Value = 2;
        int writes = fixture.Bus.Writes;
        Assert.ThrowsExactly<IOException>(() => fixture.Restore());
        Assert.AreEqual(writes, fixture.Bus.Writes);
        fixture.Bus.Value = 1;
        fixture.Restore();
        Assert.AreEqual((byte)0, fixture.Bus.Value);
    }

    [TestMethod]
    public async Task Native_cooling_keep_detaches_software_ownership_without_reverting_firmware_on_dispose()
    {
        using var fixture = await FirmwareFixture.Create(0, (_, _, _) => throw new AssertFailedException("EC open"));
        Assert.AreEqual(CommandState.Applied, NativeCooling(fixture.Provider, true).State);
        int writes = fixture.Bus.Writes;
        await fixture.Provider.RetainClientNativeCoolingAsync(CancellationToken.None);
        fixture.Provider.Dispose();
        Assert.AreEqual((byte)1, fixture.Bus.Value);
        Assert.AreEqual(writes, fixture.Bus.Writes);
        Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("fanPreviousStrongCooling", Private)!.GetValue(fixture.Provider));
        Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("fanTimer", Private)!.GetValue(fixture.Provider));
    }
}
