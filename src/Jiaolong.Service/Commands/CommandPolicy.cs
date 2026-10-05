using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;

namespace Jiaolong.Service.Commands;

public sealed record CommandContext(
    bool ClientAuthorized,
    bool CompatibilityWritable,
    bool NoConflict,
    bool DependencyHealthy,
    bool IsAc,
    DateTimeOffset DeadlineUtc,
    Guid CorrelationId)
{
    public static CommandContext DevelopmentWritable => new(
        true,
        true,
        true,
        true,
        true,
        DateTimeOffset.UtcNow.AddMinutes(1),
        Guid.NewGuid());
}

public static class CommandPolicy
{
    public static ServiceError? Validate(HardwareCommand command, CommandContext context, DateTimeOffset now)
    {
        if (!context.ClientAuthorized) return ServiceError.Create(ErrorCode.UnauthorizedClient, context.CorrelationId, false);
        if (!context.CompatibilityWritable) return ServiceError.Create(ErrorCode.ReadOnlySafeMode, context.CorrelationId, false);
        if (!context.NoConflict) return ServiceError.Create(ErrorCode.ConflictDetected, context.CorrelationId, false);
        if (!context.DependencyHealthy) return ServiceError.Create(ErrorCode.DependencyMissing, context.CorrelationId, false);
        if (!context.IsAc) return ServiceError.Create(ErrorCode.ValidationFailed, context.CorrelationId, false);
        if (context.DeadlineUtc <= now) return ServiceError.Create(ErrorCode.DeadlineExceeded, context.CorrelationId, false);
        return CommandValidation.Validate(command);
    }
}
