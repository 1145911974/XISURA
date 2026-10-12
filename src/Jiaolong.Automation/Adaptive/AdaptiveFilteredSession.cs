namespace Jiaolong_ControlCenter.Services;

public sealed record AdaptiveFilterEvidence(double? FastCpu, double? FastGpu, double? SlowCpu, double? SlowGpu,
    int WarmupSamples, AdaptiveStage ThermalCap, bool BatteryLatched, Dictionary<string, double> ConditionSeconds);

// UTC identifies samples. Runtime duration uses the caller's monotonic timestamp.
public sealed class AdaptiveFilteredSession
{
    private readonly Dictionary<string, double> conditions = new();
    private readonly Dictionary<string, double> elapsedConditions = new();
    private AdaptiveTriggerPolicy? policy;
    private AdaptiveStage? current;
    private DateTimeOffset? lastSample;
    private DateTimeOffset? lastCpuSample, lastGpuSample;
    private double cpuTime, gpuTime;
    private double lastTime, currentSince, warmSince;
    private int samples;
    private bool loadMissing, powerUnknown;
    private double? fastCpu, fastGpu, slowCpu, slowGpu;
    private bool batteryLatched;
    private AdaptiveStage thermalCap = AdaptiveStage.Turbo;
    private double thermalHoldUntil;
    private string? lastRule;
    private AdaptiveStage? ruleTarget;
    private AdaptiveTrialResult lastResult = new(null, "等待新鲜遥测。");
    public AdaptiveStage? CandidateStage { get; private set; }
    public bool RequiresEcRelease => thermalCap < AdaptiveStage.Turbo;
    public AdaptiveFilterEvidence Evidence => new(fastCpu, fastGpu, slowCpu, slowGpu, samples, thermalCap, batteryLatched,
        new(elapsedConditions));
    public static AdaptiveTrialResult EvaluateScenario(AdaptiveTriggerPolicy policy, AdaptiveTrialInput input)
    {
        if (input.ConditionSeconds is < 0 or > 3600 || input.SinceSwitchSeconds is < 0 or > 3600)
            throw new ArgumentException("场景时长必须在0–3600秒内。");
        var session = new AdaptiveFilteredSession { current = input.Current,
            currentSince = input.ConditionSeconds - input.SinceSwitchSeconds, batteryLatched = input.LowBatteryLatched };
        AdaptiveTrialResult result = new(null, "等待模拟观察。");
        for (int second = 0; second <= input.ConditionSeconds; second++)
            result = session.Evaluate(policy, input with { CpuObservedAtUtc = null, GpuObservedAtUtc = null,
                CpuTemperatureObservedAtUtc = null, GpuTemperatureObservedAtUtc = null }, DateTimeOffset.UnixEpoch.AddSeconds(second), second);
        return result with { Reason = result.Reason + "；按指定时长模拟连续样本，未执行硬件。" };
    }

    public AdaptiveTrialResult Evaluate(AdaptiveTriggerPolicy nextPolicy, AdaptiveTrialInput input,
        DateTimeOffset sampleAt, double monotonicSeconds)
    {
        nextPolicy.Validate();
        if (policy is not null && policy != nextPolicy) Reset();
        policy = nextPolicy;
        if (lastSample is { } previous && sampleAt == previous && current == input.Current) return lastResult;
        if (lastSample is { } last && (sampleAt < last || sampleAt - last > TimeSpan.FromSeconds(3) ||
            monotonicSeconds < lastTime || monotonicSeconds - lastTime > 3))
            ResetObservations(monotonicSeconds);
        double dt = lastSample is null ? 0 : Math.Clamp(monotonicSeconds - lastTime, 0, 3);
        lastSample = sampleAt; lastTime = monotonicSeconds;
        if (current != input.Current) { current = input.Current; currentSince = monotonicSeconds; }
        CandidateStage = null;
        var settings = nextPolicy.Advanced;
        double now = monotonicSeconds;
        AdaptiveTrialResult Hold(string reason) => lastResult = new(null, reason);
        AdaptiveTrialResult Target(AdaptiveStage target, string reason)
        {
            CandidateStage = target;
            return lastResult = target == input.Current && !input.ReapplyCurrentTarget ? new(null, "有效目标无变化。") : new(target, reason);
        }
        var cpuAt = input.CpuObservedAtUtc ?? sampleAt;
        var gpuAt = input.GpuObservedAtUtc ?? sampleAt;
        bool cpuValid = Valid(input.CpuPercent) && Fresh(cpuAt, sampleAt);
        bool gpuValid = Valid(input.GpuPercent) && Fresh(gpuAt, sampleAt);
        if (powerUnknown && input.AcConnected is not null || loadMissing && (cpuValid || gpuValid)) ResetLoad(now);
        powerUnknown = input.AcConnected is null;
        loadMissing = !cpuValid && !gpuValid;
        bool newCpu = cpuValid && (lastCpuSample is null || cpuAt > lastCpuSample);
        bool newGpu = gpuValid && (lastGpuSample is null || gpuAt > lastGpuSample);
        if (!cpuValid) fastCpu = slowCpu = null;
        else if (newCpu)
        {
            double cpuObservedTime = now - Math.Max(0, (sampleAt - cpuAt).TotalSeconds);
            double cpuDt = lastCpuSample is not null ? Math.Clamp(cpuObservedTime - cpuTime, 0, 3) : dt;
            fastCpu = Filter(fastCpu, input.CpuPercent, cpuDt, 2);
            slowCpu = Filter(slowCpu, input.CpuPercent, cpuDt, 8);
            lastCpuSample = cpuAt; cpuTime = now - Math.Max(0, (sampleAt - cpuAt).TotalSeconds);
        }
        if (!gpuValid) fastGpu = slowGpu = null;
        else if (newGpu)
        {
            double gpuObservedTime = now - Math.Max(0, (sampleAt - gpuAt).TotalSeconds);
            double gpuDt = lastGpuSample is not null ? Math.Clamp(gpuObservedTime - gpuTime, 0, 3) : dt;
            fastGpu = Filter(fastGpu, input.GpuPercent, gpuDt, 2);
            slowGpu = Filter(slowGpu, input.GpuPercent, gpuDt, 8);
            lastGpuSample = gpuAt; gpuTime = now - Math.Max(0, (sampleAt - gpuAt).TotalSeconds);
        }
        if (input.AcConnected is not null && (newCpu || newGpu)) { if (samples++ == 0) warmSince = now; }
        if (loadMissing) ResetLoad(now);
        var cpuTempAt = input.CpuTemperatureObservedAtUtc ?? sampleAt;
        var gpuTempAt = input.GpuTemperatureObservedAtUtc ?? sampleAt;
        bool cpuTempValid = Temperature(input.CpuTemperature) && Fresh(cpuTempAt, sampleAt);
        bool gpuTempValid = Temperature(input.GpuTemperature) && Fresh(gpuTempAt, sampleAt);
        bool temperatures = cpuTempValid && gpuTempValid;
        bool cpuHot = cpuTempValid && input.CpuTemperature >= settings.CpuTemperatureCeiling;
        bool gpuHot = gpuTempValid && input.GpuTemperature >= settings.GpuTemperatureCeiling;
        bool cpuEmergency = cpuTempValid && input.CpuTemperature >= input.CpuEmergencyTemperatureC;
        bool gpuEmergency = gpuTempValid && input.GpuTemperature >= input.GpuEmergencyTemperatureC;
        double cpuTempTime = now - Math.Max(0, (sampleAt - cpuTempAt).TotalSeconds);
        double gpuTempTime = now - Math.Max(0, (sampleAt - gpuTempAt).TotalSeconds);
        bool emergency = cpuEmergency || gpuEmergency || input.HardwareThermalThrottling == true;
        double emergencyTime = input.HardwareThermalThrottling == true ? now : Math.Max(cpuEmergency ? cpuTempTime : double.MinValue, gpuEmergency ? gpuTempTime : double.MinValue);
        if (Continuous("emergency", emergency, emergencyTime) >= 3)
        {
            thermalCap = AdaptiveStage.Office;
            thermalHoldUntil = now + 30;
            ClearCandidates(); ruleTarget = null;
            return Target(AdaptiveStage.Office, "紧急热保护；交还EC。");
        }
        double hotSeconds = Continuous("softHeat", cpuHot || gpuHot, Math.Max(cpuHot ? cpuTempTime : double.MinValue, gpuHot ? gpuTempTime : double.MinValue));
        if (hotSeconds >= 15)
        {
            thermalCap = (AdaptiveStage)Math.Max(0, Math.Min((int)thermalCap, (int)input.Current) - 1);
            thermalHoldUntil = now + 30;
            conditions["softHeat"] = now;
        }
        bool cooled = temperatures && input.CpuTemperature < settings.CpuTemperatureCeiling - 5 &&
            input.GpuTemperature < settings.GpuTemperatureCeiling - 5 && input.HardwareThermalThrottling != true;
        if (Continuous("cooled", cooled, Math.Min(cpuTempTime, gpuTempTime)) >= 30 && now >= thermalHoldUntil)
        { if (thermalCap != AdaptiveStage.Turbo) ClearCandidates(); thermalCap = AdaptiveStage.Turbo; conditions.Remove("softHeat"); }
        bool low = input.AcConnected == false && (input.BatteryPercent is >= 0 and <= 100 && input.BatteryPercent <= settings.LowBatteryPercent ||
            settings.RespectBatterySaver && input.BatterySaver == true);
        if (low) batteryLatched = true;
        bool recovered = input.AcConnected == false && input.BatteryPercent >= settings.BatteryRecoveryPercent && input.BatterySaver == false;
        if (Continuous("batteryRecovery", recovered, now) >= 5 || Continuous("acRecovery", input.AcConnected == true, now) >= 3)
            batteryLatched = false;
        if (batteryLatched) { ClearCandidates(); return Target(AdaptiveStage.Office, "电量/节能保护。"); }
        double dcSeconds = Continuous("dc", input.AcConnected == false, now);
        if (input.AcConnected is null) { ClearCandidates(); return Hold("供电未知，保持模式。"); }
        AdaptiveStage powerCap = input.AcConnected == false ? AdaptiveStage.Game : AdaptiveStage.Turbo;
        AdaptiveStage strategyCap = Continuous("quietCap", !nextPolicy.TurboEnabled, now) >= 3 ? AdaptiveStage.Game : AdaptiveStage.Turbo;
        var cap = (AdaptiveStage)Math.Min((int)thermalCap, Math.Min((int)powerCap, (int)strategyCap));
        if (input.Current > cap && (cap != powerCap || input.AcConnected != false || dcSeconds >= 2))
            return Target(cap, "供电/温控/策略上限降档；跳过普通驻留。");
        bool gameCondition = Above(fastCpu, nextPolicy.GameCpuPercent) || Above(fastGpu, nextPolicy.GameGpuPercent);
        bool turboCondition = cpuValid && gpuValid && (Above(slowCpu, nextPolicy.TurboCpuPercent) || Above(slowGpu, nextPolicy.TurboGpuPercent));
        bool officeCondition = cpuValid && gpuValid && fastCpu < nextPolicy.OfficeCpuPercent && fastGpu < nextPolicy.OfficeGpuPercent;
        bool exitCondition = cpuValid && gpuValid && slowCpu < nextPolicy.TurboExitCpuPercent && slowGpu < nextPolicy.TurboExitGpuPercent;
        double gameTime = Math.Max(Above(fastCpu, nextPolicy.GameCpuPercent) ? cpuTime : double.MinValue,
            Above(fastGpu, nextPolicy.GameGpuPercent) ? gpuTime : double.MinValue);
        double bothTime = Math.Min(cpuTime, gpuTime);
        double game = Continuous("game", gameCondition, gameTime), turbo = Continuous("turbo", turboCondition, bothTime);
        double office = Continuous("office", officeCondition, bothTime), exit = Continuous("turboExit", exitCondition, bothTime);
        if (samples < 3 || now - warmSince < 2) return Hold("暖机，等待3个不同样本。");
        var rule = input.ApplicationContextAvailable ? settings.ApplicationRules.FirstOrDefault(item =>
            string.Equals(item.Executable, input.Executable.Trim(), StringComparison.OrdinalIgnoreCase) && (!item.ForegroundOnly || input.Foreground)) : null;
        if (!string.Equals(rule?.Executable, lastRule, StringComparison.OrdinalIgnoreCase))
        {
            conditions.Remove("application"); conditions.Remove("activity"); conditions.Remove("activityAbsent");
            if (rule is not null) ruleTarget = null;
            lastRule = rule?.Executable;
        }
        bool active = Valid(input.ProcessCpuPercent) && input.ProcessCpuPercent >= 5 || Valid(input.ProcessGpuPercent) && input.ProcessGpuPercent >= 10;
        double match = Continuous("application", rule is not null, now);
        double activity = Continuous("activity", active && rule is not null, now);
        if (rule is not null && match >= settings.ApplicationSeconds && (!rule.RequireActivity || activity >= 3))
        {
            ruleTarget = rule.Target;
            conditions.Remove("ruleAbsent");
            if (active || !rule.RequireActivity) conditions.Remove("activityAbsent");
        }
        if (Continuous("ruleAbsent", rule is null, now) >= 3 || Continuous("activityAbsent", rule is not null && rule.RequireActivity && !active, now) >= 15)
            ruleTarget = null;
        if (rule is { RequireActivity: true } && match >= settings.ApplicationSeconds &&
            !Valid(input.ProcessCpuPercent) && !Valid(input.ProcessGpuPercent))
            cap = (AdaptiveStage)Math.Min((int)cap, (int)rule.Target);
        AdaptiveStage? proposed = null;
        string reason = "持续负载条件";
        if (ruleTarget is { } requested) { proposed = requested; reason = "应用规则活动已确认"; }
        else if (input.Current > cap) { proposed = cap; reason = "应用偏好上限，缺少活动证据不强制升档"; }
        else if (officeCondition && office >= nextPolicy.OfficeSeconds) proposed = AdaptiveStage.Office;
        else if (input.Current == AdaptiveStage.Office && gameCondition && game >= nextPolicy.GameSeconds) proposed = AdaptiveStage.Game;
        else if (input.Current == AdaptiveStage.Game && turboCondition && turbo >= nextPolicy.TurboSeconds) proposed = AdaptiveStage.Turbo;
        else if (input.Current == AdaptiveStage.Turbo && exitCondition && exit >= nextPolicy.TurboExitSeconds) proposed = AdaptiveStage.Game;
        if (proposed is null)
        {
            CandidateStage = officeCondition ? AdaptiveStage.Office : input.Current == AdaptiveStage.Office && gameCondition ? AdaptiveStage.Game : turboCondition ? AdaptiveStage.Turbo : null;
            if (!cpuValid && !gpuValid) return Hold("负载未知，保持模式。");
            if (input.ReapplyCurrentTarget && input.ConfigurationChanged) return Target(input.Current, "配置版本变化，重编译当前阶段。");
            return Hold("观察中，连续条件/滞回尚未满足。");
        }
        var target = (AdaptiveStage)Math.Min((int)proposed.Value, (int)cap);
        if (target == AdaptiveStage.Office && settings.IdleReturnEnabled && input.IdleContextAvailable && input.IdleSeconds < settings.IdleSeconds)
            return Hold("低负载已成立，等待用户设置的空闲时长。");
        if (ruleTarget is null && !newCpu && !newGpu) return Hold("等待新的传感器观察，缓存样本不推进负载判定。");
        CandidateStage = target;
        if (target > input.Current && (!temperatures || cpuHot || gpuHot ||
            settings.RespectBatterySaver && input.BatterySaver is null || input.AcConnected is null))
            return Hold("温度/节能状态未确认，阻止升档。");
        if (target == AdaptiveStage.Turbo && (!nextPolicy.TurboEnabled || !cpuValid || !gpuValid)) return Hold("策略或负载条件禁止新升狂飙。");
        double sinceSwitch = now - currentSince;
        if (target != input.Current && sinceSwitch < settings.CooldownSeconds) return Hold("切换冷却期，候选继续计时。");
        double dwell = input.Current == AdaptiveStage.Turbo ? nextPolicy.TurboMinimumDwellSeconds : settings.MinimumDwellSeconds;
        if (target < input.Current && sinceSwitch < dwell) return Hold("降档驻留期，候选继续计时。");
        return Target(target, reason);
    }

    public void Reset()
    {
        policy = null; current = null; lastSample = null; batteryLatched = false;
        thermalCap = AdaptiveStage.Turbo; thermalHoldUntil = 0; ruleTarget = null; lastRule = null;
        ResetObservations(0);
        lastResult = new(null, "自动判定已重置。");
    }
    private void ResetObservations(double now)
    {
        lastSample = null; samples = 0; warmSince = currentSince = now;
        lastCpuSample = lastGpuSample = null; cpuTime = gpuTime = now;
        fastCpu = fastGpu = slowCpu = slowGpu = null;
        ruleTarget = null; lastRule = null;
        conditions.Clear(); elapsedConditions.Clear(); CandidateStage = null;
        loadMissing = powerUnknown = false;
    }
    private void ResetLoad(double now)
    {
        samples = 0; warmSince = now; lastCpuSample = lastGpuSample = null;
        fastCpu = fastGpu = slowCpu = slowGpu = null;
        ClearCandidates();
    }
    private void ClearCandidates() { foreach (string key in new[] { "game", "turbo", "office", "turboExit" }) { conditions.Remove(key); elapsedConditions.Remove(key); } }
    private double Continuous(string key, bool condition, double now)
    {
        if (!condition) { conditions.Remove(key); elapsedConditions.Remove(key); return -1; }
        if (!conditions.TryGetValue(key, out double since)) conditions[key] = since = now;
        return elapsedConditions[key] = Math.Max(0, now - since);
    }
    private static bool Valid(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value);
    private static bool Fresh(DateTimeOffset observed, DateTimeOffset sampled) =>
        sampled - observed <= TimeSpan.FromSeconds(3) && observed - sampled <= TimeSpan.FromSeconds(1);
    private static bool Temperature(double? value) => value is >= -20 and <= 150 && double.IsFinite(value.Value);
    private static bool Above(double? value, double threshold) => value is double number && number >= threshold;
    private static double? Filter(double? previous, double? value, double dt, double tau) => value is not double sample ? null :
        previous is not double old ? sample : old + (1 - Math.Exp(-dt / tau)) * (sample - old);
}
