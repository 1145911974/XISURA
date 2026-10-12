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
    private PresetKey? lastAutomaticTarget;
    private PresetKey? lastFollowedTarget;
    private KeyboardLightingPlan? lastFollowedPlan;
    private string? followStatus;
    private readonly DispatcherTimer followPoll = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializeLightingControl()
    {
        var settings = preferences.Load();
        followPreset = settings.LightingFollowPreset;
        lightingSlots = settings.LightingPresetSlots ?? [];
        PresetToolbar.ConfigureFollowPreset(followPreset);
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
        await Task.CompletedTask;
        if (session?.State?.Controls.ExecutionState?.Pages.FirstOrDefault(p => p.Page == ControlPageId.Lighting) is { } page)
        {
            if (page.State == Jiaolong.Contracts.Models.ApplyState.Confirmed) PresetToolbar.SetCurrentPreset(page.Key);
            else if (page.Key is { } submitted) PresetToolbar.SetSubmittedPreset(submitted);
        }
    }

    private async void OnFollowPresetClick(object? sender, bool requested)
    {
        if (syncing || applying || loading || followSettingPending) { PresetToolbar.SetFollowPreset(followPreset); return; }
        if (requested == followPreset) return;
        try
        {
            await RestoreHardwarePreviewAsync();
            followSettingPending = true;
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
            PresetToolbar.SetFollowPreset(followPreset);
            UpdateActionAvailability();
        }
        if (Catalog is not null) await Catalog.FollowPreferenceChangedAsync(requested);
        await FollowCurrentPresetAsync();
    }

    private static CommandResult RejectedLighting() => new(Guid.NewGuid(), CommandState.Rejected, null,
        RequiredUserAction.None, ServiceError.Create(ErrorCode.ValidationFailed, Guid.NewGuid(), false), false);
}
