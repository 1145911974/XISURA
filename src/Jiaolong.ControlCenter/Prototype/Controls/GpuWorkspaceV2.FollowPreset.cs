using Jiaolong.Contracts.Models;
using System.Text.Json;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private readonly UserPreferencesStore followPreferences = new();
    public bool IsFollowingPreset { get; private set; } = true;
    private int liveGpuRevision;
    private bool liveGpuQueued;
    private bool gpuRailPointerHeld;
    private int gpuPresetLoadVersion;
    private readonly Dictionary<PresetKey, GpuWorkspacePreset> gpuEditorDrafts = [];
    private PresetKey? gpuEditingKey;
    private PresetKey? followGpuTarget;
    private PresetKey? pendingGpuFollowTarget;
    private PresetKey? appliedGpuKey;
    private GpuWorkspacePreset? appliedGpuPreset;
    private long appliedGpuRevision = -1;

    public async Task PausePresetFollowingAsync(CancellationToken token)
    {
        while (GpuWritePending)
            await Task.Delay(40, token);
    }

    public void ResumePresetFollowing() { }

    private GpuWorkspacePreset CaptureGpuEditor(bool changedOnly)
    {
        bool editing = PresetToolbar.IsEditingPreset;
        return new(
            editing && !clockReady ? selectedGpuPreset?.CoreFrequencyLimitMhz :
                clockReady && (!changedOnly || clockDirty) && !gpuEditorResetClock ? (int)Math.Round(CoreValueBox.Value) : null,
            editing && !memoryReady ? selectedGpuPreset?.MemoryOffsetKhz :
                memoryReady && (!changedOnly || memoryDraftDirty) ? (int)Math.Round(MemoryValueBox.Value * 1000) : null,
            editing && !coreOffsetReady ? selectedGpuPreset?.CoreOffsetKhz :
                coreOffsetReady && (!changedOnly || coreOffsetDirty) ? (int)Math.Round(CoreOffsetValueBox.Value * 1000) : null,
            editing ? (gpuVfDirty ? gpuVfDraft : selectedGpuPreset?.VfOffsetsKhz)?.ToArray() :
                gpuVfDirty ? gpuVfDraft?.ToArray() : changedOnly ? null : lastGpuVf?.Nodes.Select(node => node.OffsetKhz).ToArray())
            { ResetCoreFrequencyLimit = gpuEditorResetClock && (!changedOnly || clockDirty) };
    }

    private async void OnFollowPresetChanged(object? sender, bool enabled)
    {
        if (GpuWritePending) { PresetToolbar.SetFollowPreset(IsFollowingPreset); return; }
        try { followPreferences.Update(value => value with { GpuFollowPreset = enabled }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PresetToolbar.SetFollowPreset(IsFollowingPreset); await PresetToolbar.ShowStatusAsync("保存跟随设置失败"); return; }
        IsFollowingPreset = enabled;
        if (Catalog is not null) await Catalog.FollowPreferenceChangedAsync(enabled);
    }

    public async void SetFollowPresetTarget(PresetKey key)
    {
        await Task.CompletedTask;
        // State notifications update presentation only; service owns the followed plan.
        followGpuTarget = key;
        if (session?.State?.Controls.ExecutionState?.Pages.FirstOrDefault(p => p.Page == ControlPageId.Gpu) is { } page)
        {
            if (page.State == Jiaolong.Contracts.Models.ApplyState.Confirmed) PresetToolbar.SetCurrentPreset(page.Key);
            else if (page.Key is { } submitted) PresetToolbar.SetSubmittedPreset(submitted);
        }
    }

    private void RestoreLiveGpuState()
    {
        if (session?.State is not { } state) return;
        gpuEditorResetClock = false;
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

    private async void UpdateGpuActiveBadge(HomeStateSnapshot? state)
    {
        if (liveGpuQueued || gpuVfDirty && !PresetToolbar.IsEditingPreset) return;
        if (state?.Controls.ExecutionState is { } execution && execution.Pages.FirstOrDefault(p => p.Page == ControlPageId.Gpu) is { Key: { } key } page)
        {
            if (page.State != Jiaolong.Contracts.Models.ApplyState.Confirmed)
            { PresetToolbar.SetSubmittedPreset(key); PresetToolbar.SetConfirmedActivePreset(null); return; }
            if (appliedGpuKey != key || appliedGpuRevision != page.Revision)
            {
                appliedGpuKey = key; appliedGpuRevision = page.Revision; appliedGpuPreset = null;
                try
                {
                    var saved = await presetStore.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None);
                    if (appliedGpuKey != key || appliedGpuRevision != page.Revision) return;
                    if (session?.State?.Controls.ExecutionState?.Pages.Any(p => p.Page == ControlPageId.Gpu &&
                        p.Key == key && p.Revision == page.Revision && p.State == Jiaolong.Contracts.Models.ApplyState.Confirmed) != true)
                    { appliedGpuRevision = -1; return; }
                    if (saved is { SchemaVersion: PagePresetEnvelope.CurrentSchemaVersion } && saved.Revision == page.Revision)
                        appliedGpuPreset = saved.Payload.Deserialize<GpuWorkspacePreset>();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                { AppRuntimeLog.Write($"GPU applied preset read: {error.Message}"); }
            }
        }
        PresetToolbar.SetConfirmedActivePreset(appliedGpuPreset is { } preset && GpuPresetMatchesReadback(preset, session?.State ?? state) ? appliedGpuKey : null);
    }

    private static bool GpuPresetMatchesReadback(GpuWorkspacePreset preset, HomeStateSnapshot? state)
    {
        // NVAPI exposes submitted clock limits, not verified clock-limit readback.
        if (state is null || preset.ResetCoreFrequencyLimit || preset.CoreFrequencyLimitMhz is not null) return false;
        var vf = state.Controls.GpuVf;
        return (preset.MemoryOffsetKhz is null || preset.MemoryOffsetKhz == vf?.MemoryOffsetKhz) &&
            (preset.CoreOffsetKhz is null || preset.CoreOffsetKhz == vf?.CoreOffsetKhz) &&
            (preset.VfOffsetsKhz is null || vf is { Error: null } && preset.VfOffsetsKhz.SequenceEqual(vf.Nodes.Select(node => node.OffsetKhz)));
    }
}
