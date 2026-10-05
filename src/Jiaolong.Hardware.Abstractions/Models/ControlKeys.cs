namespace Jiaolong.Hardware.Abstractions.Models;

public readonly record struct ControlKey(string Value)
{
    public bool IsKnown => ControlKeys.All.Contains(Value, StringComparer.Ordinal);

    public override string ToString() => Value;
}

public static class ControlKeys
{
    public const string PerformanceMode = "performanceMode";
    public const string MuxMode = "muxMode";
    public const string FanControl = "fanControl";
    public const string KeyboardLighting = "keyboardLighting";
    public const string LidLogo = "lidLogo";
    public const string CpuTuning = "cpuTuning";
    public const string GpuFrequencyLimit = "gpuFrequencyLimit";

    public static IReadOnlyList<string> All { get; } =
    [
        PerformanceMode,
        MuxMode,
        FanControl,
        KeyboardLighting,
        LidLogo,
        CpuTuning,
        GpuFrequencyLimit
    ];
}

public sealed record ValidatedHardwareWrite(
    Guid OperationId,
    ControlKey Key,
    double? NumericValue,
    bool? BooleanValue,
    string? TextValue)
{
    public bool IsWellFormed =>
        OperationId != Guid.Empty &&
        Key.IsKnown &&
        (NumericValue.HasValue ^ BooleanValue.HasValue ^ TextValue is not null);
}
