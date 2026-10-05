using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private GpuWorkspacePreset? selectedGpuPreset;
    private bool gpuPresetApplying;
    private bool gpuPresetDirty, gpuPresetSaving;
    private bool GpuWritePending => gpuPresetApplying || gpuPresetSaving;
    private MuxMode? muxDraftMode;

    private void OnMuxModeRequested(object? sender, MuxMode mode)
    {
        if (GpuWritePending || session?.State is not { } state || !CapabilityAvailable(state, "muxMode")) return;
        muxDraftMode = mode;
        RouteDiagram.SetDraftMode(mode);
        MarkPresetDirty();
    }

    private sealed record GpuWorkspacePreset(
        int? CoreFrequencyLimitMhz,
        int? MemoryOffsetKhz,
        int? CoreOffsetKhz,
        int[]? VfOffsetsKhz)
    {
        public MuxMode? MuxMode { get; init; }
    }

    private async void OnGpuPresetSelected(object? sender, PresetKey key) { if (!gpuFollowSelecting) await LoadGpuPresetAsync(key); }

    private async Task LoadGpuPresetAsync(PresetKey key)
    {
        if (!IsFollowingPreset) return;
        if (GpuWritePending) return;
        if (gpuPresetDirty && gpuEditingKey is { } previousKey) gpuEditorDrafts[previousKey] = CaptureGpuEditor(false);
        gpuEditingKey = key;
        int revision = ++gpuPresetLoadVersion;
        selectedGpuPreset = null;
        PresetToolbar.SetActionAvailability(true, false);
        try
        {
            var saved = await presetStore.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None);
            if (!IsFollowingPreset || revision != gpuPresetLoadVersion || PresetToolbar.SelectedKey != key) return;
            if (saved is null) { await PresetToolbar.ShowStatusAsync("此档位尚无 GPU 预设"); return; }
            var draft = saved.Payload.Deserialize<GpuWorkspacePreset>() ??
                (saved.Payload.Deserialize<GpuDraft>() is { } legacy
                    ? new GpuWorkspacePreset(legacy.CoreFrequencyLimitMhz, null, null, null)
                    : null);
            if (draft is null || !ValidPreset(draft)) { await PresetToolbar.ShowStatusAsync("GPU 预设格式无效或没有有效项目"); return; }
            selectedGpuPreset = draft;
            bool retained = gpuEditorDrafts.TryGetValue(key, out var editorDraft);
            ApplyGpuPresetDraft(retained ? editorDraft! : draft);
            gpuPresetDirty = retained;
            PresetToolbar.SetEditingState(key, dirty: retained, saved: true);
            await PresetToolbar.ShowTransientStatusAsync("预设已载入");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await PresetToolbar.ShowStatusAsync("读取 GPU 预设失败");
        }
        finally
        {
            if (PresetToolbar.SelectedKey == key) PresetToolbar.SetActionAvailability(true, selectedGpuPreset is not null && !gpuPresetDirty);
        }
    }

    private void ApplyGpuPresetDraft(GpuWorkspacePreset draft)
    {
        gpuPresetLoading = true;
        try
        {
            muxDraftMode = draft.MuxMode;
            RouteDiagram.SetDraftMode(muxDraftMode);
            if (draft.CoreFrequencyLimitMhz is int clock) { CoreRail.SetValue(clock); CoreValueBox.Value = clock; }
            if (draft.MemoryOffsetKhz is int memory) { MemoryRail.SetValue(memory / 1000d); MemoryValueBox.Value = memory / 1000d; }
            if (draft.CoreOffsetKhz is int core) { CoreOffsetRail.SetValue(core / 1000d); CoreOffsetValueBox.Value = core / 1000d; }
            if (draft.VfOffsetsKhz is { Length: 127 } offsets && lastGpuVf is { Nodes.Length: 127 } hardware)
            {
                gpuVfDraft = offsets.ToArray();
                gpuVfDirty = !offsets.SequenceEqual(hardware.Nodes.Select(node => node.OffsetKhz));
                VoltageFrequencyCurve.ApplyState(new GpuCurveState(true, null,
                    hardware.MinimumOffsetKhz / 1000d, hardware.MaximumOffsetKhz / 1000d,
                    hardware.Nodes.Select((node, i) => new GpuCurveNode(node.VoltageMv, node.BaseFrequencyMhz, offsets[i] / 1000d)).ToArray()));
            }
        }
        finally { gpuPresetLoading = false; }
        PresetToolbar.SetActionAvailability(true, true);
        _ = PresetToolbar.SetDirtyStatusAsync(false);
    }

    private async Task SaveGpuPresetAsync()
    {
        if (!IsFollowingPreset) return;
        if (GpuWritePending) return;
        gpuPresetSaving = true;
        SetGpuPresetControlsEnabled(false);
        try
        {
            var hardware = session?.State;
            var vf = hardware is not null && CapabilityAvailable(hardware, "gpuVfCurve") && lastGpuVf is { Nodes.Length: 127, Error: null }
                ? gpuVfDraft ?? lastGpuVf.Nodes.Select(node => node.OffsetKhz).ToArray() : null;
            var draft = new GpuWorkspacePreset(
                clockReady && clockInitialized ? (int)Math.Round(CoreRail.Value ?? CoreValueBox.Value) : null,
                memoryReady ? (int)Math.Round((MemoryRail.Value ?? MemoryValueBox.Value) * 1000d) : null,
                coreOffsetReady ? (int)Math.Round(CoreOffsetValueBox.Value * 1000d) : null,
                vf)
            {
                MuxMode = muxDraftMode ?? hardware?.Controls.MuxMode
            };
            if (!ValidPreset(draft)) { await PresetToolbar.ShowStatusAsync("没有已读回且有效的 GPU 设置可保存"); return; }
            var key = PresetToolbar.SelectedKey;
            await presetStore.SaveAsync(new PagePresetEnvelope(1, ControlPageId.Gpu, key,
                PresetToolbar.SelectedDisplayName, System.Text.Json.JsonSerializer.SerializeToElement(draft), DateTimeOffset.UtcNow), CancellationToken.None);
            if (appliedGpuKey == key && (appliedGpuPreset is not { } applied ||
                applied.CoreFrequencyLimitMhz != draft.CoreFrequencyLimitMhz || applied.MemoryOffsetKhz != draft.MemoryOffsetKhz ||
                applied.CoreOffsetKhz != draft.CoreOffsetKhz || applied.MuxMode != draft.MuxMode ||
                !(applied.VfOffsetsKhz is null ? draft.VfOffsetsKhz is null :
                    draft.VfOffsetsKhz is not null && applied.VfOffsetsKhz.SequenceEqual(draft.VfOffsetsKhz))))
            {
                appliedGpuKey = null; appliedGpuPreset = null;
                PresetToolbar.SetConfirmedActivePreset(null);
            }
            selectedGpuPreset = draft;
            gpuEditorDrafts.Remove(key);
            gpuPresetDirty = false;
            PresetToolbar.SetEditingState(key, false, true);
            PresetToolbar.SetActionAvailability(true, true);
            await PresetToolbar.ShowSavedStatusAsync();
        }
        catch { await PresetToolbar.ShowStatusAsync("保存 GPU 预设失败"); }
        finally { gpuPresetSaving = false; SetGpuPresetControlsEnabled(true); }
    }

    private async void OnUseGpuPreset(object? sender, EventArgs e) => await ApplyGpuPresetAsync();

    private async Task ApplyGpuPresetAsync(GpuWorkspacePreset? livePreset = null, bool automatic = false)
    {
        var activeSession = session;
        var preset = livePreset ?? selectedGpuPreset;
        if (GpuWritePending || preset is null || activeSession?.State is not { } state) return;
        if (livePreset is null && !IsFollowingPreset) return;
        if (livePreset is null && gpuPresetDirty) { await PresetToolbar.ShowStatusAsync("请先保存修改，再使用预设"); return; }
        var key = PresetToolbar.SelectedKey;
        var currentVf = state.Controls.GpuVf;
        bool invalid = !ValidPreset(preset) ||
            (preset.CoreFrequencyLimitMhz is int clockValue && state.Controls.GpuClockLimit is { Error: null } clockRange &&
                (clockValue < clockRange.MinimumMhz || clockValue > clockRange.MaximumMhz)) ||
            (preset.MemoryOffsetKhz is int memoryValue && currentVf?.MemoryMinimumOffsetKhz is int memoryMin &&
                currentVf.MemoryMaximumOffsetKhz is int memoryMax && (memoryValue < memoryMin || memoryValue > memoryMax)) ||
            (preset.CoreOffsetKhz is int coreValue && currentVf?.CoreMinimumOffsetKhz is int coreMin &&
                currentVf.CoreMaximumOffsetKhz is int coreMax && (coreValue < coreMin || coreValue > coreMax)) ||
            (preset.VfOffsetsKhz is { } vfValues && currentVf is not null &&
                vfValues.Any(value => value < currentVf.MinimumOffsetKhz || value > currentVf.MaximumOffsetKhz));
        if (invalid) { await PresetToolbar.ShowStatusAsync("预设超出当前驱动范围，未发送任何项目"); return; }
        var actions = new List<(string Name, Func<Task<CommandResult>> Apply)>();
        var skipped = new List<string>();

        if (preset.CoreFrequencyLimitMhz is int clock)
        {
            if (!CapabilityAvailable(state, "gpuFrequencyLimit") || state.Controls.GpuClockLimit is not { Error: null } range) skipped.Add("频率上限：能力不可用");
            else if (clock < range.MinimumMhz || clock > range.MaximumMhz) skipped.Add("频率上限：超出当前驱动范围");
            else actions.Add(("频率上限", () => activeSession.ExecuteAsync(new SetGpuFrequencyLimitCommand(Guid.NewGuid(), clock, true), CancellationToken.None)));
        }

        var vf = state.Controls.GpuVf;
        if (preset.MemoryOffsetKhz is int memory)
        {
            if (!CapabilityAvailable(state, "gpuMemoryOffset") || vf?.MemoryOffsetKhz is not int old) skipped.Add("显存偏移：能力或读回不可用");
            else actions.Add(("显存偏移", () => activeSession.ExecuteAsync(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), old, memory, true), CancellationToken.None)));
        }
        if (preset.CoreOffsetKhz is int core)
        {
            if (!CapabilityAvailable(state, "gpuCoreOffset") || vf?.CoreOffsetKhz is not int old) skipped.Add("核心偏移：能力或读回不可用");
            else actions.Add(("核心偏移", () => activeSession.ExecuteAsync(new SetGpuCoreOffsetCommand(Guid.NewGuid(), old, core, true), CancellationToken.None)));
        }
        if (preset.VfOffsetsKhz is { Length: 127 } offsets)
        {
            if (!CapabilityAvailable(state, "gpuVfCurve") || vf is not { Nodes.Length: 127 }) skipped.Add("V/F 曲线：能力或读回不可用");
            else actions.Add(("V/F 曲线", () =>
            {
                var latest = activeSession.State?.Controls.GpuVf;
                if (latest is not { Nodes.Length: 127 } || latest.Error is not null)
                    return Task.FromResult(new CommandResult(Guid.NewGuid(), CommandState.Rejected, null, RequiredUserAction.None, null, false));
                return activeSession.ExecuteAsync(new SetGpuVfCurveCommand(Guid.NewGuid(), latest.Nodes.Select(node => node.OffsetKhz).ToArray(), offsets, true), CancellationToken.None);
            }));
        }
        if (preset.MuxMode is MuxMode mux && state.Controls.MuxMode != mux)
        {
            if (!CapabilityAvailable(state, "muxMode")) skipped.Add("输出模式：能力不可用");
            else actions.Add(("输出模式", async () =>
            {
                var result = await activeSession.ExecuteAsync(new SetMuxModeCommand(Guid.NewGuid(), mux, true), CancellationToken.None);
                if (result.State == CommandState.Applied && result.Error is null)
                {
                    muxDraftMode = null;
                    RouteDiagram.SetDraftMode(null);
                    RouteDiagram.SetMode(mux);
                    RouteDiagram.AnimateAcceptedRequest(mux, result.RequiredAction == RequiredUserAction.Restart);
                }
                return result;
            }));
        }
        if (actions.Count == 0) { await PresetToolbar.ShowStatusAsync("没有可应用的已验证 GPU 能力" + (skipped.Count == 0 ? "" : "；未应用：" + string.Join("、", skipped))); return; }

        gpuPresetApplying = true;
        SetGpuPresetControlsEnabled(false);
        int applied = 0;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "使用 GPU 预设",
                Content = $"将按顺序提交 {actions.Count} 项；未应用项：{(skipped.Count == 0 ? "无" : string.Join("、", skipped))}。每步成功后不会自动回滚，后续失败时已应用项保留。功耗和限频未读回时无法恢复未知旧值。{(preset.MuxMode is not null && preset.MuxMode != state.Controls.MuxMode ? "输出模式最后提交，重启后生效；软件不会自动重启。" : "")}",
                PrimaryButtonText = "确认应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (!automatic && await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            foreach (var action in actions)
            {
                var result = await action.Apply();
                if (result.State != CommandState.Applied || result.Error is not null)
                {
                    await PresetToolbar.ShowStatusAsync($"已应用 {applied}/{actions.Count} 项；{action.Name}失败，后续未应用。未应用项：{string.Join("、", skipped)}；已应用项保留");
                    return;
                }
                applied++;
                switch (action.Name)
                {
                    case "频率上限": clockDirty = false; break;
                    case "显存偏移": memoryDraftDirty = false; break;
                    case "核心偏移": coreOffsetDirty = false; break;
                    case "V/F 曲线": gpuVfDirty = false; lastGpuVf = null; break;
                }
            }
            appliedGpuPreset = livePreset is null && skipped.Count == 0 ? preset : null;
            appliedGpuKey = appliedGpuPreset is null ? null : key;
            UpdateGpuActiveBadge(activeSession.State);
            await PresetToolbar.ShowStatusAsync($"已提交 {applied} 项；未应用项：{(skipped.Count == 0 ? "无" : string.Join("、", skipped))}。驱动状态将随后刷新");
        }
        catch { await PresetToolbar.ShowStatusAsync($"已应用 {applied}/{actions.Count} 项；请求中断，已应用项保留"); }
        finally
        {
            gpuPresetApplying = false;
            if (activeSession.State is { } latest) ApplyState(latest);
            SetGpuPresetControlsEnabled(true);
        }
    }

    private void SetGpuPresetControlsEnabled(bool enabled)
    {
        PresetToolbar.IsEnabled = enabled;
        RouteDiagram.SetAvailable(enabled && session?.State is { } muxState && CapabilityAvailable(muxState, "muxMode"));
        PresetToolbar.SetActionAvailability(enabled, enabled && selectedGpuPreset is not null && !gpuPresetDirty);
        CoreRail.IsEnabled = CoreValueBox.IsEnabled = enabled && clockReady && !clockPending;

        MemoryRail.IsEnabled = MemoryValueBox.IsEnabled = enabled && memoryReady && !memoryPending;

        CoreOffsetRail.IsEnabled = CoreOffsetValueBox.IsEnabled = enabled && coreOffsetReady && !coreOffsetPending;



        VoltageFrequencyCurve.IsEnabled = enabled && session?.State is { } current && CapabilityAvailable(current, "gpuVfCurve");
    }

    private static bool ValidPreset(GpuWorkspacePreset draft)
    {
        bool memoryValid = draft.MemoryOffsetKhz is not int memory || memory is >= -200_000 and <= 200_000 && memory % 1000 == 0;
        bool coreValid = draft.CoreOffsetKhz is not int core || core is >= -200_000 and <= 200_000 && core % 1000 == 0;
        return (draft.CoreFrequencyLimitMhz is null or > 0) && memoryValid && coreValid &&
            (draft.MuxMode is null || Enum.IsDefined(draft.MuxMode.Value)) &&
            (draft.VfOffsetsKhz is null || draft.VfOffsetsKhz is { Length: 127 } vf && vf[0] == 0 && vf.All(value => value is >= -200_000 and <= 200_000)) &&
            (draft.CoreFrequencyLimitMhz is not null || draft.MemoryOffsetKhz is not null || draft.CoreOffsetKhz is not null ||
             draft.VfOffsetsKhz is not null || draft.MuxMode is not null);
    }
}
