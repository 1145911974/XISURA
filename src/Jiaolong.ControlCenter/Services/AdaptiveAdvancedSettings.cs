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
    public int CpuTemperatureCeiling { get; init; } = 85;
    public int GpuTemperatureCeiling { get; init; } = 80;
    public int CooldownSeconds { get; init; } = 8;
    public int MinimumDwellSeconds { get; init; } = 20;
    public int ApplicationSeconds { get; init; } = 5;
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
    public double? CpuTemperature { get; init; }
    public double? GpuTemperature { get; init; }
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
}

public sealed record AdaptiveTrialResult(AdaptiveStage? Target, string Reason);

// Stateless scenario trial only; never sends commands or infers continuous history from a snapshot.
public static class AdaptiveRuleTrial
{
    public static AdaptiveTrialResult Evaluate(AdaptiveTriggerPolicy policy, AdaptiveTrialInput input)
    {
        policy.Validate();
        var settings = policy.Advanced;
        AdaptiveTrialResult Hold(string reason) => new(null, reason);
        if (input.AcConnected is null) return Hold("供电未知：不产生切换候选。");
        if (input.AcConnected == false)
        {
            if (input.BatteryPercent is null or < 0 or > 100) return Hold("电量未知：不产生切换候选。");
            if (input.BatteryPercent <= settings.LowBatteryPercent || input.LowBatteryLatched && input.BatteryPercent < settings.BatteryRecoveryPercent
                || settings.RespectBatterySaver && input.BatterySaver is true)
                return input.Current == AdaptiveStage.Office
                    ? Hold("电量/节能保护生效，已处于办公模式，不重复切换。")
                    : new(AdaptiveStage.Office, "电量/节能保护优先，使用电池办公目标。");
            if (settings.RespectBatterySaver && input.BatterySaver is null)
                return Hold("电池节能状态未知：不产生切换候选。");
        }
        if (input.SinceSwitchSeconds < settings.CooldownSeconds) return Hold("仍在切换冷却期。");
        var app = input.ApplicationContextAvailable ? settings.ApplicationRules.FirstOrDefault(r =>
            string.Equals(r.Executable, input.Executable.Trim(), StringComparison.OrdinalIgnoreCase) && (!r.ForegroundOnly || input.Foreground)) : null;
        AdaptiveStage? target = app?.Target;
        var duration = app is not null ? settings.ApplicationSeconds : 0;
        var reason = app is not null ? $"应用规则命中：{app.Executable}" : "持续负载规则";
        if (app is null)
        {
            var cpu = input.CpuPercent;
            var gpu = input.GpuPercent;
            if (cpu is null || gpu is null || !double.IsFinite(cpu.Value) || !double.IsFinite(gpu.Value) || cpu < 0 || cpu > 100 || gpu < 0 || gpu > 100)
                return Hold("负载数据不完整或无效。");
            if (cpu < policy.OfficeCpuPercent && gpu < policy.OfficeGpuPercent)
            {
                target = AdaptiveStage.Office; duration = policy.OfficeSeconds;
                if (settings.IdleReturnEnabled && input.IdleContextAvailable && input.IdleSeconds < settings.IdleSeconds) return Hold("低负载，但尚未满足空闲时长。");
            }
            else if (policy.TurboEnabled && (cpu >= policy.TurboCpuPercent || gpu >= policy.TurboGpuPercent)) { target = AdaptiveStage.Turbo; duration = policy.TurboSeconds; }
            else if ((cpu >= policy.GameCpuPercent || gpu >= policy.GameGpuPercent) &&
                (input.Current != AdaptiveStage.Turbo || !policy.TurboEnabled ||
                 cpu < policy.TurboCpuPercent - 10 && gpu < policy.TurboGpuPercent - 10))
            { target = AdaptiveStage.Game; duration = policy.GameSeconds; }
            else if (input.Current == AdaptiveStage.Turbo && (!policy.TurboEnabled ||
                cpu < policy.TurboCpuPercent - 10 && gpu < policy.TurboGpuPercent - 10))
            { target = AdaptiveStage.Game; duration = policy.GameSeconds; reason = "狂飙负载已持续回落"; }
        }
        if (target is null) return Hold("处于阈值滞回区，保持当前模式。");
        if (target == AdaptiveStage.Turbo && (!policy.TurboEnabled || input.AcConnected == false))
        { target = AdaptiveStage.Game; reason += "；供电/策略上限限制为游戏"; }
        // A cooling GPU can stop reporting temperature; that must never lock a higher-power mode.
        if (target >= input.Current)
        {
            if (input.CpuTemperature is null || input.GpuTemperature is null || !double.IsFinite(input.CpuTemperature.Value) || !double.IsFinite(input.GpuTemperature.Value))
                return Hold("温度数据不完整：阻止升档，需确认保护状态。");
            if (input.CpuTemperature >= settings.CpuTemperatureCeiling || input.GpuTemperature >= settings.GpuTemperatureCeiling)
                return Hold("达到温度保护门槛：否决性能升档，不试探硬件调校。");
        }
        if (target == input.Current && !input.ReapplyCurrentTarget) return Hold("已经是目标模式，不重复切换。");
        if (input.ConditionSeconds < duration) return Hold($"{reason}；仍需连续满足 {duration} 秒。");
        if (target < input.Current && input.SinceSwitchSeconds < settings.MinimumDwellSeconds) return Hold("降档等待最短驻留时间。");
        return new(target, reason + "；仅为场景试算，仍需有效热余量、目标校验和执行回执。");
    }
}

public sealed class AdaptiveRuleSession
{
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(3);
    private const string TrialSuffix = "；仅为场景试算，仍需有效热余量、目标校验和执行回执。";
    private AdaptiveTriggerPolicy? policy;
    private AdaptiveStage? current;
    private DateTimeOffset? currentSince;
    private DateTimeOffset? candidateSince;
    private string? candidateKey;
    private DateTimeOffset? lastObservedAt;
    private bool lowBatteryLatched;
    private AdaptiveTrialResult lastResult = new(null, "等待连续遥测。");
    public AdaptiveStage? CandidateStage { get; private set; }

    public AdaptiveTrialResult Evaluate(AdaptiveTriggerPolicy policy, AdaptiveTrialInput input, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(input);
        policy.Validate();

        if (this.policy is not null && this.policy != policy)
        {
            ResetCandidate();
            lastObservedAt = null;
        }
        this.policy = policy;

        var currentChanged = current != input.Current;
        if (lastObservedAt is { } last)
        {
            if (observedAtUtc == last && !currentChanged) return lastResult;
            if (observedAtUtc < last || observedAtUtc - last > MaximumSampleGap)
            {
                ResetCandidate();
                currentSince = observedAtUtc;
            }
        }

        lastObservedAt = observedAtUtc;
        if (currentChanged)
        {
            current = input.Current;
            currentSince = observedAtUtc;
            ResetCandidate();
        }
        currentSince ??= observedAtUtc;
        UpdateBatteryLatch(policy.Advanced, input);

        var liveInput = input with { LowBatteryLatched = lowBatteryLatched };
        var possible = AdaptiveRuleTrial.Evaluate(policy, liveInput with
        {
            ConditionSeconds = int.MaxValue,
            SinceSwitchSeconds = int.MaxValue
        });
        if (possible.Target is not { } target)
        {
            ResetCandidate();
            return lastResult = possible;
        }

        if (IsImmediateBatteryLimit(policy.Advanced, liveInput, target))
        {
            ResetCandidate();
            CandidateStage = target;
            return lastResult = possible;
        }

        CandidateStage = target;
        // Load continuity belongs to the load signal, not whichever window the user focuses.
        var application = liveInput.ApplicationContextAvailable ? policy.Advanced.ApplicationRules.FirstOrDefault(rule =>
            string.Equals(rule.Executable, liveInput.Executable.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (!rule.ForegroundOnly || liveInput.Foreground)) : null;
        var source = application?.Executable.ToUpperInvariant() ?? "load";
        var key = $"{target}|{source}|{liveInput.AcConnected}|{liveInput.ApplicationContextAvailable}|{liveInput.IdleContextAvailable}";
        if (candidateKey != key)
        {
            candidateKey = key;
            candidateSince = observedAtUtc;
        }

        var result = AdaptiveRuleTrial.Evaluate(policy, liveInput with
        {
            ConditionSeconds = ElapsedSeconds(observedAtUtc, candidateSince!.Value),
            SinceSwitchSeconds = ElapsedSeconds(observedAtUtc, currentSince.Value)
        });
        return lastResult = result with { Reason = result.Reason.Replace(TrialSuffix, "", StringComparison.Ordinal) };
    }

    public void Reset()
    {
        policy = null;
        current = null;
        currentSince = null;
        lastObservedAt = null;
        ResetCandidate();
        lastResult = new(null, "自动判定已重置。");
    }

    private void UpdateBatteryLatch(AdaptiveAdvancedSettings settings, AdaptiveTrialInput input)
    {
        if (input.AcConnected is true)
        {
            lowBatteryLatched = false;
            return;
        }

        if (input.AcConnected is not false || input.BatteryPercent is not { } battery) return;
        if (battery <= settings.LowBatteryPercent) lowBatteryLatched = true;
        else if (battery >= settings.BatteryRecoveryPercent) lowBatteryLatched = false;
    }

    private static bool IsImmediateBatteryLimit(AdaptiveAdvancedSettings settings, AdaptiveTrialInput input, AdaptiveStage target) =>
        target == AdaptiveStage.Office && input.AcConnected is false && input.BatteryPercent is { } battery &&
        (battery <= settings.LowBatteryPercent || input.LowBatteryLatched && battery < settings.BatteryRecoveryPercent ||
            settings.RespectBatterySaver && input.BatterySaver is true);

    private static int ElapsedSeconds(DateTimeOffset now, DateTimeOffset since) =>
        (int)Math.Clamp(Math.Floor(Math.Max(0, (now - since).TotalSeconds)), 0, int.MaxValue);

    private void ResetCandidate()
    {
        CandidateStage = null;
        candidateKey = null;
        candidateSince = null;
    }
}
