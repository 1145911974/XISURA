using System.Security.Cryptography;
using System.Text;

namespace Jiaolong_ControlCenter.Services;

public sealed record ForegroundAppObservation(
    string ProcessName,
    string ExecutableHash,
    DateTimeOffset ObservedAtUtc,
    int SessionId);

public sealed class ForegroundAppMonitor
{
    private readonly object gate = new();
    private DateTimeOffset lastObservedAtUtc = DateTimeOffset.MinValue;

    public Task<ForegroundAppObservation> ObserveAsync(string executablePath, int sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("Executable path is required.", nameof(executablePath));
        if (sessionId < 0) throw new ArgumentOutOfRangeException(nameof(sessionId));

        var processName = Path.GetFileName(executablePath);
        if (string.IsNullOrWhiteSpace(processName)) throw new ArgumentException("Executable basename is unavailable.", nameof(executablePath));
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (now - lastObservedAtUtc < TimeSpan.FromMilliseconds(500))
            {
                throw new InvalidOperationException("Foreground observation rate limit exceeded.");
            }
            lastObservedAtUtc = now;
        }

        var identity = Encoding.UTF8.GetBytes($"{processName.ToLowerInvariant()}|{sessionId}");
        var hash = Convert.ToHexString(SHA256.HashData(identity));
        return Task.FromResult(new ForegroundAppObservation(processName, hash, now, sessionId));
    }
}
