using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Archived_A_plan_does_not_replace_interrupted_B_baseline()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var first = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(first)).State);
        fixture.Provider.SetExternalAcFrequency(4400);
        fixture.Provider.Identity = new("board", "bios-v35", "cpu", "gpu");
        fixture.Provider.DeviceKey = new string('B', 64);
        var runtimeB = Restarted(fixture);
        var ownerB = fixture.Owner with { ProcessId = 43 };
        var beginB = await runtimeB.BeginClientControlSessionAsync(Guid.NewGuid(), ownerB, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, beginB.State);
        var second = beginB.ClientSession!;
        Assert.AreEqual(CommandState.Applied, (await runtimeB.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(second.SessionId, null), ownerB, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Applied, (await runtimeB.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(),
            CpuPlan(4300, -4), true) { ClientSessionId = second.SessionId }, CancellationToken.None,
            ownerB.ConnectionId, ownerB.UserSid, ownerB)).State);

        var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(ownerB)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(4400, fixture.Provider.Cpu.AcFrequency());
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual(second.SessionId, capture.RootElement.GetProperty("handle").GetProperty("sessionId").GetGuid());
        Assert.AreEqual("Completed", capture.RootElement.GetProperty("phase").GetString());
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(second.SessionId, plan.RootElement.GetProperty("sessionId").GetGuid());
    }

    [TestMethod]
    public async Task Archived_A_plan_requires_exact_archive_before_B_restore()
    {
        foreach (string damage in new[] { "missing", "device", "plan", "both" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var first = await fixture.Begin(-30);
            Assert.AreEqual(CommandState.Applied, (await fixture.End(first)).State);
            fixture.Provider.Identity = new("board", "bios-v35", "cpu", "gpu");
            fixture.Provider.DeviceKey = new string('B', 64);
            var runtimeB = Restarted(fixture);
            var ownerB = fixture.Owner with { ProcessId = 43 };
            var beginB = await runtimeB.BeginClientControlSessionAsync(Guid.NewGuid(), ownerB, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, beginB.State);
            var second = beginB.ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await runtimeB.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                new(second.SessionId, null), ownerB, CancellationToken.None)).State);
            string archive = Path.Combine(fixture.Root, "completed-client-sessions", first.SessionId.ToString("N") + ".json");
            if (damage == "missing") File.Delete(archive);
            else
            {
                var json = JsonNode.Parse(File.ReadAllText(archive))!;
                if (damage == "device") json["session"]!["handle"]!["deviceKey"] = new string('C', 64);
                else json["recovery"]!["operationId"] = Guid.NewGuid();
                if (damage == "both")
                    File.WriteAllText(Path.Combine(fixture.Root, "client-session-recovery.json"), json["recovery"]!.ToJsonString());
                File.WriteAllText(archive, json.ToJsonString());
            }
            var records = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(ownerB)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.IsNotNull(result);
            Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, records);
        }
    }

    [TestMethod]
    public async Task Archived_A_plan_does_not_authorize_B_restore_over_third_party_value()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var first = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(first)).State);
        fixture.Provider.SetExternalAcFrequency(4400);
        var ownerB = fixture.Owner with { ProcessId = 43 };
        var beginB = await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), ownerB, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, beginB.State);
        var second = beginB.ClientSession!;
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
            new(second.SessionId, null), ownerB, CancellationToken.None)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(),
            CpuPlan(4300, -4), true) { ClientSessionId = second.SessionId }, CancellationToken.None,
            ownerB.ConnectionId, ownerB.UserSid, ownerB)).State);
        fixture.Provider.SetExternalAcFrequency(4500);
        var records = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;

        var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(ownerB)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, records);
    }

    [TestMethod]
    public async Task Fully_verified_Closing_plan_finishes_original_session_without_hardware_writes()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(-30);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        var capturePath = Path.Combine(fixture.Root, "client-control-session.json");
        var capture = JsonNode.Parse(File.ReadAllText(capturePath))!;
        capture["phase"] = "Closing";
        File.WriteAllText(capturePath, capture.ToJsonString());
        int writes = fixture.Provider.WriteCount;
        var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        using var saved = JsonDocument.Parse(File.ReadAllText(capturePath));
        Assert.AreEqual(handle.SessionId, saved.RootElement.GetProperty("handle").GetProperty("sessionId").GetGuid());
        Assert.AreEqual("Completed", saved.RootElement.GetProperty("phase").GetString());

        using var windowsFixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var windowsHandle = await windowsFixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await windowsFixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            CpuPlan(4600, -5), true), windowsHandle)).State);
        Assert.AreEqual(CommandState.Applied, (await windowsFixture.End(windowsHandle)).State);
        var windowsCapturePath = Path.Combine(windowsFixture.Root, "client-control-session.json");
        var windowsCapture = JsonNode.Parse(File.ReadAllText(windowsCapturePath))!;
        windowsCapture["phase"] = "Closing";
        File.WriteAllText(windowsCapturePath, windowsCapture.ToJsonString());
        Assert.IsNull((await windowsFixture.Runtime.ReadControlJournalAsync(CancellationToken.None))!.Current!.WindowsScopeSessionId);
        int windowsWrites = windowsFixture.Provider.WriteCount;
        var windowsResult = await Restarted(windowsFixture).RestorePriorClientSessionAsync(new ProcessProbe(windowsFixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(windowsResult);
        Assert.AreEqual(CommandState.Applied, windowsResult.State, windowsResult.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(windowsWrites, windowsFixture.Provider.WriteCount);
        using var windowsSaved = JsonDocument.Parse(File.ReadAllText(windowsCapturePath));
        Assert.AreEqual("Completed", windowsSaved.RootElement.GetProperty("phase").GetString());
    }

    [TestMethod]
    public async Task Verified_fan_drift_refuses_Closing_finalization_without_changing_records()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        var capturePath = Path.Combine(fixture.Root, "client-control-session.json");
        var capture = JsonNode.Parse(File.ReadAllText(capturePath))!;
        capture["phase"] = "Closing";
        File.WriteAllText(capturePath, capture.ToJsonString());
        fixture.Provider.SetExternalFan(1, 5, 7);
        var records = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;

        var result = await Restarted(fixture).RestorePriorClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, records);
    }
}
