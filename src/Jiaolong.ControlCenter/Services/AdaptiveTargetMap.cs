using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public enum AdaptivePowerSource { Ac, Dc }
public enum AdaptiveStage { Office, Game, Turbo }

public sealed record AdaptiveTargetMap(
    PresetKey AcOffice,
    PresetKey AcGame,
    PresetKey AcTurbo,
    PresetKey DcOffice,
    PresetKey DcGame)
{
    public static AdaptiveTargetMap Recommended { get; } = new(
        PresetKey.Create(ControlModeId.Office, 2),
        PresetKey.Create(ControlModeId.Gaming, 2),
        PresetKey.Create(ControlModeId.Turbo, 2),
        PresetKey.Create(ControlModeId.Office, 1),
        PresetKey.Create(ControlModeId.Gaming, 2));

    public AdaptiveTargetMap WithTarget(AdaptivePowerSource power, AdaptiveStage stage, PresetKey key)
    {
        PresetKey.Create(key.Mode, key.Slot);
        if (power == AdaptivePowerSource.Dc && HardwareModeFor(key) == PerformanceMode.Turbo)
            throw new ArgumentOutOfRangeException(nameof(key), "电池供电不可指向狂飙或自定义预设的原生狂飙模式。");
        return (power, stage) switch
        {
            (AdaptivePowerSource.Ac, AdaptiveStage.Office) => this with { AcOffice = key },
            (AdaptivePowerSource.Ac, AdaptiveStage.Game) => this with { AcGame = key },
            (AdaptivePowerSource.Ac, AdaptiveStage.Turbo) => this with { AcTurbo = key },
            (AdaptivePowerSource.Dc, AdaptiveStage.Office) => this with { DcOffice = key },
            (AdaptivePowerSource.Dc, AdaptiveStage.Game) => this with { DcGame = key },
            _ => throw new ArgumentOutOfRangeException(nameof(stage), "电池供电不允许自动进入狂飙。")
        };
    }

    public PresetKey GetTarget(AdaptivePowerSource power, AdaptiveStage stage) => (power, stage) switch
    {
        (AdaptivePowerSource.Ac, AdaptiveStage.Office) => AcOffice,
        (AdaptivePowerSource.Ac, AdaptiveStage.Game) => AcGame,
        (AdaptivePowerSource.Ac, AdaptiveStage.Turbo) => AcTurbo,
        (AdaptivePowerSource.Dc, AdaptiveStage.Office) => DcOffice,
        (AdaptivePowerSource.Dc, AdaptiveStage.Game) => DcGame,
        (AdaptivePowerSource.Dc, AdaptiveStage.Turbo) => throw new ArgumentOutOfRangeException(nameof(stage), "电池供电不可自动进入狂飙。"),
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    public static PerformanceMode PerformanceModeFor(PresetKey target)
    {
        PresetKey.Create(target.Mode, target.Slot);
        return target.Mode switch
        {
            ControlModeId.Office => PerformanceMode.Quiet,
            ControlModeId.Gaming => PerformanceMode.Balanced,
            ControlModeId.Turbo => PerformanceMode.Turbo,
            ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3 => PerformanceMode.Custom,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }

    public static PerformanceMode HardwareModeFor(PresetKey target)
    {
        var mode = PerformanceModeFor(target);
        return mode == PerformanceMode.Custom ? PerformanceMode.Turbo : mode;
    }
}
