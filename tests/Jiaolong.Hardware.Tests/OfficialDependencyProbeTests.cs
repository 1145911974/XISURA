using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Hardware.Mechrevo.Safety;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class OfficialDependencyProbeTests
{
    [TestMethod]
    public async Task Only_exact_publisher_version_and_hash_are_verified()
    {
        var expected = new VerifiedDependency("JiaolongDriver", "1.2.3.4", "Jiaolong", "ABC123");
        var probe = new OfficialDependencyProbe(
            [expected],
            (_, _) => Task.FromResult(new DependencyObservation(true, "1.2.3.4", "Other", "ABC123")));

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.IsFalse(result.IsHealthy);
        Assert.AreEqual(0, result.VerifiedDependencies.Count);
    }

    [TestMethod]
    public async Task Conflict_detector_ignores_non_allowlisted_paths_and_reports_known_basename()
    {
        var detector = new ConflictDetector(
            ["ControlCenter.exe"],
            new FixedConflictSignal(
                [
                    new ConflictCandidate("C:\\Temp\\ControlCenter.exe", DateTimeOffset.UtcNow),
                    new ConflictCandidate("ControlCenter.exe", DateTimeOffset.UtcNow)
                ]));

        var result = await detector.DetectAsync(CancellationToken.None);

        Assert.IsTrue(result.HasConflict);
        Assert.AreEqual(1, result.Sources.Count);
        StringAssert.Contains(result.Sources[0], "ControlCenter.exe");
    }

    private sealed class FixedConflictSignal(IReadOnlyList<ConflictCandidate> candidates) : IAllowlistedConflictSignal
    {
        public Task<IReadOnlyList<ConflictCandidate>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(candidates);
    }
}
