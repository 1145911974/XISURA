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
    private PresetKey? lastFollowedTarget;
    private KeyboardLightingPlan? lastFollowedPlan;
    private string? followStatus;
    private bool followReapplyPending;
    private readonly DispatcherTimer followPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly AdaptivePresetExecutor lightingFollower;

    private void InitializeLightingControl()
    {
        var settings = preferences.Load();
        followPreset = settings.LightingFollowPreset;
        independentDraft = LightingDraft.FromPlan(settings.IndependentLighting);
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
        lightingSlots[key.Mode.ToString()] = key.Slot;
        try { preferences.Update(current => current with { LightingPresetSlots = new(lightingSlots) }); }
        catch (Exception error) { AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting slot storage: {error}\n"); }
        InvalidateLightingFollow();
        _ = FollowCurrentPresetAsync();
    }

    private void InvalidateLightingFollow()
    {
        followReapplyPending = true;
        followStatus = null;
        lastFollowedPlan = null;
    }

    private async Task FollowCurrentPresetAsync()
    {
        if (!followPreset || followSettingPending || applying || saving || session?.State is not { } state || !lightingAvailable || !session.ConfigurationRestorationSettled) return;
        followTarget = LightingPresetPolicy.ResolveTarget(state.Controls, lightingSlots, confirmedCustomLighting, confirmedPerformanceLighting);
        if (lightingFollower.IsApplying || followTarget is not { } target) return;
        if (followReapplyPending) { lightingFollower.Reset(); followReapplyPending = false; }
        if (!lightingFollower.TryStartApply(target, CancellationToken.None, out var completion)) return;
        try
        {
            var result = await completion;
            if (result is not null && followPreset && followTarget == target)
                HardwareStatusText.Text = followStatus = result.Command is { State: CommandState.Applied, Error: null }
                    ? "跟随预设 · 已使用对应模式的灯光方案"
                    : result.PartialReason ?? "跟随预设未生效，已保留当前灯效";
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting preset follow: {error}\n");
            HardwareStatusText.Text = followStatus = "跟随预设通信失败，已保留当前灯效";
        }
        finally
        {
            if (followPreset && (followReapplyPending || followTarget != target)) _ = FollowCurrentPresetAsync();
        }
    }

    private async Task<AdaptivePresetApplyResult> ApplyFollowedPresetAsync(PresetKey target, CancellationToken token)
    {
        lastFollowedPlan = null;
        var envelope = await presets.LoadAsync(ControlPageId.Lighting, target, token);
        var stored = envelope?.SchemaVersion == 1 ? envelope.Payload.Deserialize<LightingDraft>() : null;
        if (stored?.IsValid() != true)
            return new(RejectedLighting(), "当前模式没有有效的已保存灯光预设，保持现有灯效");
        CancelPendingHardwarePreview();
        var plan = stored.ToPlan() with { LogoEnabled = null };
        var result = await ApplySavedLightingAsync(plan,
            () => followPreset && !followSettingPending && session?.State is { } current &&
                LightingPresetPolicy.ResolveTarget(current.Controls, lightingSlots, confirmedCustomLighting, confirmedPerformanceLighting) == target, token);
        if (result is { State: CommandState.Applied, Error: null }) { lastFollowedTarget = target; lastFollowedPlan = plan; PresetToolbar.SetConfirmedActivePreset(target); }
        return new(result);
    }

    private async void OnFollowPresetClick(object sender, RoutedEventArgs e)
    {
        if (syncing || applying || loading || followSettingPending) { FollowPresetButton.IsChecked = followPreset;
        PresetToolbar.SetFollowPreset(followPreset); return; }
        bool requested = FollowPresetButton.IsChecked == true;
        if (requested == followPreset) return;
        followSettingPending = true;
        FollowPresetButton.IsEnabled = EditorHost.IsEnabled = PresetToolbar.IsEnabled = false;
        CancelPendingHardwarePreview();
        try
        {
            if (!requested)
            {
                drafts[editing] = draft;
                await RestoreHardwarePreviewAsync();
                var independent = LightingDraft.FromPlan(session?.State?.Controls.KeyboardLighting);
                if (independent is null)
                { await PresetToolbar.ShowStatusAsync("灯光当前设置尚未读回，暂不能切换实时控制"); return; }
                independentDraft = draft = independent;
                Render();
            }
            else await RestoreHardwarePreviewAsync();
            preferences.Update(current => current with
            {
                LightingFollowPreset = requested,
                IndependentLighting = independentDraft is { } retained ? retained.ToPlan() with { LogoEnabled = null } : null,
                LightingPresetSlots = new(lightingSlots)
            });
            followPreset = requested;
            if (!requested) PresetToolbar.SetConfirmedActivePreset(null);
            followStatus = null; lastFollowedPlan = null; lastFollowedTarget = null;
            InvalidateLightingFollow();
            if (requested) await SelectPresetAsync(editing, false);
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting control selection: {error}\n");
            await PresetToolbar.ShowStatusAsync("切换未完成，请检查服务或设置存储");
        }
        finally
        {
            followSettingPending = false;
            if (followPreset) followPoll.Start(); else followPoll.Stop();
            FollowPresetButton.IsChecked = followPreset;
        PresetToolbar.SetFollowPreset(followPreset);
            FollowPresetButton.IsEnabled = EditorHost.IsEnabled = PresetToolbar.IsEnabled = true;
            UpdateActionAvailability();
        }
        await FollowCurrentPresetAsync();
    }

    private static CommandResult RejectedLighting() => new(Guid.NewGuid(), CommandState.Rejected, null,
        RequiredUserAction.None, ServiceError.Create(ErrorCode.ValidationFailed, Guid.NewGuid(), false), false);
}
