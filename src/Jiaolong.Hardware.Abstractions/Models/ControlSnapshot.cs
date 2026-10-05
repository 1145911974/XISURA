namespace Jiaolong.Hardware.Abstractions.Models;

public sealed record ControlSnapshot(
    ControlKey Key,
    DateTimeOffset CapturedAtUtc,
    double? NumericValue,
    bool? BooleanValue,
    string? TextValue,
    bool IsAvailable,
    string? UnavailableReason);
