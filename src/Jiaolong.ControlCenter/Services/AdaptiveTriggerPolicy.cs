namespace Jiaolong_ControlCenter.Services;

// Candidate values are local preview settings, not verified hardware control limits.
public sealed record AdaptiveTriggerPolicy(
    int GameCpuPercent,
    int GameGpuPercent,
    int GameSeconds,
    bool TurboEnabled,
    int TurboCpuPercent,
    int TurboGpuPercent,
    int TurboSeconds,
    int OfficeCpuPercent,
    int OfficeGpuPercent,
    int OfficeSeconds)
{
    public AdaptiveAdvancedSettings Advanced { get; init; } = new();
    public static AdaptiveTriggerPolicy Recommended(AdaptiveStrategyId strategy) => strategy switch
    {
        AdaptiveStrategyId.QuietFirst => new(55, 45, 15, false, 85, 90, 30, 20, 15, 90),
        AdaptiveStrategyId.BalancedAdaptive => new(40, 35, 8, true, 85, 90, 30, 20, 15, 120),
        AdaptiveStrategyId.ResponseFirst => new(30, 25, 4, true, 75, 85, 15, 15, 10, 180),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy))
    };

    public void Validate()
    {
        if (Advanced is null) throw new ArgumentException("高级条件不能为空。");
        Advanced.Validate();
        foreach (var percent in new[] { GameCpuPercent, GameGpuPercent, TurboCpuPercent, TurboGpuPercent, OfficeCpuPercent, OfficeGpuPercent })
            if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percent), "负载阈值必须在 1% 至 100% 之间。");

        foreach (var seconds in new[] { GameSeconds, TurboSeconds, OfficeSeconds })
            if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds), "持续时间必须在 1 至 3600 秒之间。");

        if (OfficeCpuPercent >= GameCpuPercent || OfficeGpuPercent >= GameGpuPercent)
            throw new ArgumentOutOfRangeException(nameof(OfficeCpuPercent), "降档阈值必须低于进入游戏阈值。");

        if (TurboEnabled && (TurboCpuPercent <= GameCpuPercent || TurboGpuPercent <= GameGpuPercent))
            throw new ArgumentOutOfRangeException(nameof(TurboCpuPercent), "狂飙阈值必须高于进入游戏阈值。");
    }
}
