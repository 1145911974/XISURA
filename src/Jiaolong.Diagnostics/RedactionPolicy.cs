using System.Text.RegularExpressions;

namespace Jiaolong.Diagnostics;

public static partial class RedactionPolicy
{
    private static readonly string[] SensitiveFieldNames =
    [
        "user",
        "username",
        "sid",
        "token",
        "password",
        "secret",
        "commandPayload",
        "applicationList",
        "networkInfo",
        "fullPath"
    ];

    public static string RedactText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = SidPattern().Replace(value, "[redacted-sid]");
        value = UserPathPattern().Replace(value, "[redacted-path]");
        value = value.Replace("secret-token", "[redacted]", StringComparison.OrdinalIgnoreCase);
        value = SecretPattern().Replace(value, "$1[redacted]");
        return UsernamePattern().Replace(value, "[redacted-user]");
    }

    public static DiagnosticEvent RedactEvent(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in diagnosticEvent.Fields)
        {
            if (SensitiveFieldNames.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            fields[field.Key] = RedactText(field.Value);
        }

        return new DiagnosticEvent
        {
            TimestampUtc = diagnosticEvent.TimestampUtc,
            EventName = diagnosticEvent.EventName,
            Level = diagnosticEvent.Level,
            CorrelationId = diagnosticEvent.CorrelationId,
            OperationId = diagnosticEvent.OperationId,
            Operation = RedactText(diagnosticEvent.Operation ?? string.Empty),
            DurationMs = diagnosticEvent.DurationMs,
            ErrorCode = diagnosticEvent.ErrorCode,
            ManifestId = diagnosticEvent.ManifestId,
            BiosVersion = diagnosticEvent.BiosVersion,
            ServiceVersion = diagnosticEvent.ServiceVersion,
            AppVersion = diagnosticEvent.AppVersion,
            Fields = fields
        };
    }

    [GeneratedRegex(@"(?i)S-1-5-21-(?:-?\d+){3,4}", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    [GeneratedRegex(@"(?i)C:\\Users\\[^""\r\n ]*", RegexOptions.CultureInvariant)]
    private static partial Regex UserPathPattern();

    [GeneratedRegex(@"(?i)(secret-token|(?:token|password|secret)\s*[:=]\s*)\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?i)\bAdministrator\b", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
