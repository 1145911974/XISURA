using System.Text.Json.Serialization;
using Jiaolong.Contracts.Commands;

namespace Jiaolong.Contracts.Models;

[JsonConverter(typeof(LowerCamelEnumConverter<PerformanceMode>))]
public enum PerformanceMode
{
    Quiet,
    Balanced,
    Turbo,
    Custom
}

[JsonConverter(typeof(LowerCamelEnumConverter<MuxMode>))]
public enum MuxMode
{
    Hybrid,
    Discrete,
    Integrated
}

[JsonConverter(typeof(LowerCamelEnumConverter<ReleaseReason>))]
public enum ReleaseReason
{
    UserRequested,
    SessionEnded,
    SafetyFallback,
    SensorStale,
    ThermalEmergency,
    ServiceStopping,
    SystemSuspend
}

[JsonConverter(typeof(LowerCamelEnumConverter<QuickSettingKind>))]
public enum QuickSettingKind
{
    FnLock,
    Fn,
    Touchpad,
    NumLock,
    CapsLock,
    Osd,
    Wifi,
    Bluetooth,
    TouchpadLock,
    WinKey,
    LidLogo,
    StrongCooling,
    DisplayOff
}

public sealed record FanPoint(int TemperatureC, int Percent);

public sealed record FanControlPlan(FanPoint[] Points)
{
    public FanPoint[]? GpuPoints { get; init; }
    public string Strategy { get; init; } = "Curve";
    public int? FixedRpm { get; init; }
    public int? MaximumRpm { get; init; }
}
