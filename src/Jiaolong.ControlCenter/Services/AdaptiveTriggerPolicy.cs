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
    public int TurboExitCpuPercent { get; init; } = 70;
    public int TurboExitGpuPercent { get; init; } = 75;
    public int TurboExitSeconds { get; init; } = 12;
    public int TurboMinimumDwellSeconds { get; init; } = 12;
    public static AdaptiveTriggerPolicy Recommended(AdaptiveStrategyId strategy) => strategy switch
    {
        AdaptiveStrategyId.QuietFirst => new(60, 45, 6, false, 90, 92, 25, 20, 15, 15) { TurboExitSeconds = 3 },
        AdaptiveStrategyId.BalancedAdaptive => new(45, 35, 3, true, 90, 92, 25, 20, 15, 20),
        AdaptiveStrategyId.ResponseFirst => new(30, 25, 2, true, 85, 85, 12, 15, 10, 30)
            { TurboExitCpuPercent = 65, TurboExitGpuPercent = 70, TurboExitSeconds = 20 },
        _ => throw new ArgumentOutOfRangeException(nameof(strategy))
    };

    public static AdaptiveTriggerPolicy UpgradeRecommended(AdaptiveStrategyId strategy, AdaptiveTriggerPolicy saved)
    {
        var recommended = Recommended(strategy);
        int legacyOfficeSeconds = strategy == AdaptiveStrategyId.QuietFirst ? 90 : strategy == AdaptiveStrategyId.BalancedAdaptive ? 120 : 180;
        int office = saved.OfficeSeconds == legacyOfficeSeconds ? recommended.OfficeSeconds : saved.OfficeSeconds;
        var advanced = saved.Advanced with
        {
            CooldownSeconds = saved.Advanced.CooldownSeconds == 30 ? recommended.Advanced.CooldownSeconds : saved.Advanced.CooldownSeconds,
            MinimumDwellSeconds = saved.Advanced.MinimumDwellSeconds == 90 ? recommended.Advanced.MinimumDwellSeconds : saved.Advanced.MinimumDwellSeconds
        };
        // Old default waits also occur in strategies with customized load thresholds.
        return office == saved.OfficeSeconds && advanced == saved.Advanced ? saved :
            saved with { OfficeSeconds = office, Advanced = advanced };
    }

    public void Validate()
    {
        if (Advanced is null) throw new ArgumentException("高级条件不能为空。");
        Advanced.Validate();
        if (TurboExitCpuPercent is < 1 or > 100 || TurboExitGpuPercent is < 1 or > 100 ||
            TurboExitSeconds is < 1 or > 3600 || TurboMinimumDwellSeconds is < 1 or > 3600)
            throw new ArgumentException("狂飙退出阈值和驻留时间无效。");
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
