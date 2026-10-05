namespace Jiaolong.Contracts.Errors;

public sealed record ServiceError(
    ErrorCode Code,
    string MessageKey,
    bool IsRetryable,
    Guid CorrelationId,
    Dictionary<string, string> Details)
{
    public static ServiceError Create(ErrorCode code, Guid correlationId, bool isRetryable) =>
        new(code, code.ToWireValue(), isRetryable, correlationId, new Dictionary<string, string>());
}
