using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Power_continuity_capture_is_frozen_and_changed_retry_preserves_the_original_session()
    {
        var current = RestartPower;
        int reads = 0;
        using var fixture = new Fixture(readBootTime: ReadRestartBoot,
            readPowerContinuity: _ => { reads++; return Task.FromResult<PowerContinuityEvidence?>(current); });
        var handle = await fixture.Begin(null);
        Assert.AreEqual(2, reads);
        string path = Path.Combine(fixture.Root, "client-control-session.json"), original = File.ReadAllText(path);
        using var saved = JsonDocument.Parse(original);
        Assert.AreEqual(200UL, saved.RootElement.GetProperty("powerContinuity").GetProperty("lastWakeTime").GetUInt64());
        Assert.AreEqual(100UL, saved.RootElement.GetProperty("powerContinuity").GetProperty("lastSleepTime").GetUInt64());
        Assert.AreEqual(CommandState.Applied, (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(original, File.ReadAllText(path));
        current = new(400, 300);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        Assert.AreEqual(original, File.ReadAllText(path));
        Assert.AreEqual(0, fixture.Provider.WriteCount);
        var invalid = JsonNode.Parse(original)!;
        invalid["powerContinuity"]!["lastSleepTime"] = 201UL;
        File.WriteAllText(path, invalid.ToJsonString());
        var records = OriginalRecords(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None));
        AssertOriginalRecords(fixture, records);
    }

    [TestMethod]
    public async Task Power_continuity_missing_invalid_or_changed_during_capture_never_saves_a_session()
    {
        foreach (PowerContinuityEvidence?[] values in new PowerContinuityEvidence?[][]
            { [null], [new(100, 200)], [RestartPower, null], [RestartPower, new(400, 300)] })
        {
            int reads = 0;
            using var fixture = new Fixture(readBootTime: ReadRestartBoot,
                readPowerContinuity: _ => Task.FromResult(values[Math.Min(reads++, values.Length - 1)]));
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-control-session.json")));
            Assert.AreEqual(0, fixture.Provider.WriteCount);
        }
    }

    [TestMethod]
    public async Task Power_continuity_restart_rejects_missing_changed_or_unstable_counts_before_recovery()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        var original = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        foreach (PowerContinuityEvidence?[] values in new PowerContinuityEvidence?[][]
            { [null], [new(400, 300)], [RestartPower, new(400, 300)], [RestartPower, null] })
        {
            int reads = 0;
            var restarted = Restarted(fixture, power: _ => Task.FromResult(values[Math.Min(reads++, values.Length - 1)]));
            AssertPriorRecovery(await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None), "PowerContinuityMissingOrChanged");
            AssertOriginalRecords(fixture, original);
        }
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        using var legacy = new Fixture(readBootTime: ReadRestartBoot);
        var old = await legacy.Begin(null);
        await legacy.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), old);
        var legacyRecords = OriginalRecords(legacy);
        AssertPriorRecovery(await Restarted(legacy).RecoverExitedClientSessionAsync(new ProcessProbe(legacy.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None), "PowerContinuityMissingOrChanged");
        AssertOriginalRecords(legacy, legacyRecords);
    }

    [TestMethod]
    public async Task Power_continuity_probe_reads_only_stable_query_levels_and_honors_cancellation()
    {
        var levels = new List<int>();
        var probe = new WindowsPowerContinuityProbe(level => { levels.Add(level); return (0U, level == 14 ? 200UL : 100UL); });
        Assert.AreEqual(RestartPower, await probe.ReadAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { 14, 15, 14, 15 }, levels);
        foreach (string failure in new[] { "status", "unstable", "invalid", "missing-dll" })
        {
            int reads = 0;
            var failing = new WindowsPowerContinuityProbe(level =>
            {
                reads++;
                if (failure == "missing-dll") throw new DllNotFoundException();
                return (failure == "status" ? 0xC0000022U : 0U,
                    failure == "invalid" ? level == 14 ? 100UL : 200UL :
                    failure == "unstable" && reads > 2 ? level == 14 ? 400UL : 300UL : level == 14 ? 200UL : 100UL);
            });
            Assert.IsNull(await failing.ReadAsync(CancellationToken.None), failure);
        }
        Assert.AreEqual(new PowerContinuityEvidence(0, 0), await new WindowsPowerContinuityProbe(_ => (0U, 0UL)).ReadAsync(CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        int before = levels.Count;
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.ReadAsync(cancelled.Token));
        Assert.AreEqual(before, levels.Count);
        using var during = new CancellationTokenSource();
        int duringReads = 0;
        var interrupted = new WindowsPowerContinuityProbe(_ => { duringReads++; during.Cancel(); return (0U, 0UL); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted.ReadAsync(during.Token));
        Assert.AreEqual(1, duringReads);
    }

    [TestMethod]
    public async Task Power_continuity_windows_probe_is_read_only_and_returns_stable_counts_without_sleep_testing()
    {
        var probe = new WindowsPowerContinuityProbe();
        var first = await probe.ReadAsync(CancellationToken.None);
        var second = await probe.ReadAsync(CancellationToken.None);
        Assert.IsNotNull(first, "A failed native query cannot prove continuity on this Windows installation.");
        Assert.AreEqual(first, second, "No sleep or power operation is performed by this test.");
        Console.WriteLine($"Read-only power counts: lastWakeTime={first.LastWakeTime}, lastSleepTime={first.LastSleepTime}");
    }
}
