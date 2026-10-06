using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private readonly UserPreferencesStore followPreferences = new();
    public bool IsFollowingPreset { get; private set; } = true;
    private int liveGpuRevision;
    private bool liveGpuQueued;
    private bool gpuRailPointerHeld;
    private bool gpuFollowSelecting;
    private int gpuPresetLoadVersion;
    private readonly Dictionary<PresetKey, GpuWorkspacePreset> gpuEditorDrafts = [];
    private PresetKey? gpuEditingKey;
    private PresetKey? followGpuTarget;
    private PresetKey? pendingGpuFollowTarget;
    private PresetKey? appliedGpuKey;
    private GpuWorkspacePreset? appliedGpuPreset;
    private bool gpuFollowingSuspended;
    private int gpuFollowOperations;

    public async Task PausePresetFollowingAsync(CancellationToken token)
    {
        gpuFollowingSuspended = true;
        while (gpuFollowOperations > 0 || GpuWritePending)
            await Task.Delay(40, token);
    }

    public void ResumePresetFollowing() => gpuFollowingSuspended = false;

    private GpuWorkspacePreset CaptureGpuEditor(bool changedOnly) => new(
        clockReady && (!changedOnly || clockDirty) ? (int)Math.Round(CoreValueBox.Value) : null,
        memoryReady && (!changedOnly || memoryDraftDirty) ? (int)Math.Round(MemoryValueBox.Value * 1000) : null,
        coreOffsetReady && (!changedOnly || coreOffsetDirty) ? (int)Math.Round(CoreOffsetValueBox.Value * 1000) : null,
        gpuVfDirty ? gpuVfDraft?.ToArray() : changedOnly ? null : lastGpuVf?.Nodes.Select(node => node.OffsetKhz).ToArray());

    private async void OnFollowPresetChanged(object? sender, bool enabled)
    {
        if (GpuWritePending) { PresetToolbar.SetFollowPreset(IsFollowingPreset); return; }
        try { followPreferences.Update(value => value with { GpuFollowPreset = enabled }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PresetToolbar.SetFollowPreset(IsFollowingPreset); await PresetToolbar.ShowStatusAsync("保存跟随设置失败"); return; }
        IsFollowingPreset = enabled;
    }

    public async void SetFollowPresetTarget(PresetKey key)
    {
        if (gpuFollowingSuspended) return;
        gpuFollowOperations++;
        try
        {
            var slots = followPreferences.Load().GpuPresetSlots;
            key = PresetKey.Create(key.Mode, slots.TryGetValue(key.Mode.ToString(), out int slot) && slot is >= 1 and <= 3 ? slot : 2);
            if (followGpuTarget?.Mode == key.Mode) return;
            followGpuTarget = key;
            if (!IsFollowingPreset || session is null) return;
            if (PresetToolbar.IsEditingPreset) { pendingGpuFollowTarget = key; return; }
            while (GpuWritePending || gpuEditorLoading)
            {
                await Task.Delay(40);
                if (gpuFollowingSuspended || !IsFollowingPreset || followGpuTarget != key) return;
                if (PresetToolbar.IsEditingPreset) { pendingGpuFollowTarget = key; return; }
            }
            gpuFollowSelecting = true;
            PresetToolbar.SelectedKey = key;
            gpuFollowSelecting = false;
            await LoadGpuPresetAsync(key, restoreEditor: false);
            if (!gpuFollowingSuspended && IsFollowingPreset && followGpuTarget == key && selectedGpuPreset is not null && !gpuPresetDirty)
                await ApplyGpuPresetAsync(automatic: true);
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] GPU preset follow: {error}\n");
        }
        finally { gpuFollowOperations--; }
    }

    private void RestoreLiveGpuState()
    {
        if (session?.State is not { } state) return;
        clockDirty = memoryDraftDirty = coreOffsetDirty = gpuVfDirty = false;
        clockInitialized = false;
        memoryAppliedKhz = coreOffsetAppliedKhz = null;
        lastGpuVf = null; gpuVfDraft = null;
        RouteDiagram.SetDraftMode(null);
        ApplyState(state);

    }

    private async void QueueLiveGpuChange()
    {
        int revision = ++liveGpuRevision;
        liveGpuQueued = true;
        PresetToolbar.SetConfirmedActivePreset(null);
        await Task.Delay(220);
        while (gpuRailPointerHeld && revision == liveGpuRevision) await Task.Delay(40);
        if (revision != liveGpuRevision || PresetToolbar.IsEditingPreset) { if (revision == liveGpuRevision) liveGpuQueued = false; return; }
        while (GpuWritePending) { await Task.Delay(40); if (revision != liveGpuRevision || PresetToolbar.IsEditingPreset) return; }
        // Curve edits remain explicit; scalar adjustments must not submit the unfinished curve.
        try { await ApplyGpuPresetAsync(CaptureGpuEditor(true) with { VfOffsetsKhz = null }, automatic: true); }
        catch (Exception) { await PresetToolbar.ShowStatusAsync("GPU 实时调整未确认，请检查连接"); }
        finally
        {
            if (revision == liveGpuRevision) { liveGpuQueued = false; if (!gpuVfDirty) RestoreLiveGpuState(); }
        }
    }

    private void UpdateGpuActiveBadge(HomeStateSnapshot? state)
    {
        if (liveGpuQueued || gpuVfDirty && !PresetToolbar.IsEditingPreset) return;
        PresetToolbar.SetConfirmedActivePreset(appliedGpuPreset is { } preset && GpuPresetMatchesReadback(preset, state) ? appliedGpuKey : null);
    }

    private static bool GpuPresetMatchesReadback(GpuWorkspacePreset preset, HomeStateSnapshot? state)
    {
        // NVAPI exposes submitted clock limits, not verified clock-limit readback.
        if (state is null || preset.CoreFrequencyLimitMhz is not null) return false;
        var vf = state.Controls.GpuVf;
        return (preset.MemoryOffsetKhz is null || preset.MemoryOffsetKhz == vf?.MemoryOffsetKhz) &&
            (preset.CoreOffsetKhz is null || preset.CoreOffsetKhz == vf?.CoreOffsetKhz) &&
            (preset.VfOffsetsKhz is null || vf is { Error: null } && preset.VfOffsetsKhz.SequenceEqual(vf.Nodes.Select(node => node.OffsetKhz)));
    }
}
