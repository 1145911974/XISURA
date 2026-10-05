namespace Jiaolong.Hardware.Abstractions;

public interface IConflictDetector
{
    Task<ConflictState> DetectAsync(CancellationToken cancellationToken);
}

public sealed record ConflictState(bool HasConflict, IReadOnlyList<string> Sources);
