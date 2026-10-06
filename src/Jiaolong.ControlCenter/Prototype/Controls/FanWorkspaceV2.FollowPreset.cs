using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class FanWorkspaceV2
{
    private readonly UserPreferencesStore followPreferences = new();
    public bool IsFollowingPreset { get; private set; } = true;
    private int liveFanRevision;
    private bool liveFanQueued;
    private bool fanSliderPointerHeld;
    private PresetKey? followFanTarget;
    private PresetKey? pendingFanFollowTarget;
    private PresetKey? appliedFanKey;
    private FanCurveState? appliedFanPreset;

    private async void OnFollowPresetChanged(object? sender, bool enabled)
    {
        if (applyingPreset || savingPreset) { PresetToolbar.SetFollowPreset(IsFollowingPreset); return; }
        try { followPreferences.Update(value => value with { FanFollowPreset = enabled }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PresetToolbar.SetFollowPreset(IsFollowingPreset); await PresetToolbar.ShowStatusAsync("保存跟随设置失败"); return; }
        IsFollowingPreset = enabled;
    }

    public async void SetFollowPresetTarget(PresetKey key)
    {
        var slots = followPreferences.Load().FanPresetSlots;
        key = PresetKey.Create(key.Mode, slots.TryGetValue(key.Mode.ToString(), out int slot) && slot is >= 1 and <= 3 ? slot : 2);
        if (followFanTarget?.Mode == key.Mode) return;
        followFanTarget = key;
        if (!IsFollowingPreset || session is null) return;
        if (PresetToolbar.IsEditingPreset) { pendingFanFollowTarget = key; return; }
        while (applyingPreset || savingPreset || fanEditorLoading)
        {
            await Task.Delay(40);
            if (!IsFollowingPreset || followFanTarget != key) return;
            if (PresetToolbar.IsEditingPreset) { pendingFanFollowTarget = key; return; }
        }
        editingKey = key;
        syncingPreset = true; PresetToolbar.SelectedKey = key; syncingPreset = false;
        if (IsFollowingPreset && followFanTarget == key) await ApplyFanStateAsync();
    }

    private void UpdateFanActiveBadge(HomeStateSnapshot state)
    {
        if (liveFanQueued) return;
        var expected = appliedFanPreset;
        var plan = state.Controls.ActiveFanControlPlan;
        bool matches = expected is not null && state.Controls.StrongCooling != true &&
            (expected.Strategy == "Auto" && expected.MaximumRpm is null ? plan is null :
                plan is not null && plan.Strategy == expected.Strategy && plan.MaximumRpm == expected.MaximumRpm &&
                (expected.Strategy != "Fixed" || plan.FixedRpm == expected.FixedRpm) &&
                (expected.Strategy != "Curve" ||
                 plan.Points.SequenceEqual((expected.IsShared ? expected.Shared : expected.Cpu).Select(p => new FanPoint(p.Temperature, p.TargetPercent))) &&
                 (plan.GpuPoints ?? plan.Points).SequenceEqual((expected.IsShared ? expected.Shared : expected.Gpu).Select(p => new FanPoint(p.Temperature, p.TargetPercent)))));
        PresetToolbar.SetConfirmedActivePreset(matches ? appliedFanKey : null);
    }

    private void RestoreLiveFanState(HomeStateSnapshot state)
    {
        var defaults = new FanCurveDraft(editingKey.Mode == ControlModeId.Office ? 0 : editingKey.Mode == ControlModeId.Turbo ? 2 : 1);
        var plan = state.Controls.ActiveFanControlPlan;
        var cpu = plan?.Points.Select(point => new CurvePoint(point.TemperatureC, point.Percent)).ToArray() ?? defaults.Cpu.ToArray();
        var gpu = plan?.GpuPoints?.Select(point => new CurvePoint(point.TemperatureC, point.Percent)).ToArray() ?? cpu;
        RestoreDraft(new FanCurveState(defaults.Profile, plan?.GpuPoints is null, cpu, gpu, cpu,
            plan?.Strategy ?? "Auto", plan?.FixedRpm ?? 3000) { MaximumRpm = plan?.MaximumRpm });
        UpdateLiveFanPresentation();
    }

    private void UpdateLiveFanPresentation()
    {
        bool curve = CurveStrategy.IsChecked == true;
        bool fixedSpeed = FixedStrategy.IsChecked == true;
        LiveFanUnknownText.Text = fixedSpeed ? "当前使用固定转速，未运行温度曲线。" :
            "当前由 EC 自动控制，固件曲线无法读取。\n选择“温度曲线”后，可从推荐曲线开始设置。";
        FixedTarget.Visibility = FixedTargetSlider.Visibility = fixedSpeed ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        MaximumRpmTarget.Visibility = AutoStrategy.IsChecked == true && MaximumRpmToggle.IsOn ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        LiveFanUnknownPanel.Visibility = curve ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        FanCurveWorkspace.Visibility = curve ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        FanCurveWorkspace.IsEnabled = curve && session?.State?.Capabilities.Items.Any(item => item.Key == "fanControl" && item.State == CapabilityState.Available) == true;
        FanCurveWorkspace.SetPresetCaption(session?.State?.Controls.ActiveFanControlPlan?.Strategy == "Curve"
            ? "当前硬件曲线" : "推荐起点，修改后实时应用");
    }

    private async void QueueLiveFanChange()
    {
        int revision = ++liveFanRevision;
        liveFanQueued = true;
        PresetToolbar.SetConfirmedActivePreset(null);
        await Task.Delay(220);
        while (fanSliderPointerHeld && revision == liveFanRevision) await Task.Delay(40);
        if (revision != liveFanRevision || PresetToolbar.IsEditingPreset) { if (revision == liveFanRevision) liveFanQueued = false; return; }
        while (applyingPreset || savingPreset) { await Task.Delay(40); if (revision != liveFanRevision || PresetToolbar.IsEditingPreset) return; }
        try { await ApplyFanStateAsync(CaptureDraft()); }
        catch (Exception) { await PresetToolbar.ShowStatusAsync("风扇实时调整未确认，请检查连接"); }
        finally
        {
            if (revision == liveFanRevision)
            {
                liveFanQueued = false;
                if (session?.State is { } latest) RestoreLiveFanState(latest);
            }
        }
    }
}
