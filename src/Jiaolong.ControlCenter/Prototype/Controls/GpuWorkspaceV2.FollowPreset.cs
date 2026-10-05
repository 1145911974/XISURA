using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private readonly UserPreferencesStore followPreferences = new();
    public bool IsFollowingPreset { get; private set; } = true;
    private int liveGpuRevision;
    private bool liveGpuQueued;
    private bool gpuFollowSelecting;
    private int gpuPresetLoadVersion;
    private readonly Dictionary<PresetKey, GpuWorkspacePreset> gpuEditorDrafts = [];
    private PresetKey? gpuEditingKey;
    private GpuWorkspacePreset? preservedGpuDraft;
    private bool preservedGpuDirty;
    private PresetKey? followGpuTarget;
    private PresetKey? appliedGpuKey;
    private GpuWorkspacePreset? appliedGpuPreset;

    private GpuWorkspacePreset CaptureGpuEditor(bool changedOnly) => new(
        clockReady && (!changedOnly || clockDirty) ? (int)Math.Round(CoreValueBox.Value) : null,
        memoryReady && (!changedOnly || memoryDraftDirty) ? (int)Math.Round(MemoryValueBox.Value * 1000) : null,
        coreOffsetReady && (!changedOnly || coreOffsetDirty) ? (int)Math.Round(CoreOffsetValueBox.Value * 1000) : null,
        gpuVfDirty ? gpuVfDraft?.ToArray() : changedOnly ? null : lastGpuVf?.Nodes.Select(node => node.OffsetKhz).ToArray())
        { MuxMode = muxDraftMode };

    private async void OnFollowPresetChanged(object? sender, bool enabled)
    {
        if (GpuWritePending) { PresetToolbar.SetFollowPreset(IsFollowingPreset); return; }
        try { followPreferences.Update(value => value with { GpuFollowPreset = enabled }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PresetToolbar.SetFollowPreset(IsFollowingPreset); await PresetToolbar.ShowStatusAsync("保存跟随设置失败"); return; }
        if (IsFollowingPreset) { preservedGpuDraft = CaptureGpuEditor(false); preservedGpuDirty = gpuPresetDirty; }
        IsFollowingPreset = enabled;
        ++liveGpuRevision; ++gpuPresetLoadVersion; liveGpuQueued = false;
        CoreValueBox.Visibility = enabled ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        CoreLiveUnknown.Visibility = enabled ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        if (enabled)
        {
            if (preservedGpuDraft is { } draft)
            {
                ApplyGpuPresetDraft(draft); gpuPresetDirty = preservedGpuDirty;
                PresetToolbar.SetEditingState(PresetToolbar.SelectedKey, preservedGpuDirty, selectedGpuPreset is not null);
            }
            else await LoadGpuPresetAsync(PresetToolbar.SelectedKey);
        }
        else { PresetToolbar.SetConfirmedActivePreset(null); RestoreLiveGpuState(); }
    }

    public async void SetFollowPresetTarget(PresetKey key)
    {
        if (followGpuTarget == key) return;
        followGpuTarget = key;
        if (!IsFollowingPreset || session is null) return;
        while (GpuWritePending) { await Task.Delay(40); if (!IsFollowingPreset || followGpuTarget != key) return; }
        gpuFollowSelecting = true;
        PresetToolbar.SelectedKey = key;
        gpuFollowSelecting = false;
        await LoadGpuPresetAsync(key);
        if (IsFollowingPreset && followGpuTarget == key && selectedGpuPreset is not null && !gpuPresetDirty)
            await ApplyGpuPresetAsync(automatic: true);
    }

    private void RestoreLiveGpuState()
    {
        if (session?.State is not { } state) return;
        clockDirty = memoryDraftDirty = coreOffsetDirty = gpuVfDirty = false;
        clockInitialized = false;
        memoryAppliedKhz = coreOffsetAppliedKhz = null;
        lastGpuVf = null; gpuVfDraft = null; muxDraftMode = null;
        RouteDiagram.SetDraftMode(null);
        ApplyState(state);

    }

    private async void QueueLiveGpuChange()
    {
        int revision = ++liveGpuRevision;
        liveGpuQueued = true;
        PresetToolbar.SetConfirmedActivePreset(null);
        await Task.Delay(220);
        if (revision != liveGpuRevision || IsFollowingPreset) return;
        while (GpuWritePending) { await Task.Delay(40); if (revision != liveGpuRevision || IsFollowingPreset) return; }
        try { await ApplyGpuPresetAsync(CaptureGpuEditor(true), automatic: true); }
        catch (Exception) { await PresetToolbar.ShowStatusAsync("GPU 实时调整未确认，请检查连接"); }
        finally
        {
            if (revision == liveGpuRevision) { liveGpuQueued = false; RestoreLiveGpuState(); }
        }
    }

    private void UpdateGpuActiveBadge(HomeStateSnapshot? state) => PresetToolbar.SetConfirmedActivePreset(
        IsFollowingPreset && appliedGpuPreset is { } preset && GpuPresetMatchesReadback(preset, state) ? appliedGpuKey : null);

    private static bool GpuPresetMatchesReadback(GpuWorkspacePreset preset, HomeStateSnapshot? state)
    {
        // NVAPI exposes submitted clock limits, not verified clock-limit readback.
        if (state is null || preset.CoreFrequencyLimitMhz is not null) return false;
        var vf = state.Controls.GpuVf;
        return (preset.MemoryOffsetKhz is null || preset.MemoryOffsetKhz == vf?.MemoryOffsetKhz) &&
            (preset.CoreOffsetKhz is null || preset.CoreOffsetKhz == vf?.CoreOffsetKhz) &&
            (preset.MuxMode is null || preset.MuxMode == state.Controls.MuxMode) &&
            (preset.VfOffsetsKhz is null || vf is { Error: null } && preset.VfOffsetsKhz.SequenceEqual(vf.Nodes.Select(node => node.OffsetKhz)));
    }
}
