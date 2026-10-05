using Jiaolong.Hardware.Abstractions;

namespace Jiaolong.Hardware.Mechrevo.Safety;

public sealed record ConflictCandidate(string Basename, DateTimeOffset ObservedAtUtc);

public interface IAllowlistedConflictSignal
{
    Task<IReadOnlyList<ConflictCandidate>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class ConflictDetector : IConflictDetector
{
    private readonly IReadOnlySet<string> allowlistedBasenames;
    private readonly IAllowlistedConflictSignal? signal;

    public ConflictDetector(IEnumerable<string>? allowlistedBasenames = null, IAllowlistedConflictSignal? signal = null)
    {
        this.allowlistedBasenames = new HashSet<string>(
            allowlistedBasenames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        this.signal = signal;
    }

    public async Task<ConflictState> DetectAsync(CancellationToken cancellationToken)
    {
        if (signal is null) return new ConflictState(false, Array.Empty<string>());

        IReadOnlyList<ConflictCandidate> candidates;
        try
        {
            candidates = await signal.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ConflictState(true, ["conflictSignalUnavailable"]);
        }

        var sources = candidates
            .Where(candidate => IsBasename(candidate.Basename) && allowlistedBasenames.Contains(candidate.Basename))
            .Select(candidate => $"{candidate.Basename}@{candidate.ObservedAtUtc:O}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ConflictState(sources.Length > 0, sources);
    }

    private static bool IsBasename(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('\\') &&
        !value.Contains('/') &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
}
