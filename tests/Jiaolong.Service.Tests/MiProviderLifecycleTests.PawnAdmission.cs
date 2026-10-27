using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class MiProviderLifecycleTests
{
    [TestMethod]
    public void Production_fan_factory_constructs_unopened_pawn_backend_without_bios_lockout()
    {
        foreach (string bios in new[] { "MRID6_23_P_V35", "V39", "V40" })
        {
            using var provider = new WindowsHomeHardwareProvider();
            typeof(WindowsHomeHardwareProvider).GetField("identity", Private)!.SetValue(provider, new HardwareIdentity("mrid6-23", bios, "cpu", "gpu"));
            typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable, new CapabilitySnapshot([new("fanControl", CapabilityState.Available, null)]), null, []));
            Assert.AreEqual(true, InvokeMappingProvider(provider, "HasFanEcProtocol"));
            using var transport = (BldingFanEcTransport)InvokeMappingProvider(provider, "CreateFanTransport")!;
            var backend = typeof(BldingFanEcTransport).GetField("portBackend", Private)!.GetValue(transport);
            Assert.IsInstanceOfType<PawnIoLpcEcPorts>(backend);
            Assert.IsNull(typeof(PawnIoLpcEcPorts).GetField("handle", Private)!.GetValue(backend));
            Assert.AreEqual(0L, transport.ValueWriteAttempts);
        }
    }

    [TestMethod]
    public async Task Fan_ec_admission_rejects_unknown_board_or_missing_protocol_before_mi_or_ec_access()
    {
        foreach (bool unknownBoard in new[] { true, false })
        {
            int creates = 0;
            using var fixture = await FirmwareFixture.Create(0, (_, _, _) => { creates++; throw new IOException("must not construct EC"); });
            if (unknownBoard)
                typeof(WindowsHomeHardwareProvider).GetField("identity", Private)!.SetValue(fixture.Provider, fixture.Scope.Identity with { BoardProduct = "OTHER" });
            else
                typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(fixture.Provider,
                    new CompatibilityDecision(CompatibilityMode.Writable, new CapabilitySnapshot([new("strongCooling", CapabilityState.Available, null)]), null, []));
            fixture.Bus.ReadUnavailable = true;
            int reads = fixture.Bus.Reads;
            foreach (HardwareCommand command in new HardwareCommand[]
            {
                new SetStrongCoolingCommand(Guid.NewGuid(), true),
                new SetFanControlCommand(Guid.NewGuid(), new FanControlPlan([new(30, 30), new(100, 100)]) { Strategy = "Fixed", FixedRpm = 3000 }, true)
            })
            {
                var result = await fixture.Provider.ExecuteAsync(command, CancellationToken.None);
                Assert.AreEqual(ErrorCode.CapabilityUnavailable, result.Error?.Code);
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            }
            Assert.AreEqual(reads, fixture.Bus.Reads);
            Assert.AreEqual(0, fixture.Bus.Writes);
            Assert.AreEqual(0, creates);
            Assert.IsNull((await fixture.Journal.ReadAsync(CancellationToken.None)).FanEcRecovery);
        }
    }
}
