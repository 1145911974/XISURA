using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class PerCoreLifecycleTests
{
    [TestMethod]
    public void Per_core_pause_stops_reads_writes_and_unchecked_recovery_but_read_permission_never_allows_writes()
    {
        foreach (string stage in new[] { "capture", "write", "recovery" })
        {
            bool allowed = true;
            var calls = new List<string>();
            var values = Enumerable.Range(0, 8).ToDictionary(core => core, _ => 10);
            int writes = 0;
            uint Execute(uint command, uint argument)
            {
                int core = (int)(argument >> 20);
                calls.Add(command == 0xD5 ? "read" : "write");
                if (command != 0xD5) { writes++; values[core] = unchecked((short)argument); }
                if (stage == "capture" || stage == "write" && writes == 1 || stage == "recovery" && writes == 3)
                { allowed = false; calls.Add("paused"); }
                if (stage == "recovery" && writes == 2) throw new IOException("simulatedPartialWrite");
                return unchecked((uint)values[core]);
            }
            using var transport = new PawnIoCurveOptimizerTransport(Execute, hardwareAccessAllowed: () => allowed);
            Exception? failure = null;
            try { transport.WritePerCore(new Dictionary<int, int> { [0] = -30, [1] = -20 }, CancellationToken.None); }
            catch (Exception error) { failure = error; }
            Assert.IsNotNull(failure, stage);
            Assert.AreEqual("paused", calls.Last(), "No later core read/write/recovery after pause: " + stage);
            Assert.AreEqual(stage == "capture" ? 0 : stage == "write" ? 1 : 3, writes);
            if (stage != "capture") Assert.AreEqual(true, failure.Data["curveOptimizerRollbackFailed"]);
        }
        int readCalls = 0;
        bool readsAllowed = true;
        using var readonlyTransport = new PawnIoCurveOptimizerTransport((command, argument) =>
        {
            Assert.AreEqual(0xD5u, command, "Recovery diagnostic permission is read-only.");
            readCalls++;
            return 10;
        }, hardwareAccessAllowed: () => false, hardwareReadAllowed: () => readsAllowed);
        Assert.AreEqual(8, readonlyTransport.ReadPerCore(CancellationToken.None).Count);
        Assert.Throws<IOException>(() => readonlyTransport.WritePerCore(new Dictionary<int, int> { [0] = -30 }, CancellationToken.None));
        Assert.AreEqual(8, readCalls);
        readsAllowed = false;
        Assert.Throws<IOException>(() => readonlyTransport.ReadPerCore(CancellationToken.None));
        Assert.AreEqual(8, readCalls);
    }

    [TestMethod]
    public void Per_core_capture_pause_returns_zero_write_rejection_but_partial_write_stays_unknown()
    {
        foreach (string stage in new[] { "capture", "entry", "cancel", "captureFailed", "write", "cancelAfterWrite", "cancelRecoveryPaused" })
        {
            bool duringWrite = stage is "write" or "cancelAfterWrite" or "cancelRecoveryPaused";
            bool allowed = stage != "entry";
            int writes = 0;
            var offsets = Enumerable.Range(0, 8).ToDictionary(core => core, _ => 10);
            using var cancellation = new CancellationTokenSource();
            if (stage == "cancel") cancellation.Cancel();
            using var transport = new PawnIoCurveOptimizerTransport((command, argument) =>
            {
                if (stage == "captureFailed") throw new IOException("baselineReadFailed");
                int core = (int)(argument >> 20);
                if (command == 0x06)
                {
                    writes++;
                    offsets[core] = unchecked((short)argument);
                    if (writes == 1 && stage is "cancelAfterWrite" or "cancelRecoveryPaused") cancellation.Cancel();
                }
                if (stage == "capture" || stage == "write" && writes == 1 || stage == "cancelRecoveryPaused" && writes == 2)
                    allowed = false;
                return unchecked((uint)offsets[core]);
            }, hardwareAccessAllowed: () => allowed);
            using var provider = new WindowsHomeHardwareProvider();
            typeof(WindowsHomeHardwareProvider).GetField("curveOptimizer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(provider, transport);
            var result = provider.ExecutePerCoreCurveTransaction(Guid.NewGuid(), new Dictionary<int, int> { [0] = -30 },
                cancellation.Token);
            bool rolledBack = stage == "cancelAfterWrite";
            Assert.AreEqual(rolledBack ? 9 : stage == "cancelRecoveryPaused" ? 2 : duringWrite ? 1 : 0, writes, stage);
            Assert.AreEqual(rolledBack ? CommandState.RolledBack : duringWrite ? CommandState.RecoveryRequired : CommandState.Rejected, result.State, stage);
            Assert.AreEqual(duringWrite ? HardwareMutation.Unknown : HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(rolledBack ? ErrorCode.HardwareWriteFailed : duringWrite ? ErrorCode.RollbackFailed : stage == "captureFailed" ? ErrorCode.HardwareReadFailed :
                ErrorCode.CancelledBeforeApply, result.Error?.Code);
            Assert.IsTrue(result.Error!.Details.ContainsKey("reason"));
            Assert.AreEqual(duringWrite ? "True" : "False", result.Error.Details["writeAttempted"]);
            Assert.AreEqual(rolledBack.ToString(), result.Error.Details["rollbackConfirmed"]);
            if (rolledBack) Assert.IsTrue(offsets.Values.All(value => value == 10), "Restore the captured positive BIOS values after ordinary cancellation.");
        }
    }
}
