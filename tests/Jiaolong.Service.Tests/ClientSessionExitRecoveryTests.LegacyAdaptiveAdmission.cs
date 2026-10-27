using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Legacy_adaptive_pending_preserves_original_evidence_before_capture_or_manual_write()
    {
        foreach (bool alreadyCaptured in new[] { false, true })
        {
            using var fixture = new Fixture();
            var store = new AdaptiveAutomationStateStore();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string path = Path.Combine(fixture.Root, "adaptive-test.json");
            typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
            typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store,
                new AtomicJsonStore(fixture.Root, "adaptive-test.json"));
            var runtime = new HomeServiceRuntime(new AdaptiveAutomationHardwareProvider(fixture.Provider, store),
                journal: fixture.Journal, requireClientSessions: true);
            ClientControlSessionHandle? handle = null;
            if (alreadyCaptured)
            {
                var begun = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
                Assert.AreEqual(CommandState.Applied, begun.State);
                handle = begun.ClientSession!;
                Assert.AreEqual(CommandState.Applied, (await runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(),
                    new(handle.SessionId, null), fixture.Owner, CancellationToken.None)).State);
            }
            var saved = await store.ReadAsync(CancellationToken.None);
            var legacy = new AdaptiveAutomationRecovery(PerformanceMode.Quiet, null, PerformanceMode.Turbo, true, true);
            Assert.IsTrue(await store.TryBeginRecoveryAsync(legacy, saved.Revision, CancellationToken.None));
            byte[] before = await File.ReadAllBytesAsync(path);
            string sessionPath = Path.Combine(fixture.Root, "client-control-session.json");
            byte[]? capture = File.Exists(sessionPath) ? await File.ReadAllBytesAsync(sessionPath) : null;
            string journalPath = Path.Combine(fixture.Root, "control-execution.json");
            byte[]? journal = File.Exists(journalPath) ? await File.ReadAllBytesAsync(journalPath) : null;
            int writes = fixture.Provider.WriteCount;
            if (alreadyCaptured)
            {
                var result = await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced)
                    { ClientSessionId = handle!.SessionId }, CancellationToken.None,
                    fixture.Owner.ConnectionId, fixture.Owner.UserSid, fixture.Owner);
                Assert.AreEqual(CommandState.RecoveryRequired, result.State);
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
                Assert.AreEqual("adaptivePersistedRecoveryRequired", result.Error?.Details.GetValueOrDefault("reason"));
            }
            else
            {
                var result = await runtime.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None);
                Assert.AreEqual(CommandState.RecoveryRequired, result.State);
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
                Assert.IsNull(result.ClientSession);
            }
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            if (capture is null) Assert.IsFalse(File.Exists(sessionPath));
            else CollectionAssert.AreEqual(capture, await File.ReadAllBytesAsync(sessionPath));
            if (journal is null) Assert.IsFalse(File.Exists(journalPath));
            else CollectionAssert.AreEqual(journal, await File.ReadAllBytesAsync(journalPath));

            await store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow, true, CancellationToken.None);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path),
                "A late preparation callback must retain the original legacy override/recovery evidence.");
        }
    }
}
