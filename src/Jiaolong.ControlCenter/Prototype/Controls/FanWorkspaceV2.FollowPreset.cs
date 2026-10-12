using Jiaolong.Contracts.Models;
using System.Text.Json;
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
    private long appliedFanRevision = -1;

    public async Task PausePresetFollowingAsync(CancellationToken token)
    {
        while (applyingPreset || savingPreset)
            await Task.Delay(40, token);
    }

    public void ResumePresetFollowing() { }

    private async void OnFollowPresetChanged(object? sender, bool enabled)
    {
        if (applyingPreset || savingPreset) { PresetToolbar.SetFollowPreset(IsFollowingPreset); return; }
        try { followPreferences.Update(value => value with { FanFollowPreset = enabled }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PresetToolbar.SetFollowPreset(IsFollowingPreset); await PresetToolbar.ShowStatusAsync("保存跟随设置失败"); return; }
        IsFollowingPreset = enabled;
        if (Catalog is not null) await Catalog.FollowPreferenceChangedAsync(enabled);
        if (!enabled) pendingFanFollowTarget = null;
    }

    public async void SetFollowPresetTarget(PresetKey key)
    {
        await Task.CompletedTask;
        followFanTarget = key;
        if (session?.State?.Controls.ExecutionState?.Pages.FirstOrDefault(p => p.Page == ControlPageId.Fan) is { } page)
        {
            if (page.State == Jiaolong.Contracts.Models.ApplyState.Confirmed) PresetToolbar.SetCurrentPreset(page.Key);
            else if (page.Key is { } submitted) PresetToolbar.SetSubmittedPreset(submitted);
        }
    }

    private async void UpdateFanActiveBadge(HomeStateSnapshot state)
    {
        if (liveFanQueued) return;
        if (state.Controls.ExecutionState is { } execution && execution.Pages.FirstOrDefault(p => p.Page == ControlPageId.Fan) is { Key: { } key } page)
        {
            if (page.State != Jiaolong.Contracts.Models.ApplyState.Confirmed)
            { PresetToolbar.SetSubmittedPreset(key); PresetToolbar.SetConfirmedActivePreset(null); return; }
            if (appliedFanKey != key || appliedFanRevision != page.Revision)
            {
                appliedFanKey = key; appliedFanRevision = page.Revision; appliedFanPreset = null;
                try
                {
                    var saved = await presetStore.LoadAsync(ControlPageId.Fan, key, CancellationToken.None);
                    if (appliedFanKey != key || appliedFanRevision != page.Revision) return;
                    if (session?.State?.Controls.ExecutionState?.Pages.Any(p => p.Page == ControlPageId.Fan &&
                        p.Key == key && p.Revision == page.Revision && p.State == Jiaolong.Contracts.Models.ApplyState.Confirmed) != true)
                    { appliedFanRevision = -1; return; }
                    if (saved is { SchemaVersion: PagePresetEnvelope.CurrentSchemaVersion } && saved.Revision == page.Revision)
                        appliedFanPreset = saved.Payload.Deserialize<FanCurveState>();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                { AppRuntimeLog.Write($"Fan applied preset read: {error.Message}"); }
            }
        }
        var expected = appliedFanPreset;
        state = session?.State ?? state;
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
        FixedTarget.Visibility = FixedTargetSlider.Visibility = fixedSpeed ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        MaximumRpmTarget.Visibility = AutoStrategy.IsChecked == true && MaximumRpmToggle.IsOn ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        FanCurveWorkspace.Opacity = curve ? 1 : 0.48;
        FanCurveWorkspace.SetEditingEnabled(curve && FanHardwareAvailable);
        FanCurveWorkspace.SetPresetCaption(!curve
            ? fixedSpeed ? "固定转速控制 · 曲线未运行" : "EC 自动控制 · 参考曲线未运行"
            : session?.State?.Controls.ActiveFanControlPlan?.Strategy == "Curve"
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
