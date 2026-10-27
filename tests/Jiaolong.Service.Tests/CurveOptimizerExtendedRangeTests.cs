using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CurveOptimizerExtendedRangeTests
{
    [TestMethod]
    public void Negative_fifty_readback_is_complete_and_read_only_access_cannot_write()
    {
        int writes = 0;
        using var transport = new PawnIoCurveOptimizerTransport((command, _) =>
        {
            if (command != 0xD5) writes++;
            return unchecked((uint)-50);
        }, hardwareAccessAllowed: () => false, hardwareReadAllowed: () => true);
        var values = transport.ReadPerCore(CancellationToken.None);
        var state = new CpuTuningState(85, 45, 65, 4700, true, 8, null, -50, "hardwareReadback")
            { PerCoreCurveOptimizer = values };
        Assert.IsTrue(state.HasCompleteCurveValues());
        Assert.IsTrue(values.Values.All(value => value == -50));
        Assert.Throws<IOException>(() => transport.WritePerCore(new Dictionary<int, int> { [0] = -49 }, CancellationToken.None));
        Assert.AreEqual(0, writes);
        foreach (int invalid in new[] { -51, 31 })
        {
            using var corrupt = new PawnIoCurveOptimizerTransport((_, _) => unchecked((uint)invalid));
            Assert.Throws<InvalidOperationException>(() => corrupt.ReadPerCore(CancellationToken.None));
        }
    }

    [TestMethod]
    public void Extended_negative_targets_preserve_bounds_and_restore_negative_fifty_after_partial_failure()
    {
        var values = Enumerable.Range(0, 8).ToDictionary(core => core, _ => -50);
        int writes = 0;
        bool failSecond = false;
        using var transport = new PawnIoCurveOptimizerTransport((command, argument) =>
        {
            int core = (int)(argument >> 20) & 7;
            if (command == 0xD5) return unchecked((uint)values[core]);
            values[core] = unchecked((short)argument);
            if (++writes == 2 && failSecond) throw new IOException("partial write");
            return argument;
        });
        foreach (int invalid in new[] { -51, 1 })
        {
            Assert.IsFalse(CurveOptimizerController.Validate(invalid, -50, 0).IsValid);
            Assert.Throws<ArgumentOutOfRangeException>(() => RyzenSmuProtocol.EncodeCurve(invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => transport.WritePerCore(new Dictionary<int, int> { [0] = invalid }, CancellationToken.None));
        }
        Assert.AreEqual(0, writes);
        Assert.IsTrue(CurveOptimizerController.Validate(-50, -50, 0).IsValid);
        Assert.AreEqual(0xFFFCEu, RyzenSmuProtocol.EncodeCurve(-50));
        Assert.AreEqual(0x70FFCEu, RyzenSmuProtocol.EncodePerCoreCurve(7, -50));
        transport.WritePerCore(new Dictionary<int, int> { [0] = -49 }, CancellationToken.None);
        transport.RestorePerCore(Enumerable.Range(0, 8).ToDictionary(core => core, _ => -50), CancellationToken.None);
        Assert.IsTrue(values.Values.All(value => value == -50));
        writes = 0;
        failSecond = true;
        var error = Assert.Throws<IOException>(() => transport.WritePerCore(new Dictionary<int, int> { [0] = -49, [1] = -48 }, CancellationToken.None));
        Assert.AreEqual(false, error.Data["curveOptimizerRollbackFailed"]);
        Assert.IsTrue(values.Values.All(value => value == -50));
    }
}
