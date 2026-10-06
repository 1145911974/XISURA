using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class PrototypeWindow
{
    private ControlModeId? confirmedControlMode;
    private PresetKey? confirmedPerformancePreset;
    private DateTimeOffset? confirmedAutomaticApply;
    private string? confirmedCpuSignature;
    private PerformanceMode? confirmedFirmwareMode;
    private static string? CpuSignature(CpuTuningState? cpu) => cpu is null ? null : JsonSerializer.Serialize(cpu with { WindowsPowerSchemeName = null });
    private string? trayIconPath;
    private string? taskbarIconPath;
    private readonly RuntimeWindowIcon runtimeWindowIcon = new();
    private int presetCatalogRevision;
    private bool strategyActivationPending;
    private readonly Dictionary<PresetKey, (string Name, bool Available, string? Reason)> trayPresetCatalog = [];
    private readonly Dictionary<PresetKey, PerformanceDraft> savedPerformanceDrafts = [];

    private void OnTrayThemeChanged(string theme)
    {
        try { userPreferences = preferences.Update(current => current with { TrayTheme = theme }); }
        catch (Exception error)
        {
            Services.AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Tray theme save: {error}\n");
            userPreferences = preferences.Load();
            trayQuickConsole?.ApplyTheme(userPreferences.TrayTheme, animate: true);
            trayQuickConsole?.ShowStatus("主题未保存，已恢复原样式");
        }
    }

    private void ApplyLogoStyle(string style)
    {
        userPreferences = preferences.Load();
        Hero.SetBrandingState(userPreferences.LogoStyle, confirmedControlMode, animate: false);
        trayQuickConsole?.ApplyLogoStyle(userPreferences.LogoStyle);
        PublishSystemIcons();
    }

    private void PublishConfirmedMode(ControlModeId? mode, PresetKey? preset, bool animate)
    {
        if (preset is { } key && key.Mode != mode) preset = null;
        bool targetChanged = confirmedControlMode != mode || preset is not null && confirmedPerformancePreset != preset;
        confirmedControlMode = mode;
        confirmedPerformancePreset = preset;
        PerformanceWorkspaceV2Preview.SetConfirmedActivePreset(preset);
        Hero.SetBrandingState(userPreferences.LogoStyle, mode, animate);
        trayQuickConsole?.SetConfirmedSelection(mode, preset, animate);
        PublishSystemIcons();
        if (targetChanged && mode is { } targetMode && turboBranch?.ActiveTier is null)
        {
            var target = preset ?? RememberedPerformancePreset(targetMode);
            if (target is { } keyToFollow)
            {
                GpuWorkspaceV2.SetFollowPresetTarget(keyToFollow);
                PageWorkspace.SetFanPresetTarget(keyToFollow);
                PageWorkspace.SetLightingPresetTarget(keyToFollow);
            }
        }
    }

    private void PublishSystemIcons()
    {
        string path = TrayIconAssetCatalog.ResolveAbsolute(AppContext.BaseDirectory, userPreferences.LogoStyle, confirmedControlMode);
        if (!File.Exists(path))
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Branding icon missing: {path}\n");
            return;
        }
        if (trayIconPath != path && tray.UpdateIcon(path)) trayIconPath = path;
        if (taskbarIconPath == path) return;
        try
        {
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            TaskbarGroupIcon.Apply(window);
            runtimeWindowIcon.Apply(window, path);
            taskbarIconPath = path;
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Taskbar icon update: {error}\n");
        }
    }

    private void ReconcileConfirmedBranding(HomeStateSnapshot snapshot, bool animate)
    {
        PublishSystemIcons();
        if (snapshot.Controls.PerformanceMode is not { } hardwareMode || customActivationPending || isModeCommandPending ||
            queuedModeRequest is not null || modeReconciliation.Current is not null) return;
        var automation = snapshot.Controls.AdaptiveAutomation;
        if (automation is { Enabled: true, Running: true, LastError: null, CurrentTarget: { } target, LastApplyUtc: { } appliedAt } &&
            (confirmedAutomaticApply is null || appliedAt > confirmedAutomaticApply) && AdaptiveTargetMap.HardwareModeFor(target) == hardwareMode)
        {
            confirmedAutomaticApply = appliedAt;
            if (savedPerformanceDrafts.TryGetValue(target, out var saved) && PerformanceWorkspaceV2Preview.PresetMatchesReadback(saved))
            {
                ConfirmPerformancePreset(target, animate);
                return;
            }
        }
        if (HasConfirmedPerformanceIdentity(snapshot)) return;
        ControlModeId? observed = hardwareMode switch
        {
            PerformanceMode.Quiet => ControlModeId.Office,
            PerformanceMode.Balanced => ControlModeId.Gaming,
            PerformanceMode.Turbo => ControlModeId.Turbo,
            PerformanceMode.Custom when confirmedControlMode is ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3 => confirmedControlMode,
            _ => null
        };
        if (observed != confirmedControlMode)
            PublishConfirmedMode(observed, null, animate && confirmedControlMode is not null);
        else if (confirmedPerformancePreset is not null && confirmedCpuSignature != CpuSignature(snapshot.Controls.CpuTuning))
            PublishConfirmedMode(hardwareMode == PerformanceMode.Custom ? null : observed, null, animate: false);
    }

    private void ConfirmPerformancePreset(PresetKey key, bool animate)
    {
        if (homeSession.State?.Controls.PerformanceMode is not { } firmwareMode) return;
        confirmedFirmwareMode = firmwareMode;
        if (key.Mode is ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3)
        {
            customProfile = $"Profile{(int)key.Mode - (int)ControlModeId.Custom1 + 1}";
        }
        state.Mode = PrototypeModeCommandCoordinator.Map(AdaptiveTargetMap.PerformanceModeFor(key));
        confirmedAutomaticApply = homeSession.State?.Controls.AdaptiveAutomation?.LastApplyUtc;
        confirmedCpuSignature = CpuSignature(homeSession.State?.Controls.CpuTuning);
        appliedMode.Confirm(state.Mode);
        bool presetConfirmed = PerformanceWorkspaceV2Preview.SubmittedPresetMatchesReadback(key) ||
            savedPerformanceDrafts.TryGetValue(key, out var draft) && PerformanceWorkspaceV2Preview.PresetMatchesReadback(draft);
        PublishConfirmedMode(key.Mode, presetConfirmed ? key : null, animate);
        if (!presetConfirmed) PerformanceWorkspaceV2Preview.SetSubmittedPreset(key);
        RequestModeVisuals(state.Mode, animate);
        try
        {
            userPreferences = preferences.Update(current => current with
            {
                PerformancePresetSlots = new Dictionary<string, int>(current.PerformancePresetSlots ?? []) { [key.Mode.ToString()] = key.Slot }
            });
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Remember performance slot: {error}\n");
            trayQuickConsole?.ShowStatus("预设已应用；槽位记忆保存失败");
        }
    }

    private bool HasConfirmedPerformanceIdentity(HomeStateSnapshot snapshot) => confirmedPerformancePreset is not null &&
        snapshot.Controls.PerformanceMode == confirmedFirmwareMode && confirmedCpuSignature == CpuSignature(snapshot.Controls.CpuTuning);

    private PresetKey? RememberedPerformancePreset(ControlModeId mode)
    {
        var saved = preferences.Load();
        if (saved.PerformancePresetSlots?.TryGetValue(mode.ToString(), out int slot) != true || slot is < 1 or > 3) slot = 2;
        var key = PresetKey.Create(mode, slot);
        return TrayPresetInfo(key).Available ? key : null;
    }

    private void InvalidateSavedPerformancePreset(PresetKey key)
    {
        savedPerformanceDrafts.Remove(key);
        if (confirmedPerformancePreset == key) PublishConfirmedMode(confirmedControlMode, null, animate: false);
        PageWorkspace.SetAutomaticPresetReader(PerformanceWorkspace.ReadAutomaticServicePresetsAsync);
        _ = RefreshTrayPresetCatalogAsync();
    }

    private (string Name, bool Available, string? Reason) TrayPresetInfo(PresetKey key) =>
        trayPresetCatalog.TryGetValue(key, out var info) ? info : ($"预设 {key.Slot}", false, "预设尚未读取");

    private static string? PresetUnavailableReason(PerformanceDraft draft, HomeStateSnapshot? snapshot)
    {
        bool Available(string key) => snapshot?.Capabilities.Items.Any(item =>
            item.Key == key && item.State == CapabilityState.Available) == true;
        var plan = PerformanceCommandFactory.CreatePresetCommands(draft, snapshot?.Controls.CpuTuning,
            Available("cpuTuning"), Available("cpuTuning:smu"), Available("cpuTuning:pbo"), Available("cpuTuning:curveOptimizer"));
        return plan.Error ?? (plan.Skipped.Count > 0 ? $"未提供完整读回：{string.Join("、", plan.Skipped)}" : null);
    }

    private void RefreshTrayPresetAvailability(HomeStateSnapshot snapshot)
    {
        bool changed = false;
        foreach (var (key, draft) in savedPerformanceDrafts)
        {
            if (!trayPresetCatalog.TryGetValue(key, out var old)) continue;
            string? reason = PresetUnavailableReason(draft, snapshot);
            var updated = (old.Name, reason is null, reason);
            if (old == updated) continue;
            trayPresetCatalog[key] = updated;
            changed = true;
        }
        if (changed) trayQuickConsole?.SetConfirmedSelection(confirmedControlMode, confirmedPerformancePreset, animate: false);
    }

    private async Task RefreshTrayPresetCatalogAsync()
    {
        int revision = ++presetCatalogRevision;
        var catalog = new Dictionary<PresetKey, (string Name, bool Available, string? Reason)>();
        var drafts = new Dictionary<PresetKey, PerformanceDraft>();
        var store = new ControlPresetStore();
        var names = preferences.Load().PresetNames ?? [];
        foreach (var mode in Enum.GetValues<ControlModeId>())
        for (int slot = 1; slot <= 3; slot++)
        {
            var key = PresetKey.Create(mode, slot);
            string name = PresetNameCatalog.GetName(names, key);
            try
            {
                var envelope = await store.LoadAsync(ControlPageId.Performance, key, lifetimeCancellation.Token);
                var draft = envelope is null ? null : SavedPerformancePreset.ReadDraft(envelope, key);
                string? reason = draft is null ? "尚未保存" :
                    !PerformanceDraftValidator.Validate(draft).IsValid ? "预设参数无效" :
                    !PerformanceCommandFactory.HasPresetTargets(draft) ? "没有可应用参数" :
                    PresetUnavailableReason(draft, homeSession.State);
                catalog[key] = (name, reason is null, reason);
                if (draft is not null && PerformanceDraftValidator.Validate(draft).IsValid &&
                    PerformanceCommandFactory.HasPresetTargets(draft)) drafts[key] = draft;
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Tray preset {key}: {error.Message}\n");
                catalog[key] = (name, false, "预设读取失败");
            }
        }
        if (revision != presetCatalogRevision || allowClose) return;
        trayPresetCatalog.Clear();
        foreach (var item in catalog) trayPresetCatalog[item.Key] = item.Value;
        savedPerformanceDrafts.Clear();
        foreach (var item in drafts) savedPerformanceDrafts[item.Key] = item.Value;
        if (homeSession.State is { } snapshot) RefreshTrayPresetAvailability(snapshot);
        trayQuickConsole?.SetConfirmedSelection(confirmedControlMode, confirmedPerformancePreset, animate: false);
    }

    private void ShowMainPage(string page)
    {
        if (allowClose) return;
        // Leave popup input before hiding it and transferring focus to the main island.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (allowClose) return;
            trayQuickConsole?.HideImmediately();
            trayContextMenu?.HideImmediately();
            ShowPage(page.ToLowerInvariant() switch { "cpu" => "Performance", "gpu" => "Gpu", "fan" => "Fan", "settings" => "Settings", _ => page });
            RestoreMainWindowIfMinimized();
            if (page == "Home") CenterCurrentWindowOnPrimary();
            AppWindow.Show();
            Activate();
        });
    }

    private void ManagePerformancePresets(ControlModeId mode)
    {
        ShowMainPage("Performance");
        DispatcherQueue.TryEnqueue(() => { if (!allowClose) PerformanceWorkspaceV2Preview.SelectModeForManagement(mode); });
    }

    private async void OnTrayStrategyRequested(AdaptiveStrategyId strategy)
    {
        if (allowClose || customActivationPending || isModeCommandPending || strategyActivationPending || adaptiveActivationPending) return;
        strategyActivationPending = true;
        var popup = trayQuickConsole;
        popup?.SetStrategyBusy(true);
        if (popup is not null) popup.IsModalActionPending = true;
        try
        {
            bool applied = await PageWorkspace.SelectAdaptiveStrategyAsync(strategy);
            if (applied)
            {
                userPreferences = preferences.Load();
                popup?.ApplyAdaptiveStrategy(userPreferences.ActiveAdaptiveStrategy);
            }
            popup?.ShowStatus(applied ? "调度策略已确认" : "策略未应用；保留原方案");
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Tray strategy: {error}\n");
            popup?.ShowStatus("策略切换失败；保留原方案");
        }
        finally
        {
            strategyActivationPending = false;
            if (popup is not null) { popup.IsModalActionPending = false; popup.SetStrategyBusy(false); }
        }
    }
}
