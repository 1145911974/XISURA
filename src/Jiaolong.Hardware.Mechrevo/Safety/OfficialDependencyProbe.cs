using Jiaolong.Hardware.Abstractions;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Mechrevo.Safety;

public sealed record DependencyObservation(bool IsPresent, string Version, string Publisher, string Sha256);

public sealed record DependencyProbeResult(
    bool IsHealthy,
    IReadOnlyList<VerifiedDependency> VerifiedDependencies,
    IReadOnlyList<string> UnavailableReasons);

public sealed class OfficialDependencyProbe
{
    private readonly IReadOnlyList<VerifiedDependency> expected;
    private readonly Func<VerifiedDependency, CancellationToken, Task<DependencyObservation>>? observer;

    public OfficialDependencyProbe(
        IEnumerable<VerifiedDependency> expected,
        Func<VerifiedDependency, CancellationToken, Task<DependencyObservation>>? observer = null)
    {
        this.expected = expected.ToArray();
        this.observer = observer;
    }

    public async Task<DependencyProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var verified = new List<VerifiedDependency>();
        var reasons = new List<string>();
        foreach (var dependency in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observer is null)
            {
                reasons.Add($"{dependency.Name}:probeUnavailable");
                continue;
            }

            DependencyObservation observation;
            try
            {
                observation = await observer(dependency, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                reasons.Add($"{dependency.Name}:probeFailed");
                continue;
            }

            if (observation.IsPresent &&
                string.Equals(observation.Version, dependency.Version, StringComparison.Ordinal) &&
                string.Equals(observation.Publisher, dependency.Publisher, StringComparison.Ordinal) &&
                string.Equals(observation.Sha256, dependency.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                verified.Add(dependency);
            }
            else
            {
                reasons.Add($"{dependency.Name}:evidenceMismatch");
            }
        }

        return new DependencyProbeResult(reasons.Count == 0, verified, reasons);
    }

    public async Task<IReadOnlyList<VerifiedDependency>> GetVerifiedDependenciesAsync(CancellationToken cancellationToken)
    {
        var result = await ProbeAsync(cancellationToken);
        return result.VerifiedDependencies;
    }
}
