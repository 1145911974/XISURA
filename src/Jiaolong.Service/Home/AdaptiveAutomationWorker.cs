using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.Service.Home;

public sealed class AdaptiveAutomationWorker(
    AdaptiveAutomationStateStore store,
    HomeServiceRuntime runtime,
    AdaptiveAutomationHardwareProvider hardware,
    ILogger<AdaptiveAutomationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TelemetryMaxAge = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ClientContextMaxAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly AdaptiveRuleSession ruleSession = new();
    private AdaptiveTriggerPolicy? runtimePolicy;
    private bool? lastAcConnected;
    private long seenRevision = -1;
    private DateTimeOffset retryAfterUtc;
    private PresetKey? currentTarget;
    private AdaptiveStage? currentStageOverride;
    private PerformanceMode? currentStageOverrideMode;
    private DateTimeOffset? lastEvaluationUtc;
    private DateTimeOffset? lastApplyUtc;
    private string? lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.InitializeAsync(stoppingToken);
            try
            {
                var saved = await store.ReadAsync(stoppingToken);
                if (saved.Recovery is { } recovery && (recovery.Active || recovery.Pending))
                {
                    var pending = recovery with { Pending = true };
                    if (await store.ReplaceOwnedRecoveryAsync(pending, recovery, stoppingToken))
                        await hardware.RunAutomationCycleAsync(token => RestorePendingAsync(token, force: true), stoppingToken);
                }
            }
            catch (Exception exception) { logger.LogWarning(exception, "Pending adaptive recovery will retry in the service loop."); }
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await cycleGate.WaitAsync(stoppingToken);
                try { await hardware.RunAutomationCycleAsync(TickAsync, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Adaptive service tick failed; retrying after {seconds}s", FailureBackoff.TotalSeconds);
                    ruleSession.Reset();
                    retryAfterUtc = DateTimeOffset.UtcNow + FailureBackoff;
                    lastError = exception is OperationCanceledException ? "自动执行超过时限；保留恢复状态，稍后重试。" : "自动执行失败；保留恢复状态，稍后重试。";
                    var failed = await store.ReadAsync(CancellationToken.None);
                    await PublishStatusAsync(failed.Configuration, false, lastError, lastEvaluationUtc, CancellationToken.None);
                }
                finally { cycleGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Adaptive automation could not initialize; service remains available without scheduling.");
        }
        finally
        {
            if (await cycleGate.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
            {
                try
                {
                    var saved = await store.ReadAsync(CancellationToken.None);
                    if (saved.Recovery is { } recovery && (recovery.Active || recovery.Pending))
                    {
                        var pending = recovery with { Pending = true };
                        if (await store.ReplaceOwnedRecoveryAsync(pending, recovery, CancellationToken.None))
                            await hardware.RunAutomationCycleAsync(token => RestorePendingAsync(token, force: true), CancellationToken.None);
                    }
                }
                catch (Exception exception) { logger.LogError(exception, "Adaptive automation stop recovery failed; recovery state remains persisted."); }
                finally { cycleGate.Release(); }
            }
            else logger.LogError("Adaptive automation stop recovery exceeded its five-second deadline; recovery state remains persisted.");
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < retryAfterUtc) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var saved = await store.ReadAsync(deadline.Token);
        if (saved.Revision != seenRevision)
        {
            seenRevision = saved.Revision;
            ruleSession.Reset();
            runtimePolicy = null;
        }

        if (saved.Recovery is { Pending: true })
        {
            await RestorePendingAsync(deadline.Token);
            return;
        }
        if (saved.Configuration is not { Enabled: true } configuration)
        {
            ruleSession.Reset();
            if (saved.Recovery is { } disabledRecovery && (disabledRecovery.Active || disabledRecovery.Pending))
            {
                var pending = disabledRecovery with { Pending = true };
                if (await store.ReplaceOwnedRecoveryAsync(pending, disabledRecovery, deadline.Token))
                    await RestorePendingAsync(deadline.Token, force: true);
            }
            await PublishStatusAsync(saved.Configuration, false, "自动调度已关闭。", null, cancellationToken);
            return;
        }
        if (saved.ManualOverrideUntilUtc is { } manualUntil && now < manualUntil)
        {
            ruleSession.Reset();
            if (saved.ManualModeChanged)
            {
                currentTarget = null;
                currentStageOverride = null;
                currentStageOverrideMode = null;
            }
            await PublishStatusAsync(configuration, false, "人工模式或 CPU 调整优先，驻留期内暂停自动调度。", null, cancellationToken);
            return;
        }

        var state = await runtime.GetHomeStateAsync(deadline.Token);
        now = DateTimeOffset.UtcNow;
        var telemetry = state.Telemetry;
        lastAcConnected = telemetry?.AcPowerConnected;
        if (state.Capabilities.SupportState != DeviceSupportState.Ready || state.Controls.PerformanceMode is not { } mode ||
            telemetry is null || now - telemetry.CapturedAtUtc > TelemetryMaxAge || telemetry.CapturedAtUtc > now.AddSeconds(1))
        {
            ruleSession.Reset();
            await PublishStatusAsync(configuration, false, "服务/硬件状态不可用或遥测超过 3 秒，暂停自动判定。", null, cancellationToken);
            return;
        }
        var currentStage = ResolveCurrentStage(mode);
        if (currentStage is null)
        {
            ruleSession.Reset();
            await PublishStatusAsync(configuration, false, "当前性能模式无法映射为办公、游戏或狂飙阶段。", telemetry.CapturedAtUtc, cancellationToken);
            return;
        }

        var context = IsFresh(saved.ClientContext, now) ? saved.ClientContext : null;
        var policy = runtimePolicy ??= AdaptiveAutomationConfigurationValidator.ToTriggerPolicy(configuration);
        bool contextIncomplete = policy.Advanced.ApplicationRules.Any(rule =>
            rule.ForegroundOnly ? context?.ForegroundExecutable is null : context?.RunningExecutables is null) ||
            policy.Advanced.IdleReturnEnabled && context?.IdleSeconds is null;
        var matchedApplication = policy.Advanced.ApplicationRules.FirstOrDefault(rule => rule.ForegroundOnly
            ? string.Equals(rule.Executable, context?.ForegroundExecutable, StringComparison.OrdinalIgnoreCase)
            : context?.RunningExecutables?.Contains(rule.Executable, StringComparer.OrdinalIgnoreCase) == true);
        var executable = matchedApplication?.Executable ?? context?.ForegroundExecutable ?? string.Empty;
        var idleSeconds = policy.Advanced.IdleReturnEnabled ? context?.IdleSeconds ?? 0 : 0;
        var input = new AdaptiveTrialInput
        {
            CpuPercent = telemetry.CpuUsagePercent,
            GpuPercent = telemetry.GpuUsagePercent,
            CpuTemperature = telemetry.CpuTemperatureC,
            GpuTemperature = telemetry.GpuTemperatureC,
            AcConnected = telemetry.AcPowerConnected,
            BatteryPercent = telemetry.BatteryPercent,
            BatterySaver = context?.BatterySaver,
            Executable = executable,
            Foreground = context?.ForegroundExecutable is { } foreground && string.Equals(executable, foreground, StringComparison.OrdinalIgnoreCase),
            IdleSeconds = idleSeconds,
            Current = currentStage.Value,
            ReapplyCurrentTarget = true,
            ApplicationContextAvailable = !contextIncomplete,
            IdleContextAvailable = context?.IdleSeconds is not null
        };
        var result = ruleSession.Evaluate(policy, input, telemetry.CapturedAtUtc);
        lastEvaluationUtc = telemetry.CapturedAtUtc;
        if (result.Target is not { } target)
        {
            await PublishStatusAsync(configuration, true, result.Reason, lastEvaluationUtc, cancellationToken);
            return;
        }
        if (contextIncomplete && target >= currentStage.Value)
        {
            await PublishStatusAsync(configuration, true, "应用规则暂停。空闲返回规则暂停。上下文不完整时仅允许负载降档。", lastEvaluationUtc, cancellationToken);
            return;
        }

        var power = telemetry.AcPowerConnected == true ? AdaptivePowerSource.Ac : AdaptivePowerSource.Dc;
        if (telemetry.AcPowerConnected is null)
        {
            await PublishStatusAsync(configuration, false, "供电状态未知，自动目标暂停。", lastEvaluationUtc, cancellationToken);
            return;
        }
        var map = AdaptiveAutomationConfigurationValidator.ToTargetMap(configuration.TargetMap);
        var key = map.GetTarget(power, target);
        var preset = configuration.Presets.FirstOrDefault(item => item.Key == key);
        if (preset is null)
        {
            logger.LogWarning("Adaptive target {target} has no saved preset snapshot", key);
            lastError = "目标映射引用了缺失的预设快照。";
            await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, cancellationToken);
            BackOff();
            return;
        }

        var latest = await runtime.GetHomeStateAsync(deadline.Token);
        if (latest.Controls.PerformanceMode is not { } currentMode ||
            preset.CpuWindowsTuning is { } cpuPlan && !CanApplyAndRestore(cpuPlan, latest.Controls.CpuTuning))
        {
            logger.LogWarning("Adaptive target {target} was skipped because its mode or CPU Windows values cannot be safely read back and restored", key);
            lastError = "目标模式或 CPU 字段缺少安全读回/恢复值。";
            await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, cancellationToken);
            BackOff();
            return;
        }
        var cpu = preset.CpuWindowsTuning;
        bool applyCpu = cpu is not null && !CpuPlanMatches(cpu, latest.Controls.CpuTuning);
        bool applyMode = currentMode != preset.PerformanceMode;
        if (!applyMode && !applyCpu)
        {
            currentTarget = key;
            currentStageOverride = target;
            currentStageOverrideMode = preset.PerformanceMode;
            lastError = null;
            await PublishStatusAsync(configuration, true, $"当前状态已符合{PresetLabel(key)}，无需重复应用。", lastEvaluationUtc, cancellationToken);
            return;
        }

        var priorRecovery = saved.Recovery;
        var recovery = priorRecovery ?? CaptureRecovery(latest.Controls);
        var ownedRecovery = recovery with { LastAppliedMode = preset.PerformanceMode, Pending = true, Active = true };
        if (!await store.TryBeginRecoveryAsync(ownedRecovery, saved.Revision, deadline.Token))
        {
            await PublishStatusAsync(configuration, false, "配置或人工控制权已变化，本轮自动写入已取消。", lastEvaluationUtc, cancellationToken);
            return;
        }
        bool modeApplied = false;
        await PublishStatusAsync(configuration, true, $"正在应用{PresetLabel(key)}。", lastEvaluationUtc, cancellationToken);
        if (applyMode)
        {
            var modeResult = await ApplyModeAsync(preset.PerformanceMode, deadline.Token, saved.Revision);
            if (!Succeeded(modeResult))
            {
                await store.ReplaceOwnedRecoveryAsync(priorRecovery, ownedRecovery, CancellationToken.None);
                logger.LogWarning("Adaptive target {target} mode command failed: {error}", key, modeResult.Error?.Code);
                lastError = $"性能模式应用失败：{modeResult.Error?.Code ?? ErrorCode.HardwareWriteFailed}";
                await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, CancellationToken.None);
                BackOff();
                return;
            }
            modeApplied = true;
        }

        // A performance-mode change may reset Windows CPU limits, so decide after the mode has settled.
        var postMode = modeApplied ? await runtime.GetHomeStateAsync(deadline.Token) : latest;
        applyCpu = cpu is not null && !CpuPlanMatches(cpu, postMode.Controls.CpuTuning);
        if (applyCpu && cpu is not null)
        {
            if (!CanApplyAndRestore(cpu, postMode.Controls.CpuTuning))
            {
                lastError = "性能模式切换后 CPU 字段无法安全读回/恢复。";
                await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, CancellationToken.None);
                BackOff();
                return;
            }
            var cpuResult = await ExecuteCpuPlanAsync(cpu, deadline.Token, saved.Revision);
            if (!Succeeded(cpuResult))
            {
                bool rolledBack = await TryRestoreOwnedSnapshotAsync(ownedRecovery,
                    CaptureRecovery(latest.Controls) with { LastAppliedMode = currentMode }, CancellationToken.None);
                if (rolledBack) await store.ReplaceOwnedRecoveryAsync(priorRecovery, ownedRecovery, CancellationToken.None);
                logger.LogWarning("Adaptive target {target} CPU settings failed: {error}; rolledBack={rolledBack}", key, cpuResult.Error?.Code, rolledBack);
                lastError = $"CPU 调整失败：{cpuResult.Error?.Code ?? ErrorCode.HardwareWriteFailed}; rollback={rolledBack}";
                await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, CancellationToken.None);
                BackOff();
                return;
            }
        }

        var latestConfiguration = await store.ReadAsync(CancellationToken.None);
        if (latestConfiguration.Revision != saved.Revision || latestConfiguration.Configuration?.Enabled != true)
        {
            bool rolledBack = await TryRestoreOwnedSnapshotAsync(ownedRecovery,
                CaptureRecovery(latest.Controls) with { LastAppliedMode = currentMode }, CancellationToken.None);
            if (rolledBack) await store.ReplaceOwnedRecoveryAsync(priorRecovery, ownedRecovery, CancellationToken.None);
            lastError = rolledBack ? null : "自动配置更新期间未能完整撤销旧目标；保留恢复状态。";
            await PublishStatusAsync(latestConfiguration.Configuration, false,
                rolledBack ? "自动配置已变化，已撤销旧目标并等待下一轮判定。" : lastError!,
                lastEvaluationUtc, CancellationToken.None);
            if (!rolledBack) BackOff();
            return;
        }

        var verified = await runtime.GetHomeStateAsync(deadline.Token);
        if (verified.Controls.PerformanceMode != preset.PerformanceMode ||
            cpu is not null && (!CanApplyAndRestore(cpu, verified.Controls.CpuTuning) || !CpuPlanMatches(cpu, verified.Controls.CpuTuning)))
        {
            lastError = "自动目标写入后读回不匹配；恢复状态保留并将在退避后恢复。";
            await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, CancellationToken.None);
            BackOff();
            return;
        }
        if (!await store.CompleteRecoveryAsync(ownedRecovery with { Pending = false, Active = true }, saved.Revision, CancellationToken.None))
        {
            var changed = await store.ReadAsync(CancellationToken.None);
            await PublishStatusAsync(changed.Configuration, false,
                changed.ManualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until
                    ? "检测到人工调整，自动调度已让出控制权。"
                    : "自动配置或恢复所有权已变化，未确认本轮自动目标。",
                lastEvaluationUtc, CancellationToken.None);
            return;
        }
        currentTarget = key;
        currentStageOverride = target;
        currentStageOverrideMode = preset.PerformanceMode;
        lastApplyUtc = DateTimeOffset.UtcNow;
        lastError = null;
        logger.LogInformation("Adaptive target {target} applied; scope={scope}", key, cpu is not null ? "performanceMode+cpuWindows" : "performanceModeOnly");
        await PublishStatusAsync(configuration, true,
            cpu is not null
                ? $"已应用{PresetLabel(key)}：性能模式与 CPU 系统参数。"
                : $"已应用{PresetLabel(key)}：仅性能模式。",
            lastEvaluationUtc, CancellationToken.None);
    }

    private async Task<CommandResult> ApplyModeAsync(PerformanceMode mode, CancellationToken cancellationToken, long? expectedRevision = null)
    {
        var command = new SetPerformanceModeCommand(Guid.NewGuid(), mode);
        return await hardware.ExecuteAutomaticCommandAsync(runtime, command, cancellationToken, expectedRevision);
    }

    private async Task RestorePendingAsync(CancellationToken cancellationToken, bool force = false)
    {
        var snapshot = await store.ReadAsync(cancellationToken);
        if (snapshot.Recovery is not { } recovery) return;
        if (!force && !recovery.Pending) return;
        if (snapshot.ManualOverrideUntilUtc is { } manualUntil && DateTimeOffset.UtcNow < manualUntil)
        {
            await store.ClearRecoveryAsync(CancellationToken.None);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var current = await runtime.GetHomeStateAsync(deadline.Token);
        if (current.Capabilities.SupportState != DeviceSupportState.Ready || current.Controls.PerformanceMode is not { } currentMode)
        {
            logger.LogWarning("Adaptive recovery paused because hardware support state or performance mode is unavailable.");
            retryAfterUtc = DateTimeOffset.UtcNow + FailureBackoff;
            return;
        }
        if (recovery.LastAppliedMode is { } expected && currentMode != expected)
        {
            await store.ReplaceOwnedRecoveryAsync(null, recovery, CancellationToken.None);
            currentTarget = null;
            currentStageOverride = null;
            currentStageOverrideMode = null;
            return;
        }
        if (await RestoreSnapshotAsync(recovery with { LastAppliedMode = recovery.OriginalMode }, deadline.Token))
        {
            if (!await store.ReplaceOwnedRecoveryAsync(null, recovery, CancellationToken.None))
            {
                logger.LogWarning("Adaptive recovery completed but ownership changed before its state could be cleared.");
                return;
            }
            currentTarget = null;
            currentStageOverride = null;
            currentStageOverrideMode = null;
            lastError = null;
            logger.LogInformation("Adaptive automation restored the prior performance mode and readable CPU Windows settings.");
        }
        else
        {
            if (recovery.OriginalMode is { } restoredMode)
            {
                var partial = await runtime.GetHomeStateAsync(deadline.Token);
                if (partial.Controls.PerformanceMode == restoredMode)
                    await store.ReplaceOwnedRecoveryAsync(recovery with { LastAppliedMode = restoredMode, Pending = true }, recovery, CancellationToken.None);
            }
            logger.LogWarning("Adaptive automation restoration was incomplete; recovery state retained for retry.");
            retryAfterUtc = DateTimeOffset.UtcNow + FailureBackoff;
        }
    }

    private async Task<bool> RestoreSnapshotAsync(AdaptiveAutomationRecovery target, CancellationToken cancellationToken)
    {
        var initial = await runtime.GetHomeStateAsync(cancellationToken);
        if (initial.Capabilities.SupportState != DeviceSupportState.Ready || initial.Controls.PerformanceMode is null) return false;
        if (target.LastAppliedMode is { } mode)
        {
            if (initial.Controls.PerformanceMode != mode)
            {
                var result = await ApplyModeAsync(mode, cancellationToken);
                if (!Succeeded(result)) return false;
            }
        }
        var afterMode = await runtime.GetHomeStateAsync(cancellationToken);
        if (afterMode.Capabilities.SupportState != DeviceSupportState.Ready || afterMode.Controls.PerformanceMode is null ||
            target.LastAppliedMode is { } expectedMode && afterMode.Controls.PerformanceMode != expectedMode)
            return false;
        if (target.OriginalCpuWindowsTuning is { } cpu)
        {
            if (!CanApplyAndRestore(cpu, afterMode.Controls.CpuTuning))
            {
                return false;
            }
            else if (!CpuPlanMatches(cpu, afterMode.Controls.CpuTuning))
            {
                var result = await ExecuteCpuPlanAsync(cpu, cancellationToken);
                if (!Succeeded(result)) return false;
            }
        }
        var verified = await runtime.GetHomeStateAsync(cancellationToken);
        return verified.Capabilities.SupportState == DeviceSupportState.Ready &&
            verified.Controls.PerformanceMode is { } finalMode &&
            (target.LastAppliedMode is null || finalMode == target.LastAppliedMode) &&
            (target.OriginalCpuWindowsTuning is not { } finalCpu ||
                CanApplyAndRestore(finalCpu, verified.Controls.CpuTuning) && CpuPlanMatches(finalCpu, verified.Controls.CpuTuning));
    }

    private async Task<bool> TryRestoreOwnedSnapshotAsync(
        AdaptiveAutomationRecovery expectedOwner,
        AdaptiveAutomationRecovery target,
        CancellationToken cancellationToken)
    {
        var latest = await store.ReadAsync(cancellationToken);
        if (latest.ManualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until) return false;
        if (latest.Recovery != expectedOwner || !expectedOwner.Pending) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await RestoreSnapshotAsync(target, deadline.Token);
    }

    private static AdaptiveAutomationRecovery CaptureRecovery(HomeControlState controls) =>
        new(controls.PerformanceMode, CaptureRestorableCpuPlan(controls.CpuTuning), null);

    internal static CpuTuningPlan? CaptureRestorableCpuPlan(CpuTuningState? state)
    {
        if (state is null) return null;
        var ac = state.AcMaxFrequencyMhz;
        var dc = state.DcMaxFrequencyMhz;
        var max = ac is null && dc is null ? state.MaxFrequencyMhz : null;
        var parkingAc = state.AcMinActiveCoresPercent;
        var parkingDc = state.DcMinActiveCoresPercent;
        return new CpuTuningPlan(null, null, null, max, state.BoostEnabled, null,
            state.WindowsPowerSchemeId, null)
        {
            AcMaxFrequencyMhz = ac is not null && dc is not null ? ac : null,
            DcMaxFrequencyMhz = ac is not null && dc is not null ? dc : null,
            AcMinActiveCoresPercent = parkingAc is not null && parkingDc is not null ? parkingAc : null,
            DcMinActiveCoresPercent = parkingAc is not null && parkingDc is not null ? parkingDc : null,
            Advanced = new AdvancedCpuTuningPlan(
                PboScalar: state.PboScalar is >= 1 and <= 10 ? state.PboScalar : null,
                PerCoreCurveOptimizer: HasRestorableCurve(state) ? new Dictionary<int,int>(state.PerCoreCurveOptimizer!) : null)
        } is { } plan && IsNonEmptySafePlan(plan) ? plan : null;
    }

    internal static bool CanApplyAndRestore(CpuTuningPlan plan, CpuTuningState? state)
    {
        if (!AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(plan) || state is null) return false;
        if (plan.Advanced?.PboScalar is not null && state.PboScalar is not (>= 1 and <= 10)) return false;
        if (HasCurveTargets(plan) && !HasRestorableCurve(state)) return false;
        if (plan.MaxFrequencyMhz is not null && state.MaxFrequencyMhz is null) return false;
        if (plan.AcMaxFrequencyMhz is not null && (state.AcMaxFrequencyMhz is null || state.DcMaxFrequencyMhz is null)) return false;
        if (plan.BoostEnabled is not null && state.BoostEnabled is null) return false;
        if (plan.WindowsPowerSchemeId is not null && state.WindowsPowerSchemeId is null) return false;
        return plan.AcMinActiveCoresPercent is null || state.AcMinActiveCoresPercent is not null && state.DcMinActiveCoresPercent is not null;
    }

    internal static bool CpuPlanMatches(CpuTuningPlan plan, CpuTuningState? state)
    {
        if (state is null) return false;
        return (plan.MaxFrequencyMhz is null || state.MaxFrequencyMhz == plan.MaxFrequencyMhz &&
            (state.AcMaxFrequencyMhz is null || state.AcMaxFrequencyMhz == plan.MaxFrequencyMhz) &&
            (state.DcMaxFrequencyMhz is null || state.DcMaxFrequencyMhz == plan.MaxFrequencyMhz)) &&
            (plan.AcMaxFrequencyMhz is null || state.AcMaxFrequencyMhz == plan.AcMaxFrequencyMhz && state.DcMaxFrequencyMhz == plan.DcMaxFrequencyMhz) &&
            (plan.BoostEnabled is null || state.BoostEnabled == plan.BoostEnabled) &&
            (plan.WindowsPowerSchemeId is null || state.WindowsPowerSchemeId == plan.WindowsPowerSchemeId) &&
            (plan.AcMinActiveCoresPercent is null || state.AcMinActiveCoresPercent == plan.AcMinActiveCoresPercent && state.DcMinActiveCoresPercent == plan.DcMinActiveCoresPercent) &&
            (plan.Advanced?.PboScalar is null || state.PboScalar == plan.Advanced.PboScalar) &&
            (!HasCurveTargets(plan) || HasRestorableCurve(state) &&
                ((plan.NegativeCurveOptimizer ?? plan.Advanced?.CurveOptimizerAll) is not int all ||
                    state.PerCoreCurveOptimizer!.Values.All(value => value == all)) &&
                (plan.Advanced?.PerCoreCurveOptimizer is not { } targets ||
                    targets.All(pair => state.PerCoreCurveOptimizer!.GetValueOrDefault(pair.Key, int.MaxValue) == pair.Value)));
    }

    private static bool IsNonEmptySafePlan(CpuTuningPlan plan) =>
        plan.MaxFrequencyMhz is not null || plan.AcMaxFrequencyMhz is not null || plan.BoostEnabled is not null ||
        plan.WindowsPowerSchemeId is not null || plan.AcMinActiveCoresPercent is not null || plan.Advanced?.PboScalar is not null || HasCurveTargets(plan);

    private static bool HasCurveTargets(CpuTuningPlan plan) => plan.NegativeCurveOptimizer is not null ||
        plan.Advanced?.CurveOptimizerAll is not null || plan.Advanced?.PerCoreCurveOptimizer is { Count: > 0 };

    private static bool HasRestorableCurve(CpuTuningState state) => state.CurveOptimizerVerification == "hardwareReadback" &&
        state.HasCompleteCurveValues() && state.PerCoreCurveOptimizer!.Values.All(value => value <= 0);

    private async Task<CommandResult> ExecuteCpuPlanAsync(CpuTuningPlan plan, CancellationToken token, long? revision = null)
    {
        // CO uses its existing isolated transaction; the owned journal spans both submissions.
        var windows = plan with { NegativeCurveOptimizer = null,
            Advanced = plan.Advanced?.PboScalar is int scalar ? new(PboScalar: scalar) : null };
        CommandResult? result = null;
        if (IsNonEmptySafePlan(windows))
        {
            result = await hardware.ExecuteAutomaticCommandAsync(runtime,
                new SetCpuTuningCommand(Guid.NewGuid(), windows, true), token, revision);
            if (!Succeeded(result)) return result;
        }
        if (HasCurveTargets(plan))
        {
            var curve = new CpuTuningPlan(null, null, null, null, null, null, null, plan.NegativeCurveOptimizer)
            { Advanced = plan.Advanced is { } advanced ? new(CurveOptimizerAll: advanced.CurveOptimizerAll,
                PerCoreCurveOptimizer: advanced.PerCoreCurveOptimizer) : null };
            result = await hardware.ExecuteAutomaticCommandAsync(runtime,
                new SetCpuTuningCommand(Guid.NewGuid(), curve, true), token, revision);
        }
        return result ?? throw new InvalidOperationException("Empty automatic CPU plan.");
    }

    private static bool IsFresh(AdaptiveAutomationClientContext? context, DateTimeOffset now) =>
        context is not null && context.CapturedAtUtc <= now.AddSeconds(1) && now - context.CapturedAtUtc <= ClientContextMaxAge;

    private AdaptiveStage? ResolveCurrentStage(PerformanceMode mode)
    {
        if (currentStageOverrideMode == mode && currentStageOverride is { } confirmedStage)
            return confirmedStage;
        if (currentStageOverrideMode is not null && currentStageOverrideMode != mode)
        {
            currentTarget = null;
            currentStageOverride = null;
            currentStageOverrideMode = null;
        }
        return mode switch
        {
            PerformanceMode.Quiet => AdaptiveStage.Office,
            PerformanceMode.Balanced => AdaptiveStage.Game,
            PerformanceMode.Turbo => AdaptiveStage.Turbo,
            _ => null
        };
    }

    private static bool Succeeded(CommandResult result) => result.State == CommandState.Applied && result.Error is null;

    private async Task PublishStatusAsync(
        AdaptiveAutomationConfiguration? configuration,
        bool running,
        string reason,
        DateTimeOffset? evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (evaluatedAtUtc is not null) lastEvaluationUtc = evaluatedAtUtc;
        await store.SetStatusAsync(new AdaptiveAutomationStatus(
            configuration?.Enabled == true,
            configuration?.Enabled == true && running,
            currentTarget,
            reason,
            lastEvaluationUtc,
            lastApplyUtc,
            lastError,
            configuration is null ? null : AdaptiveAutomationConfigurationValidator.Identity(configuration))
        {
            CandidateTarget = configuration is { Enabled: true } && running && lastAcConnected is bool ac && ruleSession.CandidateStage is { } stage
                ? AdaptiveAutomationConfigurationValidator.ToTargetMap(configuration.TargetMap).GetTarget(ac ? AdaptivePowerSource.Ac : AdaptivePowerSource.Dc, stage)
                : null
        },
            cancellationToken);
    }

    private static string PresetLabel(PresetKey key) => $"{key.Mode switch
    {
        ControlModeId.Office => "办公", ControlModeId.Gaming => "游戏", ControlModeId.Turbo => "狂飙",
        ControlModeId.Custom1 => "自定义 1", ControlModeId.Custom2 => "自定义 2", _ => "自定义"
    }}预设 {key.Slot}";

    private void BackOff()
    {
        ruleSession.Reset();
        retryAfterUtc = DateTimeOffset.UtcNow + FailureBackoff;
    }
}
