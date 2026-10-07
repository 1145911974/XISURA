using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class PawnIoDependencyProbeTests
{
    [TestMethod]
    public void Driver_store_metadata_produces_verified_dependency()
    {
        var dependency = PawnIoDependencyProbe.CreateVerifiedDependency(
            @"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_test\PawnIO.sys",
            "2.2.0.0",
            "namazso",
            "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940");

        Assert.AreEqual(
            new VerifiedDependency(
                "PawnIO",
                "2.2.0.0",
                "namazso",
                "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940"),
            dependency);
    }

    [TestMethod]
    public void Missing_driver_evidence_is_not_verified()
    {
        var dependency = PawnIoDependencyProbe.CreateVerifiedDependency(
            null,
            "2.2.0.0",
            "namazso",
            "hash");

        Assert.IsNull(dependency);

        foreach (int coreCount in new[] { 4, 16 })
        {
            var offsets = Enumerable.Range(0, coreCount).ToDictionary(core => core, _ => -10);
            using var variableCpu = new PawnIoCurveOptimizerTransport((command, argument) =>
            {
                int core = (int)(argument >> 20);
                if (command == 0x06) offsets[core] = unchecked((short)argument);
                return unchecked((uint)offsets[core]);
            }, coreCount);
            var snapshot = variableCpu.ReadPerCore(CancellationToken.None);
            variableCpu.WriteAsync(-30, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsTrue(offsets.Values.All(value => value == -30));
            variableCpu.RestorePerCore(snapshot, CancellationToken.None);
            Assert.IsTrue(offsets.Values.All(value => value == -10));
        }

        var smu = new CurveMailbox();
        using var curve = new PawnIoCurveOptimizerTransport(smu.Execute);
        var original = curve.ReadPerCore(CancellationToken.None);
        curve.WritePerCore(new Dictionary<int, int> { [0] = -29 }, CancellationToken.None);
        Assert.AreEqual(-29, curve.ReadPerCore(CancellationToken.None)[0]);
        Assert.AreEqual(original[1], smu.Values[1]);
        curve.RestorePerCore(original, CancellationToken.None);
        CollectionAssert.AreEqual(original.Values.ToArray(), smu.Values);

        foreach (bool failRecovery in new[] { false, true })
        {
            var failing = new CurveMailbox { FailSecondWrite = true, FailRecovery = failRecovery };
            using var transport = new PawnIoCurveOptimizerTransport(failing.Execute);
            var captured = transport.ReadPerCore(CancellationToken.None).Values.ToArray();
            try
            {
                transport.WritePerCore(new Dictionary<int, int> { [0] = -29, [1] = -19 }, CancellationToken.None);
                Assert.Fail("A partially applied SMU failure must not report success.");
            }
            catch (Exception error) when (error is not AssertFailedException)
            {
                Assert.AreEqual(failRecovery, error.Data["curveOptimizerRollbackFailed"]);
                if (!failRecovery) CollectionAssert.AreEqual(captured, failing.Values);
            }
        }

        using var canceled = new CancellationTokenSource();
        var interrupted = new CurveMailbox { Cancel = canceled };
        using var cancellationTransport = new PawnIoCurveOptimizerTransport(interrupted.Execute);
        var beforeCancellation = interrupted.Values.ToArray();
        Assert.Throws<OperationCanceledException>(() => cancellationTransport.WritePerCore(
            new Dictionary<int, int> { [0] = -29, [1] = -19 }, canceled.Token));
        CollectionAssert.AreEqual(beforeCancellation, interrupted.Values);
    }

    private sealed class CurveMailbox
    {
        public int[] Values { get; } = [-30, -20, -10, 0, 1, 2, 3, 4];
        private int writes;
        public bool FailSecondWrite { get; init; }
        public bool FailRecovery { get; init; }
        public CancellationTokenSource? Cancel { get; init; }
        public uint Execute(uint command, uint argument)
        {
            int core = (int)(argument >> 20) & 7;
            if (command == 0xD5) return unchecked((uint)Values[core]);
            Assert.AreEqual(0x06u, command);
            writes++;
            Values[core] = unchecked((short)argument);
            if (writes == 1) Cancel?.Cancel();
            if (FailSecondWrite && (writes == 2 || FailRecovery && writes > 2)) throw new IOException("Simulated partial SMU write.");
            return argument;
        }
    }

    [TestMethod]
    [TestCategory("CurrentMachine")]
    public void Current_machine_pawnio_installation_matches_the_pinned_dependency()
    {
        var dependency = PawnIoDependencyProbe.ReadInstalled();

        Assert.IsNotNull(dependency);
        Assert.AreEqual("PawnIO", dependency.Name);
        Assert.AreEqual("2.2.0.0", dependency.Version);
        Assert.AreEqual("namazso", dependency.Publisher);
        Assert.AreEqual(
            "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940",
            dependency.Sha256);
    }
}
