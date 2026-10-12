namespace Jiaolong_ControlCenter.Services;

public sealed record AdaptiveStrategyAppearance(string Name, string Note, string Icon)
{
    public static readonly string[] Icons = ["StrategyQuiet", "StrategyBalanced", "StrategyResponse", "TargetOffice", "TargetGame", "TargetTurbo", "DecisionCpu", "DecisionGpu", "DecisionThermal", "RulesDocument"];
    public static AdaptiveStrategyAppearance Default(AdaptiveStrategyId strategy) => strategy switch
    {
        AdaptiveStrategyId.QuietFirst => new("安静优先", "优先控制噪声，适合日常办公与轻度使用", "StrategyQuiet"),
        AdaptiveStrategyId.ResponseFirst => new("响应优先", "更快切换到高性能，适合游戏与高负载场景", "StrategyResponse"),
        _ => new("均衡自适应", "根据负载与应用状态智能切换，平衡体验", "StrategyBalanced")
    };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 12 || Name.Any(char.IsControl)
            || Note is null || Note.Length > 60 || Note.Any(char.IsControl) || !Icons.Contains(Icon))
            throw new ArgumentException("昵称为1–12字，备注最多60字，请选择内置图标。");
    }
}

public sealed record AdaptiveApplicationRule(string Executable, bool ForegroundOnly, AdaptiveStage Target)
{
    public bool RequireActivity { get; init; } = true;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable) || Executable.Length > 120 || !Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Executable.IndexOfAny(['/', '\\', '*', '?', ':', '"', '<', '>', '|']) >= 0 || Executable.Any(char.IsControl) || !Enum.IsDefined(Target))
            throw new ArgumentException("应用请输入准确的进程名，例如 ffmpeg.exe；不接受路径、通配符或命令。");
    }
}

public sealed record AdaptiveAdvancedSettings
{
    public int LowBatteryPercent { get; init; } = 20;
    public int BatteryRecoveryPercent { get; init; } = 30;
    public bool RespectBatterySaver { get; init; } = true;
    public int CpuTemperatureCeiling { get; init; } = 90;
    public int GpuTemperatureCeiling { get; init; } = 82;
    public int CooldownSeconds { get; init; } = 4;
    public int MinimumDwellSeconds { get; init; } = 8;
    public int ApplicationSeconds { get; init; } = 3;
    public bool IdleReturnEnabled { get; init; }
    public int IdleSeconds { get; init; } = 300;
    public AdaptiveApplicationRule[] ApplicationRules { get; init; } = [];
    public void Validate()
    {
        if (LowBatteryPercent is < 5 or > 80 || BatteryRecoveryPercent <= LowBatteryPercent || BatteryRecoveryPercent > 100
            || CpuTemperatureCeiling is < 50 or > 95 || GpuTemperatureCeiling is < 45 or > 87
            || CooldownSeconds is < 1 or > 600 || MinimumDwellSeconds is < 1 or > 3600
            || ApplicationSeconds is < 1 or > 300 || IdleSeconds is < 30 or > 3600 || ApplicationRules is null || ApplicationRules.Length > 8)
            throw new ArgumentException("请检查电量恢复阈值、温度和时间范围；最多8条应用规则。");
        foreach (var rule in ApplicationRules) { if (rule is null) throw new ArgumentException("应用规则不能为空。"); rule.Validate(); }
        if (ApplicationRules.Select(r => r.Executable).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ApplicationRules.Length)
            throw new ArgumentException("同一进程只保留一条规则，避免互相冲突。");
    }
}

public sealed record AdaptiveTrialInput
{
    public double? CpuPercent { get; init; }
    public double? GpuPercent { get; init; }
    public DateTimeOffset? CpuObservedAtUtc { get; init; }
    public DateTimeOffset? GpuObservedAtUtc { get; init; }
    public double? CpuTemperature { get; init; }
    public double? GpuTemperature { get; init; }
    public DateTimeOffset? CpuTemperatureObservedAtUtc { get; init; }
    public DateTimeOffset? GpuTemperatureObservedAtUtc { get; init; }
    public bool? AcConnected { get; init; }
    public int? BatteryPercent { get; init; }
    public bool? BatterySaver { get; init; }
    public bool LowBatteryLatched { get; init; }
    public string Executable { get; init; } = "";
    public bool Foreground { get; init; }
    public int ConditionSeconds { get; init; }
    public int SinceSwitchSeconds { get; init; }
    public int IdleSeconds { get; init; }
    public AdaptiveStage Current { get; init; } = AdaptiveStage.Office;
    public bool ReapplyCurrentTarget { get; init; }
    public bool ApplicationContextAvailable { get; init; } = true;
    public bool IdleContextAvailable { get; init; } = true;
    public double? ProcessCpuPercent { get; init; }
    public double? ProcessGpuPercent { get; init; }
    public double CpuEmergencyTemperatureC { get; init; } = 95;
    public double GpuEmergencyTemperatureC { get; init; } = 87;
    public bool? HardwareThermalThrottling { get; init; }
    public bool ConfigurationChanged { get; init; }
}

public sealed record AdaptiveTrialResult(AdaptiveStage? Target, string Reason);

// Explicit scenario history: replay synthetic fresh samples through the runtime rule engine.
public static class AdaptiveRuleTrial
{
    public static AdaptiveTrialResult Evaluate(AdaptiveTriggerPolicy policy, AdaptiveTrialInput input) =>
        AdaptiveFilteredSession.EvaluateScenario(policy, input);
}

public sealed class AdaptiveRuleSession
{
    private readonly AdaptiveFilteredSession session = new();
    public AdaptiveStage? CandidateStage => session.CandidateStage;
    public AdaptiveFilterEvidence Evidence => session.Evidence;
    public bool RequiresEcRelease => session.RequiresEcRelease;
    public AdaptiveTrialResult Evaluate(AdaptiveTriggerPolicy policy, AdaptiveTrialInput input, DateTimeOffset observedAtUtc) =>
        session.Evaluate(policy, input, observedAtUtc, (observedAtUtc - DateTimeOffset.UnixEpoch).TotalSeconds);
    public AdaptiveTrialResult Evaluate(AdaptiveTriggerPolicy policy, AdaptiveTrialInput input, DateTimeOffset observedAtUtc, TimeSpan monotonicElapsed) =>
        session.Evaluate(policy, input, observedAtUtc, monotonicElapsed.TotalSeconds);
    public void Reset() => session.Reset();
}
