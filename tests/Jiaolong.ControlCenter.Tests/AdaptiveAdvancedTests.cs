using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using System.Text.Json;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AdaptiveAdvancedTests
{
    [TestMethod]
    public void Target_map_resolves_each_supported_power_and_stage_pair()
    {
        var map = new AdaptiveTargetMap(
            PresetKey.Create(ControlModeId.Office, 1),
            PresetKey.Create(ControlModeId.Gaming, 2),
            PresetKey.Create(ControlModeId.Turbo, 3),
            PresetKey.Create(ControlModeId.Office, 1),
            PresetKey.Create(ControlModeId.Gaming, 2));

        Assert.AreEqual(map.AcOffice, map.GetTarget(AdaptivePowerSource.Ac, AdaptiveStage.Office));
        Assert.AreEqual(map.AcGame, map.GetTarget(AdaptivePowerSource.Ac, AdaptiveStage.Game));
        Assert.AreEqual(map.AcTurbo, map.GetTarget(AdaptivePowerSource.Ac, AdaptiveStage.Turbo));
        Assert.AreEqual(map.DcOffice, map.GetTarget(AdaptivePowerSource.Dc, AdaptiveStage.Office));
        Assert.AreEqual(map.DcGame, map.GetTarget(AdaptivePowerSource.Dc, AdaptiveStage.Game));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => map.GetTarget(AdaptivePowerSource.Dc, AdaptiveStage.Turbo));
        Assert.AreEqual(PerformanceMode.Quiet, AdaptiveTargetMap.PerformanceModeFor(map.AcOffice));
        Assert.AreEqual(PerformanceMode.Balanced, AdaptiveTargetMap.PerformanceModeFor(map.AcGame));
        Assert.AreEqual(PerformanceMode.Turbo, AdaptiveTargetMap.PerformanceModeFor(map.AcTurbo));
        Assert.AreEqual(PerformanceMode.Quiet, AdaptiveTargetMap.HardwareModeFor(map.DcOffice));
        Assert.AreEqual(PerformanceMode.Balanced, AdaptiveTargetMap.HardwareModeFor(map.DcGame));
        foreach (var mode in new[] { ControlModeId.Custom1, ControlModeId.Custom2, ControlModeId.Custom3 })
        {
            var key = PresetKey.Create(mode, 3);
            Assert.AreEqual(PerformanceMode.Custom, AdaptiveTargetMap.PerformanceModeFor(key));
            Assert.AreEqual(PerformanceMode.Turbo, AdaptiveTargetMap.HardwareModeFor(key));
            Assert.AreEqual(key, map.WithTarget(AdaptivePowerSource.Ac, AdaptiveStage.Game, key).AcGame);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Game, key));
        }
    }

    [TestMethod]
    public async Task Adaptive_preset_executor_does_not_duplicate_inflight_or_completed_targets()
    {
        var target = PresetKey.Create(ControlModeId.Gaming, 2);
        var calls = 0;
        var completion = new TaskCompletionSource<AdaptivePresetApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new AdaptivePresetExecutor((_, _) =>
        {
            calls++;
            return completion.Task;
        });

        Assert.IsTrue(executor.TryStartApply(target, CancellationToken.None, out var first));
        Assert.IsFalse(executor.TryStartApply(target, CancellationToken.None, out _));
        Assert.AreEqual(1, calls);
        completion.SetResult(new(new CommandResult(Guid.NewGuid(), CommandState.Applied, null, RequiredUserAction.None, null, false)));
        Assert.IsNotNull(await first);
        Assert.IsFalse(executor.TryStartApply(target, CancellationToken.None, out _));
        Assert.AreEqual(1, calls);

        executor.Reset();
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(executor.TryStartApply(target, CancellationToken.None, out var retry));
        Assert.AreEqual(2, calls);
        completion.SetResult(new(new CommandResult(Guid.NewGuid(), CommandState.Applied, null, RequiredUserAction.None, null, false)));
        Assert.IsNotNull(await retry);
    }

    [TestMethod]
    public void Windows_runtime_signals_are_read_only_and_return_safe_unknowns()
    {
        var signals = AdaptiveRuntimeSignals.Read();

        Assert.IsTrue(signals.IdleSeconds is null or >= 0);
        Assert.IsTrue(signals.Executable.Length == 0 || signals.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Presentation_and_advanced_settings_roundtrip_and_reject_invalid_values()
    {
        var policy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.BalancedAdaptive) with
        { Advanced = new() { LowBatteryPercent = 25, ApplicationRules = [new("ffmpeg.exe", false, AdaptiveStage.Game)] } };
        var copy = JsonSerializer.Deserialize<AdaptiveTriggerPolicy>(JsonSerializer.Serialize(policy))!;
        Assert.AreEqual(25, copy.Advanced.LowBatteryPercent);
        Assert.AreEqual("ffmpeg.exe", copy.Advanced.ApplicationRules[0].Executable);
        var preferences = new UserPreferences(false, "system", false)
        { AdaptiveStrategyAppearances = new() { ["BalancedAdaptive"] = new("日常主力", "视频压缩与办公", "DecisionCpu") } };
        var restored = JsonSerializer.Deserialize<UserPreferences>(JsonSerializer.Serialize(preferences))!;
        Assert.AreEqual("日常主力", restored.AdaptiveStrategyAppearances["BalancedAdaptive"].Name);
        Assert.AreEqual("DecisionCpu", restored.AdaptiveStrategyAppearances["BalancedAdaptive"].Icon);
        Assert.ThrowsExactly<ArgumentException>(() => (policy.Advanced with { BatteryRecoveryPercent = 20 }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new AdaptiveStrategyAppearance("", "备注", "StrategyQuiet").Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new AdaptiveStrategyAppearance("名称", "备注", "../file").Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new AdaptiveApplicationRule("*.exe", false, AdaptiveStage.Turbo).Validate());
    }

    [TestMethod]
    public void Safety_and_unknown_inputs_override_application_requests()
    {
        var policy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.BalancedAdaptive) with
        { Advanced = new() { ApplicationRules = [new("game.exe", true, AdaptiveStage.Turbo)] } };
        var sample = new AdaptiveTrialInput { Executable = "game.exe", Foreground = true, CpuPercent = 95, GpuPercent = 98, CpuTemperature = 70, GpuTemperature = 65, BatteryPercent = 15, AcConnected = false, ConditionSeconds = 60, SinceSwitchSeconds = 200 };
        Assert.AreEqual(AdaptiveStage.Office, AdaptiveRuleTrial.Evaluate(policy, sample with { Current = AdaptiveStage.Game }).Target);
        Assert.IsNull(AdaptiveRuleTrial.Evaluate(policy, sample with { Current = AdaptiveStage.Office }).Target);
        Assert.IsNull(AdaptiveRuleTrial.Evaluate(policy, sample with { AcConnected = true, CpuTemperature = null }).Target);
        Assert.IsNull(AdaptiveRuleTrial.Evaluate(policy, sample with { AcConnected = true, CpuTemperature = 96 }).Target);
    }

    [TestMethod]
    public void Background_encoding_matches_without_foreground_and_timing_is_respected()
    {
        var policy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.BalancedAdaptive) with
        { Advanced = new() { ApplicationRules = [new("ffmpeg.exe", false, AdaptiveStage.Game)] } };
        var sample = new AdaptiveTrialInput { Executable = "FFMPEG.EXE", Foreground = false, CpuPercent = 60, GpuPercent = 5, CpuTemperature = 70, GpuTemperature = 50, AcConnected = true, BatteryPercent = 90, ConditionSeconds = 60, SinceSwitchSeconds = 200, IdleSeconds = 1000 };
        Assert.AreEqual(AdaptiveStage.Game, AdaptiveRuleTrial.Evaluate(policy, sample).Target);
        Assert.IsNull(AdaptiveRuleTrial.Evaluate(policy, sample with { SinceSwitchSeconds = 0 }).Target);
        Assert.IsNull(AdaptiveRuleTrial.Evaluate(policy, sample with { ConditionSeconds = 0 }).Target);
    }

    [TestMethod]
    public void Continuous_session_requires_consecutive_fresh_samples()
    {
        var policy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.ResponseFirst) with
        {
            GameCpuPercent = 50,
            GameGpuPercent = 80,
            GameSeconds = 4,
            TurboEnabled = false,
            Advanced = new() { CooldownSeconds = 1, MinimumDwellSeconds = 1 }
        };
        var sample = new AdaptiveTrialInput
        {
            CpuPercent = 60, GpuPercent = 5, CpuTemperature = 60, GpuTemperature = 60,
            AcConnected = true, BatteryPercent = 90, Current = AdaptiveStage.Office
        };
        var session = new AdaptiveRuleSession();
        var start = DateTimeOffset.FromUnixTimeSeconds(1_000);

        Assert.IsNull(session.Evaluate(policy, sample, start).Target);
        Assert.IsNull(session.Evaluate(policy, sample, start).Target);
        Assert.IsNull(session.Evaluate(policy, sample, start.AddSeconds(2)).Target);
        Assert.IsNull(session.Evaluate(policy, sample with { CpuPercent = null }, start.AddSeconds(3)).Target);
        Assert.IsNull(session.Evaluate(policy, sample, start.AddSeconds(4)).Target);
        Assert.IsNull(session.Evaluate(policy, sample, start.AddSeconds(7)).Target);
        Assert.AreEqual(AdaptiveStage.Game, session.Evaluate(policy, sample, start.AddSeconds(8)).Target);
    }

    [TestMethod]
    public void Changed_current_mode_rechecks_same_telemetry_without_reusing_old_candidate()
    {
        var policy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.ResponseFirst) with
        {
            GameCpuPercent = 50,
            GameGpuPercent = 80,
            GameSeconds = 1,
            TurboEnabled = false,
            Advanced = new() { CooldownSeconds = 1, MinimumDwellSeconds = 1 }
        };
        var sample = new AdaptiveTrialInput
        {
            CpuPercent = 60, GpuPercent = 5, CpuTemperature = 60, GpuTemperature = 60,
            AcConnected = true, BatteryPercent = 90, Current = AdaptiveStage.Office
        };
        var session = new AdaptiveRuleSession();
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_000);

        Assert.IsNull(session.Evaluate(policy, sample, timestamp).Target);
        var candidate = session.Evaluate(policy, sample, timestamp.AddSeconds(1));
        Assert.AreEqual(AdaptiveStage.Game, candidate.Target);
        Assert.IsNull(session.Evaluate(policy, sample with { Current = AdaptiveStage.Game }, timestamp.AddSeconds(1)).Target);
    }

    [TestMethod]
    public void Unknown_battery_saver_blocks_battery_upshift_but_low_battery_still_downshifts()
    {
        var policy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.BalancedAdaptive);
        var sample = new AdaptiveTrialInput
        {
            AcConnected = false, BatteryPercent = 85, BatterySaver = null,
            CpuPercent = 90, GpuPercent = 95, CpuTemperature = 60, GpuTemperature = 60,
            Current = AdaptiveStage.Office, ConditionSeconds = 60, SinceSwitchSeconds = 200
        };

        Assert.IsNull(AdaptiveRuleTrial.Evaluate(policy, sample).Target);
        Assert.AreEqual(AdaptiveStage.Office,
            AdaptiveRuleTrial.Evaluate(policy, sample with { BatteryPercent = 10, Current = AdaptiveStage.Game }).Target);
    }

    [TestMethod]
    public async Task Failed_automatic_apply_retries_after_backoff_but_success_stays_deduplicated()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2_000));
        var calls = 0;
        var target = PresetKey.Create(ControlModeId.Gaming, 2);
        var executor = new AdaptivePresetExecutor((_, _) =>
        {
            calls++;
            var state = calls == 1 ? CommandState.Rejected : CommandState.Applied;
            return Task.FromResult(new AdaptivePresetApplyResult(
                new CommandResult(Guid.NewGuid(), state, null, RequiredUserAction.None, null, false),
                PartialReason: calls > 1 ? "CPU 调校缺少硬件读回" : null));
        }, clock);

        Assert.IsTrue(executor.TryStartApply(target, CancellationToken.None, out var first));
        Assert.AreEqual(CommandState.Rejected, (await first)?.Command.State);
        Assert.IsFalse(executor.TryStartApply(target, CancellationToken.None, out _));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.IsTrue(executor.TryStartApply(target, CancellationToken.None, out var retry));
        AdaptivePresetApplyResult? result = await retry;
        Assert.AreEqual(CommandState.Applied, result?.Command.State);
        Assert.IsTrue(result?.IsPartial);
        Assert.IsFalse(executor.TryStartApply(target, CancellationToken.None, out _));
        Assert.AreEqual(2, calls);
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
