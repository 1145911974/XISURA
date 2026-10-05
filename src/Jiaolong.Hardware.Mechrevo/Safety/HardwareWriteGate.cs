using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Abstractions.Compatibility;

namespace Jiaolong.Hardware.Mechrevo.Safety;

public sealed record SystemState(bool HasConflict, bool HealthyDependency);

public sealed record ValidationResult(bool IsValid, string? Reason)
{
    public static ValidationResult Allow() => new(true, null);
    public static ValidationResult Reject(string reason) => new(false, reason);
}

public interface IHardwareWriteGate
{
    ValidationResult Validate(HardwareCommand command, CompatibilityDecision compatibility, SystemState systemState);
}

public sealed class HardwareWriteGate : IHardwareWriteGate
{
    public ValidationResult Validate(
        HardwareCommand command,
        CompatibilityDecision compatibility,
        SystemState systemState)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(systemState);

        if (command.OperationId == Guid.Empty) return ValidationResult.Reject("operationIdRequired");
        if (compatibility.Mode != CompatibilityMode.Writable) return ValidationResult.Reject("readOnlySafeMode");
        if (systemState.HasConflict) return ValidationResult.Reject("conflictDetected");
        if (!systemState.HealthyDependency) return ValidationResult.Reject("dependencyMissing");
        return ValidationResult.Allow();
    }
}
