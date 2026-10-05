using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Abstractions;

public class HardwareOperationException(
    ControlKey key,
    string outcome,
    object? requestedValue,
    object? readBackValue,
    bool rollbackSucceeded)
    : InvalidOperationException($"Hardware operation '{outcome}' for '{key.Value}'.")
{
    public ControlKey Key { get; } = key;
    public string Outcome { get; } = outcome;
    public object? RequestedValue { get; } = requestedValue;
    public object? ReadBackValue { get; } = readBackValue;
    public bool RollbackSucceeded { get; } = rollbackSucceeded;
}
