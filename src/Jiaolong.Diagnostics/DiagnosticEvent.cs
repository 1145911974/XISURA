using System.Text.Json.Serialization;

namespace Jiaolong.Diagnostics;

public interface IDiagnosticEventWriter
{
    ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken);
}

public interface IDiagnosticEventReader
{
    Task<string> ReadRecentAsync(int maximumEntries, CancellationToken cancellationToken);
}

public sealed class DiagnosticEvent
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string EventName { get; init; } = string.Empty;
    public string Level { get; init; } = "info";
    public Guid CorrelationId { get; init; }
    public Guid OperationId { get; init; }
    public string? Operation { get; init; }
    public int? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ManifestId { get; init; }
    public string? BiosVersion { get; init; }
    public string? ServiceVersion { get; init; }
    public string? AppVersion { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

public static class DiagnosticEventNames
{
    public const string ServiceStarted = "service.started";
    public const string ServiceStopping = "service.stopping";
    public const string ServiceRecovered = "service.recovered";
    public const string IpcClientAccepted = "ipc.clientAccepted";
    public const string IpcClientRejected = "ipc.clientRejected";
    public const string IpcInvalidFrame = "ipc.invalidFrame";
    public const string FingerprintObserved = "fingerprint.observed";
    public const string CompatibilityEvaluated = "compatibility.evaluated";
    public const string DependencyUnhealthy = "dependency.unhealthy";
    public const string CommandReceived = "command.received";
    public const string CommandValidated = "command.validated";
    public const string CommandApplying = "command.applying";
    public const string CommandApplied = "command.applied";
    public const string CommandRejected = "command.rejected";
    public const string CommandReadbackMismatch = "command.readbackMismatch";
    public const string CommandRollbackSucceeded = "command.rollbackSucceeded";
    public const string CommandRollbackFailed = "command.rollbackFailed";
    public const string CircuitOpened = "circuit.opened";
    public const string CircuitClosed = "circuit.closed";
    public const string FanReleased = "fan.released";
    public const string AutomationTransition = "automation.transition";
    public const string ConflictDetected = "conflict.detected";
    public const string DiagnosticsExported = "diagnostics.exported";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ServiceStarted,
        ServiceStopping,
        ServiceRecovered,
        IpcClientAccepted,
        IpcClientRejected,
        IpcInvalidFrame,
        FingerprintObserved,
        CompatibilityEvaluated,
        DependencyUnhealthy,
        CommandReceived,
        CommandValidated,
        CommandApplying,
        CommandApplied,
        CommandRejected,
        CommandReadbackMismatch,
        CommandRollbackSucceeded,
        CommandRollbackFailed,
        CircuitOpened,
        CircuitClosed,
        FanReleased,
        AutomationTransition,
        ConflictDetected,
        DiagnosticsExported
    };
}
