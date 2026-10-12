using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;
using Jiaolong_ControlCenter.Services;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private GpuWorkspacePreset? selectedGpuPreset;
    private bool gpuPresetApplying;
    private bool gpuPresetDirty, gpuPresetSaving;
    private bool gpuEditorLoading;
    private bool gpuEditorResetClock;
    private bool GpuWritePending => gpuPresetApplying || gpuPresetSaving;

    private async void OnMuxModeRequested(object? sender, MuxMode mode)
    {
        if (GpuWritePending || session?.State is not { } state || !CapabilityAvailable(state, "muxMode")) return;
        if (state.Controls.MuxMode == mode) return;
        gpuPresetApplying = true;
        SetGpuPresetControlsEnabled(false);
        try
        {
            var result = await session.ExecuteAsync(new SetMuxModeCommand(Guid.NewGuid(), mode, true), CancellationToken.None);
            if (result.State != CommandState.Applied || result.Error is not null)
            { await PresetToolbar.ShowStatusAsync("输出模式切换失败，请检查设备状态"); return; }
            bool restart = result.RequiredAction == RequiredUserAction.Restart;
            RouteDiagram.AnimateAcceptedRequest(mode, restart);
            if (!restart && session.State is { } current) ApplyState(current);
            if (restart)
            {
                string ModeName(MuxMode? value) => value == MuxMode.Discrete ? "独显直连" : value == MuxMode.Hybrid ? "混合输出" : "等待设备报告";
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "输出模式已设置",
                    Content = $"当前生效：{ModeName(state.Controls.MuxMode)}\n已设置：{ModeName(mode)}，重启电脑后生效。",
                    CloseButtonText = "稍后重启"
                };
                await Jiaolong_ControlCenter.Services.ContentDialogCoordinator.ShowAsync(dialog);
            }
        }
        catch (Exception) { await PresetToolbar.ShowStatusAsync("输出模式请求中断，请检查连接"); }
        finally
        {
            gpuPresetApplying = false; SetGpuPresetControlsEnabled(true);
        }
    }

    private sealed record GpuWorkspacePreset(
        int? CoreFrequencyLimitMhz,
        int? MemoryOffsetKhz,
        int? CoreOffsetKhz,
        int[]? VfOffsetsKhz)
    {
        public MuxMode? MuxMode { get; init; }
        public bool ResetCoreFrequencyLimit { get; init; }
    }

    private async void OnGpuPresetSelected(object? sender, PresetKey key) { if (PresetToolbar.IsEditingPreset) await LoadGpuPresetAsync(key); }

    private async Task LoadGpuPresetAsync(PresetKey key, bool? restoreEditor = null)
    {
        if (GpuWritePending) return;
        bool edit = restoreEditor ?? PresetToolbar.IsEditingPreset;
        if (edit && !gpuEditorLoading && gpuPresetDirty && gpuEditingKey is { } previousKey) gpuEditorDrafts[previousKey] = CaptureGpuEditor(false);
        await (Catalog?.FlushAsync() ?? Task.CompletedTask);
        gpuEditingKey = key;
        int revision = ++gpuPresetLoadVersion;
        gpuEditorLoading = true;
        selectedGpuPreset = null;
        SetGpuPresetControlsEnabled(false);
        try
        {
            var saved = await presetStore.LoadAsync(ControlPageId.Gpu, key, CancellationToken.None);
            if (revision != gpuPresetLoadVersion || PresetToolbar.SelectedKey != key || edit && !PresetToolbar.IsEditingPreset) return;
            if (saved is null) { await PresetToolbar.ShowStatusAsync("此档位尚无 GPU 预设"); return; }
            if (saved.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion)
            { await PresetToolbar.ShowStatusAsync("预设来自更新版本，请升级应用；原文件未修改"); return; }
            var draft = saved.Payload.Deserialize<GpuWorkspacePreset>() ??
                (saved.Payload.Deserialize<GpuDraft>() is { } legacy
                    ? new GpuWorkspacePreset(legacy.CoreFrequencyLimitMhz, null, null, null)
                    : null);
            if (draft is null || !ValidPreset(draft)) { await PresetToolbar.ShowStatusAsync("GPU 预设格式无效或没有有效项目"); return; }
            selectedGpuPreset = draft;
            bool retained = edit && gpuEditorDrafts.TryGetValue(key, out _);
            if (edit) ApplyGpuPresetDraft(retained ? gpuEditorDrafts[key] : draft);
            if (edit) gpuPresetDirty = retained;
            PresetToolbar.SetEditingState(key, dirty: retained, saved: true);
            if (edit) await PresetToolbar.ShowTransientStatusAsync("编辑内容已载入，电脑设置未改变");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await PresetToolbar.ShowStatusAsync("读取 GPU 预设失败");
        }
        finally
        {
            if (revision == gpuPresetLoadVersion)
            { gpuEditorLoading = false; SetGpuPresetControlsEnabled(true); }
        }
    }

    private void ApplyGpuPresetDraft(GpuWorkspacePreset draft)
    {
        gpuPresetLoading = true;
        try
        {
            gpuEditorResetClock = draft.ResetCoreFrequencyLimit;
            CoreValueBox.Visibility = Visibility.Visible;
            CoreLiveUnknown.Visibility = Visibility.Collapsed;
            if (draft.CoreFrequencyLimitMhz is int clock) { CoreRail.SetValue(clock); CoreValueBox.Value = clock; }
            else if (draft.ResetCoreFrequencyLimit)
            {
                CoreRail.SetValue(CoreValueBox.Maximum);
                CoreValueBox.Value = CoreValueBox.Maximum;
                SetGpuHelpState(CoreFrequencyHelp, "恢复驱动默认 · 使用时解除软件限频");
            }
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
        PresetToolbar.SetActionAvailability(!gpuEditorLoading, !gpuEditorLoading);
        _ = PresetToolbar.SetDirtyStatusAsync(false);
    }

    private async Task<bool> SaveGpuPresetAsync(PresetKey? target = null)
    {
        if (GpuWritePending || gpuEditorLoading) return false;
        var key = target ?? (PresetToolbar.IsEditingPreset ? PresetToolbar.SelectedKey : PresetToolbar.CurrentSourceKey);
        if (key is null) { await PresetToolbar.ShowStatusAsync("请选择另存为的位置"); return false; }
        gpuPresetSaving = true;
        SetGpuPresetControlsEnabled(false);
        try
        {
            var hardware = session?.State;
            var draft = PresetToolbar.IsEditingPreset ? CaptureGpuEditor(false) : new GpuWorkspacePreset(null,
                    memoryReady ? hardware?.Controls.GpuVf?.MemoryOffsetKhz : null,
                    coreOffsetReady ? hardware?.Controls.GpuVf?.CoreOffsetKhz : null,
                    hardware?.Controls.GpuVf is { Nodes.Length: 127, Error: null } actual ? actual.Nodes.Select(node => node.OffsetKhz).ToArray() : null);
            if (!ValidPreset(draft)) { await PresetToolbar.ShowStatusAsync("没有已读回且有效的 GPU 设置可保存"); return false; }
            if (hardware is not null && !ValidForCurrentDevice(draft, hardware))
            { await PresetToolbar.ShowStatusAsync("GPU 设置超出当前驱动可用范围，原预设未改变"); return false; }
            if (Catalog is null) return false;
            await Catalog.FlushAsync();
            await Catalog.SavePayloadAsync(ControlPageId.Gpu, key.Value, PresetToolbar.DisplayNameFor(key.Value),
                System.Text.Json.JsonSerializer.SerializeToElement(draft), CancellationToken.None, copyCurrent: !PresetToolbar.IsEditingPreset);
            if (appliedGpuKey == key && (appliedGpuPreset is not { } applied ||
                applied.CoreFrequencyLimitMhz != draft.CoreFrequencyLimitMhz || applied.MemoryOffsetKhz != draft.MemoryOffsetKhz ||
                applied.ResetCoreFrequencyLimit != draft.ResetCoreFrequencyLimit ||
                applied.CoreOffsetKhz != draft.CoreOffsetKhz ||
                !(applied.VfOffsetsKhz is null ? draft.VfOffsetsKhz is null :
                    draft.VfOffsetsKhz is not null && applied.VfOffsetsKhz.SequenceEqual(draft.VfOffsetsKhz))))
            {
                appliedGpuKey = null; appliedGpuPreset = null;
                PresetToolbar.SetConfirmedActivePreset(null);
            }
            gpuEditorDrafts.Remove(key.Value);
            if (!PresetToolbar.IsEditingPreset || PresetToolbar.SelectedKey == key.Value)
            {
                selectedGpuPreset = draft;
                gpuPresetDirty = false;
                PresetToolbar.SetEditingState(key.Value, false, true);
            }
            if (!PresetToolbar.IsEditingPreset && GpuPresetMatchesReadback(draft, hardware))
            { appliedGpuPreset = draft; appliedGpuKey = key; PresetToolbar.SetCurrentPreset(key); }
            PresetToolbar.SetActionAvailability(true, true);
            await PresetToolbar.ShowSavedStatusAsync();
            return true;
        }
        catch { await PresetToolbar.ShowStatusAsync("保存 GPU 预设失败"); return false; }
        finally { gpuPresetSaving = false; SetGpuPresetControlsEnabled(true); }
    }

    private async void OnUseGpuPreset(object? sender, EventArgs e)
    {
        if (PresetToolbar.IsEditingPreset)
        {
            if (!await SaveGpuPresetAsync()) return;
            if (await ApplyGpuPresetAsync(automatic: true)) PresetToolbar.ExitPresetManagement();
        }
        else await ApplyGpuPresetAsync(CaptureGpuEditor(true), automatic: true);
    }

    private async void OnGpuPresetUseRequested(object? sender, PresetKey key)
    {
        if (GpuWritePending) return;
        if (PresetToolbar.IsEditingPreset) { await LoadGpuPresetAsync(key); return; }
        ++liveGpuRevision; liveGpuQueued = false;
        await LoadGpuPresetAsync(key, restoreEditor: false);
        if (selectedGpuPreset is not null && PresetToolbar.SelectedKey == key) await ApplyGpuPresetAsync(automatic: true);
    }

    private async void OnGpuEditingModeChanged(object? sender, bool editing)
    {
        ++liveGpuRevision; liveGpuQueued = false; ++gpuPresetLoadVersion;
        await (Catalog?.FlushAsync() ?? Task.CompletedTask);
        if (editing) await LoadGpuPresetAsync(PresetToolbar.SelectedKey);
        else
        {
            if (!gpuEditorLoading && gpuPresetDirty && gpuEditingKey is { } key) gpuEditorDrafts[key] = CaptureGpuEditor(false);
            gpuEditorLoading = false;
            gpuPresetDirty = false;
            RestoreLiveGpuState();
            SetGpuPresetControlsEnabled(true);
            if (pendingGpuFollowTarget is { } pending)
            { pendingGpuFollowTarget = null; followGpuTarget = null; SetFollowPresetTarget(pending); }
        }
    }

    private async void OnGpuSaveAsRequested(object? sender, PresetKey key) => await SaveGpuPresetAsync(key);

    private async void OnResetGpuPreset(object? sender, PresetKey key)
    {
        if (GpuWritePending || gpuEditorLoading) return;
        gpuPresetSaving = true;
        SetGpuPresetControlsEnabled(false);
        try
        {
            await (Catalog?.FlushAsync() ?? Task.CompletedTask);
            var envelope = await presetStore.ResetAsync(ControlPageId.Gpu, key, CancellationToken.None);
            if (Catalog is not null && session?.IsServiceConnected == true) await Catalog.PublishAsync(Catalog.CurrentLogicalMode(), CancellationToken.None);
            gpuEditorDrafts.Remove(key);
            if (appliedGpuKey == key) { appliedGpuKey = null; appliedGpuPreset = null; PresetToolbar.SetConfirmedActivePreset(null); }
            if (PresetToolbar.IsEditingPreset && PresetToolbar.SelectedKey == key)
            {
                selectedGpuPreset = envelope.Payload.Deserialize<GpuWorkspacePreset>();
                if (selectedGpuPreset is { } draft) ApplyGpuPresetDraft(draft);
                gpuPresetDirty = false;
                PresetToolbar.SetEditingState(key, false, true);
            }
            await PresetToolbar.ShowTransientStatusAsync("此预设已恢复默认推荐值");
        }
        catch { await PresetToolbar.ShowStatusAsync("恢复默认未完成，请重试"); }
        finally { gpuPresetSaving = false; SetGpuPresetControlsEnabled(true); }
    }

    private async Task<bool> ApplyGpuPresetAsync(GpuWorkspacePreset? livePreset = null, bool automatic = false)
    {
        if (livePreset is null && Catalog is not null)
        {
            var target = PresetToolbar.SelectedKey;
            var result = await Catalog.ApplyAsync(target.Mode, Catalog.NativeModeFor(target), ControlOrigin.PresetUse,
                ControlPageId.Gpu, target);
            if (result is null) return false;
            var page = result.ExecutionState?.Pages.FirstOrDefault(p => p.Page == ControlPageId.Gpu);
            if (page?.State == Jiaolong.Contracts.Models.ApplyState.Confirmed) PresetToolbar.SetCurrentPreset(target);
            else PresetToolbar.SetSubmittedPreset(target);
            return result is { State: CommandState.Applied, Error: null };
        }
        if (livePreset is not null)
        {
            Catalog?.RecordManualLive(ControlPageId.Gpu);
            IsFollowingPreset = false;
            PresetToolbar.SetFollowPreset(false);
        }
        var activeSession = session;
        var preset = livePreset ?? selectedGpuPreset;
        if (GpuWritePending || gpuEditorLoading || preset is null || activeSession?.State is not { } state) return false;
        var key = PresetToolbar.SelectedKey;
        bool invalid = !ValidPreset(preset) || !ValidForCurrentDevice(preset, state);
        if (invalid) { await PresetToolbar.ShowStatusAsync("预设超出当前驱动范围，未发送任何项目"); return false; }
        var actions = new List<(string Name, Func<Task<CommandResult>> Apply)>();
        var skipped = new List<string>();

        if (preset.ResetCoreFrequencyLimit || preset.CoreFrequencyLimitMhz is not null)
        {
            int? clock = preset.ResetCoreFrequencyLimit ? null : preset.CoreFrequencyLimitMhz;
            if (!state.Capabilities.Fields.Any(item => item.Key == "coreFrequencyLimitMhz" && item.Readable && item.Writable))
                skipped.Add("频率上限：当前锁频无法独立读回，已暂停写入");
            else if (!CapabilityAvailable(state, "gpuFrequencyLimit") || state.Controls.GpuClockLimit is not { Error: null } range) skipped.Add("频率上限：能力不可用");
            else if (clock is not null && (clock < range.MinimumMhz || clock > range.MaximumMhz)) skipped.Add("频率上限：超出当前驱动范围");
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
        if (actions.Count == 0) { await PresetToolbar.ShowStatusAsync("没有可应用的已验证 GPU 能力" + (skipped.Count == 0 ? "" : "；未应用：" + string.Join("、", skipped))); return false; }

        gpuPresetApplying = true;
        SetGpuPresetControlsEnabled(false);
        int applied = 0;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "使用 GPU 预设",
                Content = $"将按顺序提交 {actions.Count} 项；未应用项：{(skipped.Count == 0 ? "无" : string.Join("、", skipped))}。每步成功后不会自动回滚，后续失败时已应用项保留。功耗和限频未读回时无法恢复未知旧值。",
                PrimaryButtonText = "确认应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (!automatic && await Jiaolong_ControlCenter.Services.ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
            foreach (var action in actions)
            {
                var result = await action.Apply();
                if (result.State != CommandState.Applied || result.Error is not null)
                {
                    await PresetToolbar.ShowStatusAsync($"已应用 {applied}/{actions.Count} 项；{action.Name}失败，后续未应用。未应用项：{string.Join("、", skipped)}；已应用项保留");
                    return false;
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
            if (livePreset is null && skipped.Count == 0)
            {
                try { followPreferences.Update(value => value with { GpuPresetSlots = new(value.GpuPresetSlots) { [key.Mode.ToString()] = key.Slot } }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await PresetToolbar.ShowStatusAsync("配置已提交，但记忆档位保存失败"); }
            }
            UpdateGpuActiveBadge(activeSession.State);
            if (appliedGpuPreset is not null && !GpuPresetMatchesReadback(appliedGpuPreset, activeSession.State))
            {
                PresetToolbar.SetCurrentPreset(null);
                PresetToolbar.SetSubmittedPreset(key);
            }
            await PresetToolbar.ShowStatusAsync($"已提交 {applied} 项；未应用项：{(skipped.Count == 0 ? "无" : string.Join("、", skipped))}。驱动状态将随后刷新");
            return skipped.Count == 0;
        }
        catch { await PresetToolbar.ShowStatusAsync($"已应用 {applied}/{actions.Count} 项；请求中断，已应用项保留"); return false; }
        finally
        {
            gpuPresetApplying = false;
            if (!PresetToolbar.IsEditingPreset && activeSession.State is { } latest) ApplyState(latest);
            SetGpuPresetControlsEnabled(true);
            if (livePreset is not null && Catalog is not null) await Catalog.PublishManualLiveAsync();
        }
    }

    private void SetGpuPresetControlsEnabled(bool enabled)
    {
        enabled = enabled && !gpuEditorLoading;
        PresetToolbar.IsEnabled = enabled;
        RouteDiagram.SetAvailable(enabled && session?.State is { } muxState && CapabilityAvailable(muxState, "muxMode"));
        PresetToolbar.SetActionAvailability(enabled, enabled && (PresetToolbar.IsEditingPreset ? selectedGpuPreset is not null || gpuPresetDirty : gpuVfDirty));
        CoreRail.IsEnabled = CoreValueBox.IsEnabled = enabled && clockReady && !clockPending;

        MemoryRail.IsEnabled = MemoryValueBox.IsEnabled = enabled && memoryReady && !memoryPending;

        CoreOffsetRail.IsEnabled = CoreOffsetValueBox.IsEnabled = enabled && coreOffsetReady && !coreOffsetPending;



        VoltageFrequencyCurve.IsEnabled = enabled && session?.State is { } current && CapabilityAvailable(current, "gpuVfCurve");
    }

    private static bool ValidPreset(GpuWorkspacePreset draft)
    {
        if (!PresetValidation.ValidatePayload(ControlPageId.Gpu, JsonSerializer.SerializeToElement(draft)).IsValid) return false;
        bool memoryValid = draft.MemoryOffsetKhz is not int memory || memory is >= -200_000 and <= 200_000 && memory % 1000 == 0;
        bool coreValid = draft.CoreOffsetKhz is not int core || core is >= -200_000 and <= 200_000 && core % 1000 == 0;
        return (draft.CoreFrequencyLimitMhz is null or > 0) && memoryValid && coreValid &&
            !(draft.ResetCoreFrequencyLimit && draft.CoreFrequencyLimitMhz is not null) &&
            (draft.VfOffsetsKhz is null || draft.VfOffsetsKhz is { Length: 127 } vf && vf[0] == 0 && vf.All(value => value is >= -200_000 and <= 200_000)) &&
            (draft.ResetCoreFrequencyLimit || draft.CoreFrequencyLimitMhz is not null || draft.MemoryOffsetKhz is not null || draft.CoreOffsetKhz is not null ||
             draft.VfOffsetsKhz is not null);
    }

    private static bool ValidForCurrentDevice(GpuWorkspacePreset preset, HomeStateSnapshot state)
    {
        var vf = state.Controls.GpuVf;
        return !(preset.CoreFrequencyLimitMhz is int clock && state.Controls.GpuClockLimit is { Error: null } range && (clock < range.MinimumMhz || clock > range.MaximumMhz)) &&
            !(preset.MemoryOffsetKhz is int memory && vf?.MemoryMinimumOffsetKhz is int memoryMin && vf.MemoryMaximumOffsetKhz is int memoryMax && (memory < memoryMin || memory > memoryMax)) &&
            !(preset.CoreOffsetKhz is int core && vf?.CoreMinimumOffsetKhz is int coreMin && vf.CoreMaximumOffsetKhz is int coreMax && (core < coreMin || core > coreMax)) &&
            !(preset.VfOffsetsKhz is { } offsets && vf is not null && offsets.Any(value => value < vf.MinimumOffsetKhz || value > vf.MaximumOffsetKhz));
    }
}
