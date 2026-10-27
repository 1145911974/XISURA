using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.Service.Home;

public sealed class AdaptiveAutomationWorker(
    AdaptiveAutomationStateStore store,
    HomeServiceRuntime runtime,
    AdaptiveAutomationHardwareProvider hardware,
    ILogger<AdaptiveAutomationWorker> logger,
    TimeProvider? timeProvider = null,
    AdaptiveProcessCpuReader? processCpuReader = null,
    AdaptiveProcessGpuReader? processGpuReader = null) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TelemetryMaxAge = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ClientContextMaxAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly AdaptiveRuleSession ruleSession = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly AdaptiveProcessCpuReader processCpu = processCpuReader ?? new(timeProvider);
    private readonly AdaptiveProcessGpuReader processGpu = processGpuReader ?? new(timeProvider);
    private readonly long startedAt = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private long publishedRevision = -1;
    private readonly Dictionary<PresetKey, PageSelection[]> publishedSelections = new();
    private bool configurationChanged;
    private AdaptiveTriggerPolicy? runtimePolicy;
    private bool? lastAcConnected;
    private long seenRevision = -1;
    private DateTimeOffset retryAfterUtc;
    private PresetKey? currentTarget;
    private PresetKey? submittedTarget;
    private long submittedRevision = -1;
    private ApplyState? targetApplyState;
    private bool unverifiedNativeMode;
    private Guid? unknownOperation;
    private long seenHardwareEpoch = -1;
    private readonly Dictionary<PresetKey, int> failures = new();
    private PresetKey? pausedTarget;
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
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await cycleGate.WaitAsync(stoppingToken);
                try { await hardware.RunAutomationCycleAsync(TickAsync, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Adaptive service tick failed; retrying after {seconds}s", FailureBackoff.TotalSeconds);
                    ResetObservations();
                    retryAfterUtc = clock.GetUtcNow() + FailureBackoff;
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
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var saved = await store.ReadAsync(deadline.Token);
        if (runtime.IsSuspended || !runtime.CanRunClientAutomation(saved.OwnerSid)) { ResetObservations(); return; }
        if (saved.Recovery is not null)
        {
            ResetObservations();
            await PublishStatusAsync(saved.Configuration, false, "原自动调度恢复记录尚未处理，自动执行已暂停。", null, cancellationToken);
            return;
        }
        if (saved.Revision != seenRevision)
        {
            seenRevision = saved.Revision;
            ResetObservations();
            runtimePolicy = null;
            configurationChanged = true;
            failures.Clear(); pausedTarget = null; retryAfterUtc = default;
        }

        if (saved.Configuration is not { Enabled: true } configuration)
        {
            ResetObservations();
            // Disable transfers control to the user; it never restores an old whole-machine snapshot.
            await PublishStatusAsync(saved.Configuration, false, "自动调度已关闭。", null, cancellationToken);
            return;
        }
        if (saved.ManualOverrideUntilUtc is { } manualUntil && now < manualUntil)
        {
            ResetObservations();
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
        if (seenHardwareEpoch != runtime.Execution.HardwareEpoch)
        {
            seenHardwareEpoch = runtime.Execution.HardwareEpoch;
            ResetObservations(); failures.Clear(); pausedTarget = null;
            retryAfterUtc = default; submittedRevision = -1;
        }
        var unresolved = await runtime.ReadUnresolvedOperationsAsync(deadline.Token);
        if (unresolved.Length > 0) unknownOperation ??= unresolved[0];
        now = clock.GetUtcNow();
        var telemetry = state.Telemetry;
        lastAcConnected = telemetry?.AcPowerConnected;
        if (state.Capabilities.SupportState != DeviceSupportState.Ready || state.Controls.PerformanceMode is not { } mode ||
            telemetry is null || now - telemetry.CapturedAtUtc > TelemetryMaxAge || telemetry.CapturedAtUtc > now.AddSeconds(1))
        {
            ResetObservations();
            await PublishStatusAsync(configuration, false, "服务/硬件状态不可用或遥测超过 3 秒，暂停自动判定。", null, cancellationToken);
            return;
        }
        var currentStage = ResolveCurrentStage(mode);
        if (currentStage is null)
        {
            ResetObservations();
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
        double? processCpuPercent = null;
        double? processGpuPercent = null;
        if (context is not null && matchedApplication is { RequireActivity: true } &&
            runtime.GetClientAutomationProcessScope(saved.OwnerSid) is { } processOwner)
        {
            var processScope = $"{processOwner.ClientSessionId:N}/{saved.Revision}/{runtime.Execution.HardwareEpoch}";
            processCpuPercent = processCpu.Read(matchedApplication.Executable, processOwner.OwnerSid, processOwner.SessionId,
                processScope);
            processGpuPercent = processGpu.Read(matchedApplication.Executable, processOwner.OwnerSid, processOwner.SessionId,
                processScope);
        }
        else { processCpu.Reset(); processGpu.Reset(); }
        var input = new AdaptiveTrialInput
        {
            CpuObservedAtUtc = telemetry.CpuUsageObservedAtUtc ?? telemetry.CapturedAtUtc,
            GpuObservedAtUtc = telemetry.GpuUsageObservedAtUtc ?? telemetry.CapturedAtUtc,
            CpuTemperatureObservedAtUtc = telemetry.CpuTemperatureObservedAtUtc ?? telemetry.CapturedAtUtc,
            GpuTemperatureObservedAtUtc = telemetry.GpuTemperatureObservedAtUtc ?? telemetry.CapturedAtUtc,
            CpuPercent = FreshSensor(telemetry.CpuUsageObservedAtUtc ?? telemetry.CapturedAtUtc, now) ? telemetry.CpuUsagePercent : null,
            GpuPercent = FreshSensor(telemetry.GpuUsageObservedAtUtc ?? telemetry.CapturedAtUtc, now) ? telemetry.GpuUsagePercent : null,
            CpuTemperature = FreshSensor(telemetry.CpuTemperatureObservedAtUtc ?? telemetry.CapturedAtUtc, now) ? telemetry.CpuTemperatureC : null,
            GpuTemperature = FreshSensor(telemetry.GpuTemperatureObservedAtUtc ?? telemetry.CapturedAtUtc, now) ? telemetry.GpuTemperatureC : null,
            AcConnected = telemetry.AcPowerConnected,
            BatteryPercent = telemetry.BatteryPercent,
            BatterySaver = telemetry.BatterySaver,
            ProcessCpuPercent = processCpuPercent,
            ProcessGpuPercent = processGpuPercent,
            Executable = executable,
            Foreground = context?.ForegroundExecutable is { } foreground && string.Equals(executable, foreground, StringComparison.OrdinalIgnoreCase),
            IdleSeconds = idleSeconds,
            Current = currentStage.Value,
            ReapplyCurrentTarget = configurationChanged,
            ConfigurationChanged = configurationChanged,
            ApplicationContextAvailable = !contextIncomplete,
            IdleContextAvailable = context?.IdleSeconds is not null
        };
        var result = ruleSession.Evaluate(policy, input, telemetry.CapturedAtUtc, clock.GetElapsedTime(startedAt));
        store.RecordDecision(new(clock.GetUtcNow(), clock.GetElapsedTime(startedAt).TotalSeconds, saved.Revision,
            runtime.Execution.HardwareEpoch, mode, ruleSession.CandidateStage, ruleSession.Evidence,
            input.CpuObservedAtUtc is { } cpuAt ? (now - cpuAt).TotalSeconds : null,
            input.GpuObservedAtUtc is { } gpuAt ? (now - gpuAt).TotalSeconds : null, result.Reason));
        lastEvaluationUtc = telemetry.CapturedAtUtc;
        if (ruleSession.RequiresEcRelease && (state.Controls.ActiveFanControlPlan is not null || state.Controls.StrongCooling == true))
        {
            if (unresolved.Length > 0 && (await runtime.ReadControlJournalAsync(deadline.Token))?.Unresolved.Any(item =>
                    item.CommandKind == nameof(ReleaseFanControlCommand)) == true)
            {
                await PublishStatusAsync(configuration, false, "此前交还EC的结果未知，禁止自动重复写入；请人工核实风扇状态。", lastEvaluationUtc, cancellationToken);
                return;
            }
            var release = await hardware.ExecuteAutomaticCommandAsync(runtime,
                new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.ThermalEmergency), deadline.Token, saved.Revision);
            if (!Succeeded(release))
            {
                lastError = $"温控交还EC未确认：{release.Error?.Code}";
                await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, cancellationToken);
                return;
            }
        }
        if (unknownOperation is not null)
        {
            await PublishStatusAsync(configuration, false, $"操作{unknownOperation}结果未知；等待读回/人工核实，禁止自动重复写入。", lastEvaluationUtc, cancellationToken);
            return;
        }
        if (now < retryAfterUtc) return;
        if (result.Target is not { } target)
        {
            await PublishStatusAsync(configuration, true, result.Reason, lastEvaluationUtc, cancellationToken);
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
        if (pausedTarget == key)
        {
            await PublishStatusAsync(configuration, false, "同一目标已重试3次；等待配置、设备代次或用户请求变化。", lastEvaluationUtc, cancellationToken);
            return;
        }
        if (submittedTarget == key && submittedRevision == saved.Revision)
        {
            await PublishStatusAsync(configuration, true, targetApplyState == ApplyState.SubmittedUnverified
                ? "目标已提交，实际模式尚无独立确认；不重复写入。" : "有效目标未变化，不重复写入。", lastEvaluationUtc, cancellationToken);
            return;
        }
        if (unverifiedNativeMode && target > currentStage.Value)
        {
            await PublishStatusAsync(configuration, false, "前一原生模式尚未独立确认，暂停继续升档。", lastEvaluationUtc, cancellationToken);
            return;
        }
        var preset = configuration.Presets.FirstOrDefault(item => item.Key == key);
        if (preset is null)
        {
            logger.LogWarning("Adaptive target {target} has no saved preset snapshot", key);
            lastError = "目标映射引用了缺失的预设快照。";
            await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, cancellationToken);
            BackOff(key);
            return;
        }

        if (publishedRevision != saved.Revision)
        {
            var snapshots = configuration.Presets.Select(BuildSnapshot).ToArray();
            var items = snapshots.SelectMany(item => item.Pages).DistinctBy(item => (item.Page, item.Key)).ToArray();
            publishedSelections.Clear();
            foreach (var snapshot in snapshots) publishedSelections[snapshot.Key] = snapshot.Selections;
            if (items.Length > 0)
            {
                string catalogOwner = HomeServiceRuntime.AutomaticCatalogOwner(saved.OwnerSid);
                var publication = new PublishPresetCatalogCommand(Guid.NewGuid(), runtime.Execution.Catalog.Revision(catalogOwner), items);
                var published = await runtime.ExecuteAsync(publication, deadline.Token, userSid: catalogOwner);
                if (!Succeeded(published))
                {
                    lastError = $"自动预设目录发布失败：{published.Error?.Code}";
                    await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, deadline.Token);
                    BackOff(key); return;
                }
            }
            publishedRevision = saved.Revision;
        }
        var operationId = Guid.NewGuid();
        var selections = publishedSelections.GetValueOrDefault(key) ?? [];
        if (ruleSession.RequiresEcRelease) selections = selections.Where(page => page.Page != ControlPageId.Fan).ToArray();
        var command = new ApplyControlRequestCommand(operationId,
            new(operationId, 0, ControlOrigin.Adaptive, key.Mode, preset.PerformanceMode, selections));
        var applied = await hardware.ExecuteAutomaticCommandAsync(runtime, command, deadline.Token, saved.Revision);
        store.RecordDecision(new(clock.GetUtcNow(), clock.GetElapsedTime(startedAt).TotalSeconds, saved.Revision,
            runtime.Execution.HardwareEpoch, mode, target, ruleSession.Evidence, null, null,
            applied.Error?.Code.ToString() ?? applied.State.ToString(), operationId, applied.ExecutionState?.Pages));
        if (!Succeeded(applied))
        {
            lastError = $"自动目标提交失败：{applied.Error?.Code ?? ErrorCode.HardwareWriteFailed}";
            await PublishStatusAsync(configuration, false, lastError, lastEvaluationUtc, CancellationToken.None);
            if (applied.State == CommandState.RecoveryRequired || applied.Mutation == HardwareMutation.Unknown && applied.Error is not null)
                unknownOperation = operationId;
            BackOff(key); return;
        }
        var latestConfiguration = await store.ReadAsync(CancellationToken.None);
        if (latestConfiguration.Revision != saved.Revision || latestConfiguration.Configuration?.Enabled != true)
        {
            await PublishStatusAsync(latestConfiguration.Configuration, false, "配置或手动控制权已变化；旧计划已停止，不回写旧快照。",
                lastEvaluationUtc, CancellationToken.None);
            return;
        }
        submittedTarget = key; submittedRevision = saved.Revision;
        unverifiedNativeMode = applied.VerifiedPerformanceMode != preset.PerformanceMode;
        targetApplyState = unverifiedNativeMode ? ApplyState.SubmittedUnverified : ApplyState.Confirmed;
        if (!unverifiedNativeMode)
        {
            currentTarget = key;
            currentStageOverride = target;
            currentStageOverrideMode = preset.PerformanceMode;
        }
        configurationChanged = false;
        lastApplyUtc = clock.GetUtcNow();
        var pages = applied.ExecutionState?.Pages ?? [];
        bool partial = pages.Any(page => page.State is ApplyState.Partial or ApplyState.Failed or ApplyState.Unknown or ApplyState.Superseded);
        lastError = partial ? string.Join("；", pages.Where(page => page.State is ApplyState.Partial or ApplyState.Failed or ApplyState.Unknown)
            .Select(page => $"{page.Page}: {page.ErrorCode ?? string.Join(',', page.SkippedFields)}")) : null;
        if (partial) targetApplyState = ApplyState.Partial;
        await PublishStatusAsync(configuration, true, partial ? $"已提交{PresetLabel(key)}；部分字段未应用：{lastError}"
            : $"已提交{PresetLabel(key)}；模式与字段是否确认以独立读回证据为准。", lastEvaluationUtc, CancellationToken.None);
    }

    // Legacy configuration remains readable; its Windows values join the same page compiler.
    private AdaptiveAutomationPreset BuildSnapshot(AdaptiveAutomationPreset preset)
    {
        if (preset.Pages.Length > 0 || preset.CpuWindowsTuning is not { } cpu) return preset;
        var settings = new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase);
        void Set<T>(string field, T? value) { if (value is not null) settings[field] = SettingRequest.Set(value); }
        int? ac = cpu.AcMaxFrequencyMhz ?? cpu.MaxFrequencyMhz;
        int? dc = cpu.DcMaxFrequencyMhz ?? cpu.MaxFrequencyMhz;
        Set("acMaxFrequencyMhz", ac is > 0 ? ac : null); Set("dcMaxFrequencyMhz", dc is > 0 ? dc : null);
        Set("isBoostEnabled", cpu.BoostEnabled); Set("windowsPowerSchemeId", cpu.WindowsPowerSchemeId);
        Set("acMinActiveCoresPercent", cpu.AcMinActiveCoresPercent); Set("dcMinActiveCoresPercent", cpu.DcMinActiveCoresPercent);
        Set("negativeCurveOptimizer", cpu.NegativeCurveOptimizer); Set("advancedCpuTuning", cpu.Advanced);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            temperatureLimitC = 75, splWatts = 60, spptWatts = 60, acMaxFrequencyMhz = ac is > 0 ? ac : 4500,
            dcMaxFrequencyMhz = dc is > 0 ? dc : 3200, isBoostEnabled = cpu.BoostEnabled ?? true,
            windowsPowerSchemeId = cpu.WindowsPowerSchemeId ?? Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            acMinActiveCoresPercent = cpu.AcMinActiveCoresPercent, dcMinActiveCoresPercent = cpu.DcMinActiveCoresPercent,
            negativeCurveOptimizer = cpu.NegativeCurveOptimizer, advancedCpuTuning = cpu.Advanced
        });
        var envelope = new PagePresetEnvelope(2, ControlPageId.Performance, preset.Key, "历史自动预设", payload, DateTimeOffset.UnixEpoch)
        { Revision = seenRevision + 1, Settings = settings };
        return preset with { Pages = [envelope], Selections = [new(ControlPageId.Performance, true, preset.Key, envelope.Revision)] };
    }

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
        if (plan.BoostPolicy is not null && CpuBoostPolicy.FromState(state) is null) return false;
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
            (plan.BoostEnabled is null || (CpuBoostPolicy.FromState(state) is { } actualBoost
                ? actualBoost.MatchesToggle(plan.BoostEnabled.Value) : state.BoostEnabled == plan.BoostEnabled)) &&
            (plan.BoostPolicy is null || CpuBoostPolicy.FromState(state) == plan.BoostPolicy) &&
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
        plan.MaxFrequencyMhz is not null || plan.AcMaxFrequencyMhz is not null || plan.BoostEnabled is not null || plan.BoostPolicy is not null ||
        plan.WindowsPowerSchemeId is not null || plan.AcMinActiveCoresPercent is not null || plan.Advanced?.PboScalar is not null || HasCurveTargets(plan);

    private static bool HasCurveTargets(CpuTuningPlan plan) => plan.NegativeCurveOptimizer is not null ||
        plan.Advanced?.CurveOptimizerAll is not null || plan.Advanced?.PerCoreCurveOptimizer is { Count: > 0 };

    private static bool HasRestorableCurve(CpuTuningState state) => state.CurveOptimizerVerification == "hardwareReadback" &&
        state.HasCompleteCurveValues() && state.PerCoreCurveOptimizer!.Values.All(value => value <= 0);

    private static bool IsFresh(AdaptiveAutomationClientContext? context, DateTimeOffset now) =>
        context is not null && context.CapturedAtUtc <= now.AddSeconds(1) && now - context.CapturedAtUtc <= ClientContextMaxAge;

    private static bool FreshSensor(DateTimeOffset sampledAt, DateTimeOffset now) =>
        sampledAt <= now.AddSeconds(1) && now - sampledAt <= TelemetryMaxAge;

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
            SubmittedTarget = submittedTarget, TargetApplyState = targetApplyState,
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

    private void BackOff(PresetKey? target = null)
    {
        ResetObservations();
        int count = target is { } key ? failures[key] = failures.GetValueOrDefault(key) + 1 : 1;
        if (count > 3 && target is { } failed) pausedTarget = failed;
        retryAfterUtc = clock.GetUtcNow().AddSeconds(count switch { 1 => 5, 2 => 15, _ => 30 });
    }

    private void ResetObservations()
    {
        ruleSession.Reset();
        processCpu.Reset();
        processGpu.Reset();
    }
}
