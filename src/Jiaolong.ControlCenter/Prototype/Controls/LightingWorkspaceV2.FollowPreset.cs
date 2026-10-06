using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class LightingWorkspaceV2
{
    private bool followPreset;
    private bool followSettingPending;
    private LightingDraft? independentDraft;
    private Dictionary<string, int> lightingSlots = [];
    private PresetKey? confirmedCustomLighting;
    private PresetKey? confirmedPerformanceLighting;
    public void SetConfirmedPerformanceTarget(PresetKey? key) => confirmedPerformanceLighting = key;
    private PresetKey? followTarget;
    private PresetKey? lastAutomaticTarget;
    private PresetKey? lastFollowedTarget;
    private KeyboardLightingPlan? lastFollowedPlan;
    private string? followStatus;
    private readonly DispatcherTimer followPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly AdaptivePresetExecutor lightingFollower;

    private void InitializeLightingControl()
    {
        var settings = preferences.Load();
        followPreset = settings.LightingFollowPreset;
        lightingSlots = settings.LightingPresetSlots ?? [];
        FollowPresetButton.IsChecked = followPreset;
        PresetToolbar.SetFollowPreset(followPreset);
        followPoll.Tick += (_, _) => _ = FollowCurrentPresetAsync();
        Loaded += (_, _) => { if (followPreset) followPoll.Start(); };
        Unloaded += (_, _) => followPoll.Stop();
    }

    public void SetFollowPresetTarget(PresetKey key)
    {
        if (!PresetKey.All.Contains(key)) return;
        if (key.Mode is ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3) confirmedCustomLighting = key;
        // The notification carries the computer mode, not a lighting slot selection.
        _ = FollowCurrentPresetAsync();
    }

    private async Task FollowCurrentPresetAsync()
    {
        if (!followPreset || followSettingPending || applying || saving || loading || colorDragging || lightingSliderDragging || colorFocused || pendingHardwarePreview is not null || PresetToolbar.IsEditingPreset || session?.State is not { } state || !lightingAvailable || !session.ConfigurationRestorationSettled) return;
        followTarget = LightingPresetPolicy.ResolveTarget(state.Controls, lightingSlots, confirmedCustomLighting, confirmedPerformanceLighting);
        if (lightingFollower.IsApplying || followTarget is not { } target || target == lastAutomaticTarget) return;
        lightingFollower.Reset();
        if (!lightingFollower.TryStartApply(target, CancellationToken.None, out var completion)) return;
        // Remember the transition, not readback equality: polling cannot erase manual changes.
        lastAutomaticTarget = target;
        try
        {
            var result = await completion;
            if (result is not null && followPreset && followTarget == target)
                HardwareStatusText.Text = followStatus = result.Command is { State: CommandState.Applied, Error: null }
                    ? "已随模式应用对应灯光"
                    : result.PartialReason ?? "自动应用未生效，已保留当前灯效；可手动重试";
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting preset follow: {error}\n");
            HardwareStatusText.Text = followStatus = "自动应用通信失败，已保留当前灯效；可手动重试";
        }
        finally
        {
            if (followPreset && followTarget != target) _ = FollowCurrentPresetAsync();
        }
    }

    private async Task<AdaptivePresetApplyResult> ApplyFollowedPresetAsync(PresetKey target, CancellationToken token)
    {
        var envelope = await presets.LoadAsync(ControlPageId.Lighting, target, token);
        var stored = envelope is null ? LightingDraft.Default : envelope.SchemaVersion == 1 ? envelope.Payload.Deserialize<LightingDraft>() : null;
        if (stored?.IsValid() != true)
            return new(RejectedLighting(), "当前模式没有有效的已保存灯光预设，保持现有灯效");
        var plan = stored.ToPlan() with { LogoEnabled = null };
        var result = await ApplySavedLightingAsync(plan,
            () => followPreset && !followSettingPending && !PresetToolbar.IsEditingPreset && session?.State is { } current &&
                LightingPresetPolicy.ResolveTarget(current.Controls, lightingSlots, confirmedCustomLighting, confirmedPerformanceLighting) == target, token);
        if (result is { State: CommandState.Applied, Error: null })
        {
            lastFollowedTarget = target; lastFollowedPlan = plan;
            independentDraft = LightingDraft.FromPlan(plan);
            PresetToolbar.SetCurrentPreset(target);
            PresetToolbar.SetConfirmedActivePreset(target);
            if (!PresetToolbar.IsEditingPreset) { draft = independentDraft!; Render(); }
        }
        return new(result);
    }

    private async void OnFollowPresetClick(object sender, RoutedEventArgs e)
    {
        if (syncing || applying || loading || followSettingPending) { FollowPresetButton.IsChecked = followPreset; return; }
        bool requested = FollowPresetButton.IsChecked == true;
        if (requested == followPreset) return;
        try
        {
            await RestoreHardwarePreviewAsync();
            followSettingPending = true; FollowPresetButton.IsEnabled = false;
            preferences.Update(current => current with { LightingFollowPreset = requested });
            followPreset = requested;
            // Enabling automation applies once; disabling keeps the real current settings.
            lastAutomaticTarget = null;
            followStatus = null;
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting automation selection: {error}\n");
            await PresetToolbar.ShowStatusAsync("切换未完成，请检查设置存储");
        }
        finally
        {
            followSettingPending = false;
            if (followPreset) followPoll.Start(); else followPoll.Stop();
            FollowPresetButton.IsChecked = followPreset;
            PresetToolbar.SetFollowPreset(followPreset);
            FollowPresetButton.IsEnabled = true;
            UpdateActionAvailability();
        }
        await FollowCurrentPresetAsync();
    }

    private static CommandResult RejectedLighting() => new(Guid.NewGuid(), CommandState.Rejected, null,
        RequiredUserAction.None, ServiceError.Create(ErrorCode.ValidationFailed, Guid.NewGuid(), false), false);
}
