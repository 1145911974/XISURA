using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Incomplete_sessions_still_reject_firmware_transition_without_rewriting_records()
    {
        foreach (var phase in new[] { ClientControlSessionPhase.Preparing, ClientControlSessionPhase.Active, ClientControlSessionPhase.Closing })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var begin = await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, begin.State);
            var handle = begin.ClientSession!;
            if (phase != ClientControlSessionPhase.Preparing)
                Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                    new(handle.SessionId, null), fixture.Owner, CancellationToken.None)).State);
            if (phase == ClientControlSessionPhase.Closing)
                Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.FreezeClientSessionWritesAsync(Guid.NewGuid(),
                    handle.SessionId, fixture.Owner, CancellationToken.None)).State);
            var original = OriginalRecords(fixture);
            fixture.Provider.Identity = new("board", "bios-v35", "cpu", "gpu");
            fixture.Provider.DeviceKey = new string('B', 64);
            var restarted = Restarted(fixture);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await restarted.BeginClientControlSessionAsync(Guid.NewGuid(),
                fixture.Owner, CancellationToken.None)).State);
            var restored = await restarted.RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.IsNotNull(restored);
            Assert.AreEqual(CommandState.RecoveryRequired, restored.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }

    [TestMethod]
    public async Task Completed_archive_digest_is_immutable_and_legacy_capture_keeps_identity_guard()
    {
        foreach (bool differentFirmware in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = await fixture.Begin(null);
            Assert.AreEqual(CommandState.Applied, (await fixture.End(first)).State);
            fixture.Provider.SetExternalAcFrequency(4400);
            if (differentFirmware)
            {
                fixture.Provider.Identity = new("board", "bios-v35", "cpu", "gpu");
                fixture.Provider.DeviceKey = new string('B', 64);
            }
            var runtime = Restarted(fixture);
            var begin = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, begin.State);
            var second = begin.ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                new(second.SessionId, null), fixture.Owner, CancellationToken.None)).State);
            var store = new ClientControlSessionStore(fixture.Root);
            var capture = (await store.ReadAsync(CancellationToken.None))!;
            string capturePath = Path.Combine(fixture.Root, "client-control-session.json");
            string original = File.ReadAllText(capturePath);
            foreach (string invalid in new[] { "", new string('G', 64), new string('A', 63) })
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(
                    capture with { PreviousCompletedArchiveSha256 = invalid }, CancellationToken.None));
                var damaged = JsonNode.Parse(original)!;
                damaged["previousCompletedArchiveSha256"] = invalid;
                File.WriteAllText(capturePath, damaged.ToJsonString());
                await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(CancellationToken.None));
                Assert.AreEqual(damaged.ToJsonString(), File.ReadAllText(capturePath));
                File.WriteAllText(capturePath, original);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(
                capture with { PreviousCompletedArchiveSha256 = new string('0', 64) }, CancellationToken.None));
            Assert.AreEqual(original, File.ReadAllText(capturePath));
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(),
                CpuPlan(4300, -4), true) { ClientSessionId = second.SessionId }, CancellationToken.None,
                fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner)).State);
            var legacy = JsonNode.Parse(File.ReadAllText(capturePath))!;
            legacy.AsObject().Remove("previousCompletedArchiveSha256");
            File.WriteAllText(capturePath, legacy.ToJsonString());
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.IsNotNull(result);
            Assert.AreEqual(differentFirmware ? CommandState.RecoveryRequired : CommandState.Applied, result.State);
            Assert.AreEqual(differentFirmware ? 4300 : 4400, fixture.Provider.Cpu.AcFrequency());
            if (differentFirmware)
            {
                Assert.AreEqual(writes, fixture.Provider.WriteCount);
                AssertOriginalRecords(fixture, records);
            }
        }
    }
}
