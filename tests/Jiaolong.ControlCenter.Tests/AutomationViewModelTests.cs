using Jiaolong_ControlCenter.ViewModels;
using Jiaolong_ControlCenter.Services;
using Jiaolong.Contracts.Models;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AutomationViewModelTests
{
    [TestMethod]
    public void Priority_explanation_orders_thermal_conflict_power_lock_manual_app_load_baseline()
    {
        CollectionAssert.AreEqual(
            new[] { "thermal", "compatibilityOrConflict", "dc", "sessionLock", "manualOverride", "foregroundApp", "adaptiveLoad", "baseline" },
            AutomationPriority.DisplayOrder);
    }

    [TestMethod]
    public void Choosing_a_strategy_does_not_use_it_until_explicitly_applied()
    {
        var selection = new AdaptiveStrategySelection();

        selection.Choose(AdaptiveStrategyId.QuietFirst);
        Assert.AreEqual(AdaptiveStrategyId.QuietFirst, selection.Editing);
        Assert.AreEqual(AdaptiveStrategyId.BalancedAdaptive, selection.Active);

        selection.UseEditing();
        Assert.AreEqual(AdaptiveStrategyId.QuietFirst, selection.Active);
    }

    [TestMethod]
    public void Decision_preview_never_reports_stale_or_missing_sensor_values_as_live()
    {
        var now = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        var stale = new HardwareSnapshot(now.AddSeconds(-30), "Ready", null, null, null, null)
        { CpuUsagePercent = 82, GpuUsagePercent = 74, AcPowerConnected = true };

        var display = AdaptiveDecisionDisplay.From(stale, now);
        Assert.AreEqual("--", display.CpuUsage);
        Assert.AreEqual("--", display.GpuUsage);
        Assert.AreEqual("供电待确认", display.Power);

        var fresh = stale with { CapturedAtUtc = now, CpuUsagePercent = 42, GpuUsagePercent = 38 };
        display = AdaptiveDecisionDisplay.From(fresh, now);
        Assert.AreEqual("42%", display.CpuUsage);
        Assert.AreEqual("38%", display.GpuUsage);
        Assert.AreEqual("AC 供电", display.Power);
    }

    [TestMethod]
    public void Target_mapping_keeps_ac_and_dc_separate_and_rejects_turbo_on_battery()
    {
        var map = AdaptiveTargetMap.Recommended;
        var batteryPreset = PresetKey.Create(ControlModeId.Gaming, 3);

        map = map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Game, batteryPreset);
        Assert.AreEqual(batteryPreset, map.DcGame);
        Assert.AreEqual(PresetKey.Create(ControlModeId.Gaming, 2), map.AcGame);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Turbo, PresetKey.Create(ControlModeId.Turbo, 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Office, PresetKey.Create(ControlModeId.Turbo, 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Game, PresetKey.Create(ControlModeId.Custom3, 2)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Game, PresetKey.Create(ControlModeId.Custom2, 3)));
    }

    [TestMethod]
    public void Recommended_trigger_policies_use_responsive_downshift_timing()
    {
        var quiet = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.QuietFirst);
        var balanced = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.BalancedAdaptive);
        var responsive = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.ResponseFirst);

        Assert.AreEqual((55, 45, 15, false, 30),
            (quiet.GameCpuPercent, quiet.GameGpuPercent, quiet.GameSeconds, quiet.TurboEnabled, quiet.OfficeSeconds));
        Assert.AreEqual((40, 35, 8, true, 85, 90, 30, 20),
            (balanced.GameCpuPercent, balanced.GameGpuPercent, balanced.GameSeconds,
             balanced.TurboEnabled, balanced.TurboCpuPercent, balanced.TurboGpuPercent,
             balanced.TurboSeconds, balanced.OfficeSeconds));
        Assert.AreEqual((30, 25, 4, 75, 85, 15, 15),
            (responsive.GameCpuPercent, responsive.GameGpuPercent, responsive.GameSeconds,
             responsive.TurboCpuPercent, responsive.TurboGpuPercent, responsive.TurboSeconds,
             responsive.OfficeSeconds));
    }

    [TestMethod]
    public void Trigger_policy_rejects_invalid_hysteresis_and_range()
    {
        var policy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.BalancedAdaptive);
        policy.Validate();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            (policy with { GameCpuPercent = 101 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            (policy with { OfficeGpuPercent = policy.GameGpuPercent }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            (policy with { TurboCpuPercent = policy.GameCpuPercent }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            (policy with { GameSeconds = 0 }).Validate());
    }
}
