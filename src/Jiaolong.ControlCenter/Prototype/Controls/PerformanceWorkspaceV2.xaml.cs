using System.Text.Json;
using System.Diagnostics;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Controls;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class PerformanceWorkspaceV2 : UserControl
{
    public event Action<PresetKey>? PresetApplied;
    public event Action<PresetKey>? PresetSaved;
    public Func<Task<bool>>? PrepareManualControlAsync { get; set; }
    public event EventHandler<bool>? FollowPresetChanged;
    public bool IsFollowingPreset { get; private set; } = true;
    private Func<PresetKey, Task<bool>>? presetActivator;
    public void SetPresetActivator(Func<PresetKey, Task<bool>> activate) => presetActivator = activate;
    public void SetConfirmedActivePreset(PresetKey? key) => PresetToolbar.SetConfirmedActivePreset(key);
    public void SetSubmittedPreset(PresetKey key) { PresetToolbar.SetCurrentPreset(null); PresetToolbar.SetSubmittedPreset(key); }
    private PresetKey? lastSubmittedPresetKey;
    private PerformanceDraft? lastSubmittedPresetDraft;
    public bool SubmittedPresetMatchesReadback(PresetKey key) => lastSubmittedPresetKey == key &&
        lastSubmittedPresetDraft is { } draft && PresetMatchesReadback(draft);
    public Task ShowPresetStatusAsync(string message) => PresetToolbar.ShowStatusAsync(message);
    public string LastPresetFeedback => PresetToolbar.StatusDetail;
    public bool PresetMatchesReadback(PerformanceDraft draft)
    {
        var plan = PerformanceCommandFactory.CreatePresetCommands(draft, currentState,
            cpuTuningCapabilityAvailable, advancedCapabilityAvailable, pboCapabilityAvailable, curveOptimizerCapabilityAvailable);
        return plan.Error is null && plan.Skipped.Count == 0 && plan.Steps.All(step =>
            step.ReadbackPlan is not null && PerformanceCommandFactory.MatchesReadBack(currentState, step.ReadbackPlan));
    }
    public void SelectModeForManagement(ControlModeId mode)
    {
        if (applyInProgress || liveEditApplying || liveEditPending || presetEditorLoading) return;
        PresetToolbar.EnterPresetManagement();
        PresetToolbar.SelectedKey = PresetKey.Create(mode, selectedPreset.Slot);
    }
    public bool ReducedMotion { set => CpuBoundary.ReducedMotion = value; }
    private static readonly JsonSerializerOptions PresetJson = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ControlPresetStore presetStore = new();
    private readonly UserPreferencesStore followPreferences = new();
    private PresetKey selectedPreset = PresetKey.Create(ControlModeId.Office, 1);
    private HomeControlSession? session;
    private CpuTuningState? currentState;
    private PerformanceDraft? selectedSavedDraft;
    private int? acFrequencyMhz;
    private int? dcFrequencyMhz;
    private Guid? windowsPowerSchemeDraftId;
    private bool editingAc = true;
    private bool synchronizing;
    private bool dirty;
    private bool applyInProgress;
    private readonly Dictionary<PresetKey, PerformanceDraft> editorDrafts = [];
    private readonly DispatcherTimer liveEditTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool liveEditPending;
    private bool liveEditApplying;
    private CpuCurveOptimizerMode? liveCurveMode;
    private bool liveCurveReadbackKnown;
    private long presetLoadRevision;
    private bool presetEditorLoading;
    private bool advancedOpen;
    private bool advancedCapabilityAvailable;
    private bool pboCapabilityAvailable;
    private bool curveOptimizerCapabilityAvailable;
    private bool cpuTuningCapabilityAvailable;
    private bool? acPowerConnected;
    private IReadOnlyList<(Guid Id, string Name)> powerSchemes = [];
    private Guid? activePowerSchemeId;
    private DateTimeOffset lastPowerSchemeRead;
    private bool readingPowerScheme;

    public PerformanceWorkspaceV2()
    {
        InitializeComponent();
        IsFollowingPreset = followPreferences.Load().PerformanceFollowPreset;
        BoostHelpButton.Help = new ParameterHelpContent(
            "允许处理器在功耗、温度与电流限制内自动提高频率；开启后短时响应更快，也可能更热、更吵。",
            "开关状态由硬件回读。",
            "办公、续航优先可关闭；游戏或持续负载可开启，并保留厂商保护限制。",
            "关闭可限制睿频、减少峰值功耗；已有稳定配置可保持当前开关。",
            "仅有开启或关闭；实际最高频率由处理器、固件和当前限制决定，开启不保证达到上限。",
            "实时页面直接生效；预设管理中仅修改草稿。过热或供电受限时仍会降频。");
        PowerPlanHelpButton.Help = new ParameterHelpContent(
            "选择电脑已有的 Windows 电源计划，影响处理器响应、核心停泊和续航；高性能可能增加待机耗电。",
            "实时页面选择后直接应用；预设管理中保存到草稿，保存并应用后生效。",
            "日常选平衡；游戏或重负载可选本机已有的高性能或厂商性能方案。",
            "保持当前 Windows 或厂商标准计划，减少频率、续航与温度变化。",
            "只能选择 Windows 实际列出的方案；没有统一数值上限，也不会改写计划内部策略。",
            "第三方计划的响应与耗电可能不同；当前方案无法回读时保持不可用。");
        PresetToolbar.EnablePresetReset(OnResetPreset);
        PresetToolbar.ConfigureFollowPreset(IsFollowingPreset);
        PresetToolbar.FollowPresetChanged += OnFollowPresetChanged;
        PresetToolbar.EditingModeChanged += OnEditingModeChanged;
        PresetToolbar.PresetUseRequested += OnPresetUseRequested;
        PresetToolbar.SaveAsRequested += async (_, key) => await SavePresetToAsync(key);
        liveEditTimer.Tick += OnLiveEditTimerTick;
        Loaded += OnLoaded;
    }

    public void ConfigureFollowPreset(bool enabled)
    {
        PresetToolbar.ConfigureFollowPreset(enabled);
        ApplyFollowPresetState(enabled);
    }

    public void SetFollowPreset(bool enabled)
    {
        PresetToolbar.SetFollowPreset(enabled);
        OnFollowPresetChanged(this, enabled);
    }

    private async void OnFollowPresetChanged(object? sender, bool enabled)
    {
        if (IsFollowingPreset == enabled) return;
        if (liveEditPending || applyInProgress)
        {
            PresetToolbar.SetFollowPreset(IsFollowingPreset);
            await PresetToolbar.ShowStatusAsync("CPU 编辑正在应用，请稍后切换跟随设置");
            return;
        }
        try
        {
            followPreferences.Update(value => value with { PerformanceFollowPreset = enabled });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            PresetToolbar.SetFollowPreset(IsFollowingPreset);
            await PresetToolbar.ShowStatusAsync("保存跟随设置失败");
            return;
        }

        ApplyFollowPresetState(enabled);
        FollowPresetChanged?.Invoke(this, enabled);
    }

    private void ApplyFollowPresetState(bool enabled) => IsFollowingPreset = enabled;

    private async void OnEditingModeChanged(object? sender, bool editing)
    {
        ++presetLoadRevision;
        if (editing)
        {
            liveEditTimer.Stop();
            long revision = presetLoadRevision + 1;
            await LoadSelectedPresetAsync(applyToEditor: true, animate: true);
            if (!PresetToolbar.IsEditingPreset || revision != presetLoadRevision) return;
            if (editorDrafts.TryGetValue(selectedPreset, out var retained)) ApplyDraft(retained, animate: true);
            SetDirty(editorDrafts.ContainsKey(selectedPreset), updateStatus: false);
        }
        else
        {
            if (dirty && TryCreateDraft() is { } retained) editorDrafts[selectedPreset] = retained;
            dirty = false;
            presetEditorLoading = false;
            ApplyHardwareState(currentState, useRecommendations: false, preservePresetState: true);
            _ = PresetToolbar.SetDirtyStatusAsync(false);
        }
        SetInteractionAvailability(currentState is not null);
    }

    private async void OnPresetUseRequested(object? sender, PresetKey key)
    {
        if (applyInProgress || liveEditApplying || liveEditPending || PresetToolbar.IsEditingPreset) return;
        bool applied = await (presetActivator?.Invoke(key) ?? ApplyStoredPresetAsync(key, XamlRoot));
        if (applied)
        {
            PresetApplied?.Invoke(key);
        }
        currentState = session?.State?.Controls.CpuTuning ?? currentState;
        if (!PresetToolbar.IsEditingPreset) ApplyHardwareState(currentState, useRecommendations: false, preservePresetState: true);
    }

    private void OnLiveEditTimerTick(object? sender, object e)
    {
        liveEditTimer.Stop();
        if (!liveEditPending || PresetToolbar.IsEditingPreset) return;
        if ((GetAsyncKeyState(1) & 0x8000) != 0 || TemperatureRow.IsInputPending || SustainedPowerRow.IsInputPending || BurstPowerRow.IsInputPending || FrequencyRow.IsInputPending || CurveOptimizerRow.IsInputPending)
        { liveEditTimer.Start(); return; }
        if (applyInProgress || liveEditApplying)
            return;
        liveEditPending = false;
        if (currentState?.TemperatureLimitC is null || currentState.SplWatts is null || currentState.SpptWatts is null ||
            TryCreateDraft() is not { } draft)
        {
            _ = PresetToolbar.ShowStatusAsync("当前 CPU 参数读回不完整，未应用编辑");
            return;
        }
        _ = ApplyLiveEditAsync(draft);
    }

    private async Task ApplyLiveEditAsync(PerformanceDraft draft)
    {
        liveEditApplying = true;
        SetInteractionAvailability(false);
        try
        {
            if (PrepareManualControlAsync is { } prepare && !await prepare()) return;
            await ApplySavedDraftAsync(draft, XamlRoot, requireComplete: false, presetKey: null);
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] CPU live edit: {ex}");
            _ = PresetToolbar.ShowStatusAsync("CPU 编辑结果未知；已重新读取硬件状态");
        }
        finally
        {
            liveEditApplying = false;
            if (!PresetToolbar.IsEditingPreset && !liveEditPending)
            {
                currentState = session?.State?.Controls.CpuTuning ?? currentState;
                ApplyHardwareState(currentState, useRecommendations: false, preservePresetState: true);
            }
            if (!PresetToolbar.IsEditingPreset && liveEditPending) liveEditTimer.Start();
            SetInteractionAvailability(currentState is not null);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private void OnUserDraftEdited()
    {
        if (PresetToolbar.IsEditingPreset)
        {
            SetDirty(true);
            if (TryCreateDraft() is { } retained) editorDrafts[selectedPreset] = retained;
        }
        else
        {
            PresetToolbar.SetCurrentSettingsModified();
            liveEditPending = true;
            liveEditTimer.Stop();
            liveEditTimer.Start();
            SetInteractionAvailability(currentState is not null);
        }
    }

    public void AttachSession(HomeControlSession controlSession)
    {
        session = controlSession;
        if (controlSession.State is { } snapshot)
            ApplyState(snapshot);
    }

    public void ShowAdvancedPreview() => ShowAdvanced(true);

    public void ApplyAcceptancePreview()
    {
        var previewState = new CpuTuningState(85, 95, 45, 4200, true, 8, Guid.Empty, -15, "verified")
        {
            AcMaxFrequencyMhz = 4200,
            DcMaxFrequencyMhz = 3600
        };
        currentState = previewState;
        advancedCapabilityAvailable = true;
        curveOptimizerCapabilityAvailable = true;
        selectedPreset = PresetKey.Create(ControlModeId.Turbo, 2);
        PresetToolbar.SelectedKey = selectedPreset;
        PresetToolbar.SetActivePreset(PresetKey.Create(ControlModeId.Turbo, 1));
        ApplyHardwareState(previewState);
        RefreshBoundaryFromEditor();
        ApplyTelemetry(new HardwareSnapshot(DateTimeOffset.UtcNow, "connected", 68, null, 45, null)
        {
            CpuFrequencyMhz = 4200,
            CpuVoltageVolts = 1.125,
            AcPowerConnected = true
        });
        SetConnectionState(capabilityAvailable: true, hasState: true);
        ControlSourceText.Text = "蛟龙控制中心";
        SetInteractionAvailability(true);
        SetDirty(true);
    }

    public void ShowPresetPreview()
    {
        if (applyInProgress || liveEditApplying || liveEditPending || presetEditorLoading) return;
        ShowAdvanced(false);
        PresetToolbar.EnterPresetManagement();
        PresetToolbar.ShowPicker();
    }

    public void ApplyTelemetry(HardwareSnapshot snapshot)
    {
        acPowerConnected = snapshot.AcPowerConnected;
        CpuBoundary.ApplyTelemetry(snapshot);
        PowerStateText.Text = snapshot.AcPowerConnected switch
        {
            true => "已接通电源（AC）",
            false => "电池供电（DC）",
            null => "不可用"
        };
        SetInteractionAvailability(currentState is not null);
    }

    public void ApplyState(HomeStateSnapshot snapshot)
    {
        acPowerConnected = snapshot.Telemetry?.AcPowerConnected;
        bool available = IsCapabilityAvailable(snapshot, "cpuTuning");
        cpuTuningCapabilityAvailable = available;
        advancedCapabilityAvailable =
            IsCapabilityAvailable(snapshot, "cpuTuning:smu");
        pboCapabilityAvailable = IsCapabilityAvailable(snapshot, "cpuTuning:pbo");
        curveOptimizerCapabilityAvailable = IsCapabilityAvailable(snapshot, "cpuTuning:curveOptimizer");
        currentState = available ? snapshot.Controls.CpuTuning : null;
        AdvancedWorkspace.SetSmuHardware(currentState?.AdvancedLimits);
        AdvancedWorkspace.SetPboHardware(currentState?.PboScalar);
        AdvancedWorkspace.SetCurveHardware(currentState?.PerCoreCurveOptimizer);
        UpdateCoreTopology(currentState?.EnabledCoreCount);
        if (!dirty && selectedSavedDraft is null)
            windowsPowerSchemeDraftId = currentState?.WindowsPowerSchemeId;
        UpdatePowerSchemeSelection();
        AdvancedWorkspace.SetCoreParkingHardware(
            currentState?.AcMinActiveCoresPercent, currentState?.DcMinActiveCoresPercent);
        SetConnectionState(available, currentState is not null);
        ControlSourceText.Text = PresetToolbar.IsEditingPreset ? "正在编辑预设 · 尚未应用" : currentState is not null ? "硬件回读" : available ? "等待读回" : "未连接";

        if (!PresetToolbar.IsEditingPreset && !applyInProgress && !liveEditApplying && !liveEditPending)
            ApplyHardwareState(available ? currentState : null, useRecommendations: false, preservePresetState: true);

        RefreshBoundaryFromEditor();
        SetInteractionAvailability(available && currentState is not null);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await SetFrequencyModeAsync(editAc: true, animate: false);
        if (session?.State is { } snapshot)
            ApplyState(snapshot);

        var envelope = await LoadSelectedPresetAsync(applyToEditor: PresetToolbar.IsEditingPreset);
        if (PresetToolbar.IsEditingPreset && envelope is not null && selectedSavedDraft is not null)
        {
            SetDirty(false);
            PresetToolbar.SetEditingState(selectedPreset, dirty: false, saved: true);
            PresetSaved?.Invoke(selectedPreset);
        }
        SetInteractionAvailability(currentState is not null);
        await RefreshPresetSummariesAsync();
        _ = RefreshPowerSchemesAsync();
    }

    private void ApplyHardwareState(CpuTuningState? state, bool useRecommendations = true, bool preservePresetState = false)
    {
        synchronizing = true;
        if (!PresetToolbar.IsEditingPreset) windowsPowerSchemeDraftId = state?.WindowsPowerSchemeId;
        TemperatureRow.SetValue((double?)state?.TemperatureLimitC ??
            (useRecommendations && cpuTuningCapabilityAvailable ? TemperatureRow.RecommendedValue : null));
        SustainedPowerRow.SetValue((double?)state?.SplWatts ??
            (useRecommendations && cpuTuningCapabilityAvailable ? SustainedPowerRow.RecommendedValue : null));
        BurstPowerRow.SetValue((double?)state?.SpptWatts ??
            (useRecommendations && cpuTuningCapabilityAvailable ? BurstPowerRow.RecommendedValue : null));
        acFrequencyMhz = state?.AcFrequency() is int ac && ac is >= 1500 and <= 5400 ? ac : null;
        dcFrequencyMhz = state?.DcFrequency() is int dc && dc is >= 1500 and <= 5400 ? dc : null;
        FrequencyRow.SetValue(editingAc ? acFrequencyMhz : dcFrequencyMhz);
        var curveReadback = AdvancedCpuTuningDraft.FromCurveReadback(state, liveCurveMode);
        liveCurveReadbackKnown = curveReadback.PerCoreCurveOptimizer is not null;
        AdvancedWorkspace.ApplyDraft(curveReadback);
        AdvancedWorkspace.SetPboHardware(state?.PboScalar);
        BoostToggle.IsOn = state?.BoostEnabled == true;
        UpdateCoreTopology(state?.EnabledCoreCount);
        UpdatePowerSchemeSelection();
        ControlSourceText.Text = state is null ? "未连接" : "硬件回读";
        TemperatureRow.SetDescription("CPU 最高温度限制");
        SustainedPowerRow.SetDescription(state?.SplWatts is int actualSpl
            ? $"当前硬件 {actualSpl} W" : "长时间稳定运行功耗");
        BurstPowerRow.SetDescription(state?.SpptWatts is int actualSppt
            ? $"当前硬件 {actualSppt} W" : "短时加速最大功耗");
        TemperatureRow.SetHelpCurrentValue(state?.TemperatureLimitC is int temperature
            ? $"硬件温限 {temperature} °C" : "硬件未提供当前温限；编辑值是预设配置，参考值不代表当前设置。");
        SustainedPowerRow.SetHelpCurrentValue(state?.SplWatts is int spl
            ? $"硬件 SPL {spl} W" : "硬件未提供当前 SPL；编辑值是预设配置，55 W 参考值不代表当前设置。");
        BurstPowerRow.SetHelpCurrentValue(state?.SpptWatts is int sppt
            ? $"硬件 SPPT {sppt} W" : "硬件未提供当前 SPPT；编辑值是预设配置，55 W 参考值不代表当前设置。");
        BoundarySourceText.Text = state is null
            ? "等待读回"
            : state.TemperatureLimitC is null || state.SplWatts is null || state.SpptWatts is null
                ? "部分为规格参考"
                : "限值已硬件读回";
        if (state is not null &&
            (state.TemperatureLimitC is null || state.SpptWatts is null || state.SplWatts is null))
            ControlSourceText.Text = "硬件回读 · 部分上限缺失";
        synchronizing = false;
        RefreshBoundaryFromEditor();
        if (!preservePresetState) SetDirty(false);
    }

    private async Task RefreshPowerSchemesAsync()
    {
        if (readingPowerScheme || DateTimeOffset.UtcNow - lastPowerSchemeRead < TimeSpan.FromSeconds(15))
            return;

        readingPowerScheme = true;
        try
        {
            powerSchemes = await Task.Run(WindowsPowerSchemeReader.ReadSchemes);
            activePowerSchemeId = await Task.Run(WindowsPowerSchemeReader.ReadActiveId);
            lastPowerSchemeRead = DateTimeOffset.UtcNow;
            Guid? observedId = currentState?.WindowsPowerSchemeId ?? activePowerSchemeId;
            if (!dirty && selectedSavedDraft is null && observedId is Guid id && id != Guid.Empty)
                windowsPowerSchemeDraftId = id;
            UpdatePowerSchemeSelection();
        }
        catch
        {
            powerSchemes = [];
            activePowerSchemeId = null;
            lastPowerSchemeRead = DateTimeOffset.UtcNow;
            UpdatePowerSchemeSelection();
        }
        finally
        {
            readingPowerScheme = false;
        }
    }

    private void UpdatePowerSchemeSelection()
    {
        bool wasSynchronizing = synchronizing;
        synchronizing = true;
        try
        {
            var existingPlans = PowerPlanSelector.Items.OfType<ComboBoxItem>()
                .Where(item => item.IsEnabled).Select(item => ((Guid)item.Tag, (string)item.Content));
            if (!existingPlans.SequenceEqual(powerSchemes))
            {
                PowerPlanSelector.Items.Clear();
                foreach (var scheme in powerSchemes)
                    PowerPlanSelector.Items.Add(new ComboBoxItem { Content = scheme.Name, Tag = scheme.Id });
            }

            Guid? selectedId = windowsPowerSchemeDraftId ?? currentState?.WindowsPowerSchemeId ?? activePowerSchemeId;
            var selected = selectedId is Guid id
                ? PowerPlanSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is Guid candidate && candidate == id)
                : null;
            if (selected is null && selectedId is Guid unavailableId && unavailableId != Guid.Empty)
            {
                selected = new ComboBoxItem { Content = "已保存计划（当前不可用）", Tag = unavailableId, IsEnabled = false };
                PowerPlanSelector.Items.Insert(0, selected);
            }
            if (!ReferenceEquals(PowerPlanSelector.SelectedItem, selected))
                PowerPlanSelector.SelectedItem = selected;
            PowerPlanSelector.PlaceholderText = powerSchemes.Count == 0 ? "计划不可用" : "选择计划";
            PowerPlanSelector.IsEnabled = powerSchemes.Count > 0 && !applyInProgress;
        }
        finally
        {
            synchronizing = wasSynchronizing;
        }
    }

    private void OnPowerPlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || PowerPlanSelector.SelectedItem is not ComboBoxItem { IsEnabled: true, Tag: Guid selectedScheme } ||
            windowsPowerSchemeDraftId == selectedScheme)
            return;

        windowsPowerSchemeDraftId = selectedScheme;
        OnUserDraftEdited();
    }

    private void SetInteractionAvailability(bool available)
    {
        PresetToolbar.IsEnabled = !applyInProgress && !liveEditApplying && !presetEditorLoading;
        CurveModeSelector.IsEnabled = curveOptimizerCapabilityAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading;
        CurveOptimizerRow.SetEditorEnabled(curveOptimizerCapabilityAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading &&
            AdvancedWorkspace.CurveMode == CpuCurveOptimizerMode.AllCore);
        bool temperatureWritable = cpuTuningCapabilityAvailable && acPowerConnected is true &&
            !applyInProgress && !liveEditApplying && !presetEditorLoading && session?.Status == HomeSessionStatus.Connected;
        TemperatureRow.SetEditorEnabled(temperatureWritable);
        bool powerWritable = cpuTuningCapabilityAvailable && acPowerConnected is true &&
            !applyInProgress && !liveEditApplying && !presetEditorLoading && session?.Status == HomeSessionStatus.Connected;
        SustainedPowerRow.SetEditorEnabled(powerWritable);
        BurstPowerRow.SetEditorEnabled(powerWritable);
        bool frequencyAvailable = available && acFrequencyMhz is not null && dcFrequencyMhz is not null;
        FrequencyRow.SetEditorEnabled(frequencyAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading);
        FrequencyAcButton.IsEnabled = frequencyAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading;
        FrequencyDcButton.IsEnabled = frequencyAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading;
        BoostToggle.IsEnabled = available && !applyInProgress && !liveEditApplying && !presetEditorLoading && currentState?.BoostEnabled is not null;
        PowerPlanSelector.IsEnabled = powerSchemes.Count > 0 && !applyInProgress && !liveEditApplying && !presetEditorLoading;
        AdvancedWorkspace.SetAvailability(available && currentState is not null && !applyInProgress && !liveEditApplying && !presetEditorLoading,
            advancedCapabilityAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading, pboCapabilityAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading);
        PresetToolbar.SetActionAvailability(
            saveEnabled: !applyInProgress && !liveEditApplying && !presetEditorLoading && !liveEditPending && (!PresetToolbar.IsEditingPreset || TryCreateDraft() is not null),
            useEnabled: PresetToolbar.IsEditingPreset && available && TryCreateDraft() is not null && !applyInProgress && !liveEditApplying && !presetEditorLoading);
    }

    private void SetConnectionState(bool capabilityAvailable, bool hasState)
    {
        ConnectionText.Text = hasState && capabilityAvailable ? "连接正常" : capabilityAvailable ? "等待回读" : "不可用";
        ConnectionDot.Fill = Brush(hasState && capabilityAvailable ? "ModeAccentBrush" : "PrototypeSecondaryTextBrush");
    }

    private void OnDraftValueChanged(object? sender, double value)
    {
        if (!synchronizing)
        {
            AdvancedWorkspace.SynchronizeBasicAliases(
                ReferenceEquals(sender, TemperatureRow) ? value : null,
                ReferenceEquals(sender, BurstPowerRow) ? value : null);
            RefreshBoundaryFromEditor();
            OnUserDraftEdited();
        }
    }

    private void OnFrequencyValueChanged(object? sender, double value)
    {
        if (synchronizing)
            return;

        if (editingAc) acFrequencyMhz = (int)Math.Round(value);
        else dcFrequencyMhz = (int)Math.Round(value);
        RefreshBoundaryFromEditor();
        OnUserDraftEdited();
    }

    private async void OnFrequencyAcClick(object sender, RoutedEventArgs e) => await SetFrequencyModeAsync(editAc: true);
    private async void OnFrequencyDcClick(object sender, RoutedEventArgs e) => await SetFrequencyModeAsync(editAc: false);

    private async Task SetFrequencyModeAsync(bool editAc, bool animate = true)
    {
        if (editingAc == editAc && animate)
            return;

        editingAc = editAc;
        synchronizing = true;
        if (animate)
            await FrequencyRow.SetValueAnimatedAsync(editAc ? acFrequencyMhz : dcFrequencyMhz);
        else
            FrequencyRow.SetValue(editAc ? acFrequencyMhz : dcFrequencyMhz);
        FrequencyAcButton.Background = Brush(editAc ? "ModeSelectionBrush" : "ControlButtonNeutralBrush");
        FrequencyDcButton.Background = Brush(editAc ? "ControlButtonNeutralBrush" : "ModeSelectionBrush");
        FrequencyAcButton.BorderBrush = Brush(editAc ? "ModeAccentBrush" : "PrototypeStrokeBrush");
        FrequencyDcButton.BorderBrush = Brush(editAc ? "PrototypeStrokeBrush" : "ModeAccentBrush");
        FrequencyAcButton.Foreground = editAc ? Brush("PrototypeTextBrush") : Brush("PrototypeSecondaryTextBrush");
        FrequencyDcButton.Foreground = editAc ? Brush("PrototypeSecondaryTextBrush") : Brush("PrototypeTextBrush");
        synchronizing = false;
        RefreshBoundaryFromEditor();
    }

    private void OnBoostToggled(object sender, RoutedEventArgs e)
    {
        if (!synchronizing)
            OnUserDraftEdited();
    }

    private async void OnResetPreset(object? sender, PresetKey key)
    {
        if (applyInProgress) return;
        applyInProgress = true;
        if (selectedPreset == key) ++presetLoadRevision;
        SetInteractionAvailability(false);
        try
        {
            var envelope = await presetStore.ResetPerformanceAsync(key, CancellationToken.None);
            PresetSaved?.Invoke(key);
            editorDrafts.Remove(key);
            if (selectedPreset == key && PresetToolbar.IsEditingPreset)
            {
                selectedSavedDraft = SavedPerformancePreset.ReadDraft(envelope, key);
                if (selectedSavedDraft is { } draft) ApplyDraft(draft, animate: true);
                SetDirty(false, updateStatus: false);
                PresetToolbar.SetEditingState(key, dirty: false, saved: true);
            }
            await RefreshPresetSummariesAsync();
            await PresetToolbar.ShowTransientStatusAsync("此预设已恢复默认推荐值");
        }
        catch { await PresetToolbar.ShowStatusAsync("恢复默认未完成，请重新读取此预设"); }
        finally
        {
            applyInProgress = false;
            SetInteractionAvailability(currentState is not null);
        }
    }

    private async Task RefreshPresetSummariesAsync()
    {
        var mode = selectedPreset.Mode;
        for (int slot = 1; slot <= 3; slot++)
        {
            var key = PresetKey.Create(mode, slot);
            var envelope = await presetStore.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
            if (selectedPreset.Mode != mode) return;
            var draft = envelope is null ? null : SavedPerformancePreset.ReadDraft(envelope, key);
            PresetToolbar.SetSlotSummary(slot, draft is null ? "未配置" :
                $"{draft.TemperatureLimitC}°C · {draft.SplWatts}W · {draft.AcMaxFrequencyMhz / 1000d:0.0}GHz");
        }
    }

    private async void OnSavePresetClick(object? sender, EventArgs e)
    {
        var key = PresetToolbar.IsEditingPreset ? selectedPreset : PresetToolbar.CurrentSourceKey;
        if (key is { } target) await SavePresetToAsync(target);
    }

    private async Task<bool> SavePresetToAsync(PresetKey key)
    {
        if (applyInProgress || liveEditApplying || liveEditPending || presetEditorLoading) return false;
        PerformanceDraft? draft = TryCreateDraft();
        if (draft is null) { await PresetToolbar.ShowStatusAsync("当前值不可用，未保存"); return false; }
        if (!PresetToolbar.IsEditingPreset && (draft.TemperatureLimitC is < 45 or > 100 || draft.SplWatts is < 45 or > 75 || draft.SpptWatts is < 45 or > 75))
        {
            await PresetToolbar.ShowStatusAsync("当前厂商限值超出预设编辑范围，未保存；可在管理预设中编辑允许范围内的配置");
            return false;
        }
        applyInProgress = true;
        SetInteractionAvailability(false);
        try
        {
            await presetStore.SaveAsync(new PagePresetEnvelope(1, ControlPageId.Performance, key,
                PresetToolbar.DisplayNameFor(key), JsonSerializer.SerializeToElement(draft, PresetJson), DateTimeOffset.UtcNow), CancellationToken.None);
            editorDrafts.Remove(key);
            if (key == selectedPreset)
            {
                selectedSavedDraft = draft;
                SetDirty(false, updateStatus: false);
                PresetToolbar.SetEditingState(key, dirty: false, saved: true);
            }
            PresetSaved?.Invoke(key);
            await RefreshPresetSummariesAsync();
            _ = PresetToolbar.ShowSavedStatusAsync();
            return true;
        }
        catch { await PresetToolbar.ShowStatusAsync("保存失败，原预设未确认更改"); return false; }
        finally { applyInProgress = false; SetInteractionAvailability(currentState is not null); }
    }

    private async void OnUsePresetClick(object? sender, EventArgs e)
    {
        if (applyInProgress || !PresetToolbar.IsEditingPreset) return;
        var key = selectedPreset;
        if (!await SavePresetToAsync(key)) return;
        if (await (presetActivator?.Invoke(key) ?? ApplyStoredPresetAsync(key, XamlRoot)))
        {
            PresetApplied?.Invoke(key);
            if (selectedPreset == key) PresetToolbar.ExitPresetManagement();
        }
    }

    public Task<bool> ApplyCustomPresetAsync(PresetKey key, XamlRoot confirmationRoot, Func<Task<bool>>? beforeApply = null) =>
        key.Mode is ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3
            ? ApplyStoredPresetAsync(key, confirmationRoot, beforeApply) : Task.FromResult(false);

    public async Task<bool> ApplyStoredPresetAsync(
        PresetKey key,
        XamlRoot confirmationRoot,
        Func<Task<bool>>? beforeApply = null,
        Func<bool>? isCurrentRequest = null,
        CancellationToken queueCancellationToken = default)
    {
        PresetKey.Create(key.Mode, key.Slot);
        if (applyInProgress)
            return false;

        PagePresetEnvelope? envelope;
        try
        {
            envelope = await presetStore.LoadAsync(ControlPageId.Performance, key, CancellationToken.None);
        }
        catch
        {
            await PresetToolbar.ShowStatusAsync("性能预设读取失败");
            return false;
        }

        PerformanceDraft? draft = null;
        try
        {
            draft = envelope is null ? null : SavedPerformancePreset.ReadDraft(envelope, key);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
        }
        if (draft is null)
        {
            string profile = key.Mode switch
            {
                ControlModeId.Office => "办公",
                ControlModeId.Gaming => "游戏",
                ControlModeId.Turbo => "狂飙",
                ControlModeId.Custom1 => "自定义 1",
                ControlModeId.Custom2 => "自定义 2",
                _ => "自定义 3"
            };
            await PresetToolbar.ShowStatusAsync($"请先保存{profile}预设 {key.Slot}");
            return false;
        }

        if (isCurrentRequest?.Invoke() == false) return false;
        bool applied = await ApplySavedDraftAsync(draft, confirmationRoot, beforeApply, requireComplete: true, presetKey: key,
            isCurrentRequest: isCurrentRequest, queueCancellationToken: queueCancellationToken);
        if (applied)
        {
            lastSubmittedPresetKey = key;
            lastSubmittedPresetDraft = draft;
            currentState = session?.State?.Controls.CpuTuning ?? currentState;
            if (PresetMatchesReadback(draft)) PresetToolbar.SetCurrentPreset(key);
            else SetSubmittedPreset(key);
        }
        return applied;
    }

    private async Task<bool> ApplySavedDraftAsync(
        PerformanceDraft draft,
        XamlRoot confirmationRoot,
        Func<Task<bool>>? beforeApply = null,
        bool requireComplete = false,
        PresetKey? presetKey = null,
        Func<bool>? isCurrentRequest = null,
        CancellationToken queueCancellationToken = default)
    {
        if (applyInProgress || session is null || currentState is null ||
            !PerformanceCommandFactory.HasPresetTargets(draft))
        {
            await PresetToolbar.ShowStatusAsync("硬件不可用");
            return false;
        }

        ValidationSummary validation = PerformanceDraftValidator.Validate(presetKey is null
            ? NormalizeUnchangedLiveValues(draft, currentState!)
            : draft);
        if (!validation.IsValid)
        {
            await PresetToolbar.ShowStatusAsync(validation.Errors[0]);
            return false;
        }

        bool forcePreset = presetKey is not null;
        PerformanceMode? nativeMode = presetKey is null ? session.State?.Controls.PerformanceMode : presetKey.Value.Mode switch
        {
            ControlModeId.Office => PerformanceMode.Quiet,
            ControlModeId.Gaming => PerformanceMode.Balanced,
            ControlModeId.Turbo => PerformanceMode.Turbo,
            _ => session.State?.Controls.PerformanceMode
        };
        PerformancePresetCommandPlan commandPlan = presetKey is null
            ? CreateLiveEditCommandPlan(draft, currentState)
            : PerformanceCommandFactory.CreatePresetCommands(
                draft, currentState, cpuTuningCapabilityAvailable, advancedCapabilityAvailable,
                pboCapabilityAvailable, curveOptimizerCapabilityAvailable, forceApply: forcePreset);
        if (commandPlan.Error is not null)
        {
            await PresetToolbar.ShowStatusAsync(commandPlan.Error);
            return false;
        }
        if (requireComplete && commandPlan.Skipped.Count > 0)
        {
            await PresetToolbar.ShowStatusAsync($"硬件未提供完整预设回读；未提交：{string.Join("、", commandPlan.Skipped)}");
            return false;
        }
        if (commandPlan.Steps.Any(step => step.Label is "温度墙" or "持续功耗 SPL" or "短时功耗 SPPT") &&
            session.State?.Telemetry?.AcPowerConnected is not true)
        {
            await PresetToolbar.ShowStatusAsync("性能预设包含 OEM 功耗/温度写入；请接通电源并等待状态确认");
            return false;
        }
        if (commandPlan.Steps.Count == 0 && beforeApply is null)
        {
            await PresetToolbar.ShowStatusAsync(commandPlan.Skipped.Count == 0
                ? "预设值已与当前可读值一致；没有提交命令"
                : $"没有可发送项；未提交：{string.Join("、", commandPlan.Skipped)}");
            return ConfirmAppliedCurveMode(draft, commandPlan.Skipped.Count);
        }

        string acknowledgements = string.Join("、", commandPlan.Steps
            .Where(step => step.ReadbackPlan is null).Select(step => step.Label));
        applyInProgress = true;
        SetInteractionAvailability(false);
        int applied = 0;
        string? inFlight = null;
        try
        {
            if (beforeApply is not null && !await beforeApply())
            {
                await PresetToolbar.ShowStatusAsync("自定义模式未能应用；性能预设未提交");
                return false;
            }

            if (beforeApply is not null)
            {
                CpuTuningState? postModeState = session.State?.Controls.CpuTuning;
                if (postModeState is null)
                {
                    await PresetToolbar.ShowStatusAsync("模式切换后未读回 CPU 状态；未提交性能预设");
                    return false;
                }
                commandPlan = PerformanceCommandFactory.CreatePresetCommands(
                    draft, postModeState, cpuTuningCapabilityAvailable, advancedCapabilityAvailable,
                    pboCapabilityAvailable, curveOptimizerCapabilityAvailable, forceApply: forcePreset);
                if (commandPlan.Error is not null || requireComplete && commandPlan.Skipped.Count > 0)
                {
                    await PresetToolbar.ShowStatusAsync(commandPlan.Error ??
                        $"模式切换后硬件未提供完整预设回读；未提交：{string.Join("、", commandPlan.Skipped)}");
                    return false;
                }
                if (commandPlan.Steps.Any(step => step.Label is "温度墙" or "持续功耗 SPL" or "短时功耗 SPPT") &&
                    session.State?.Telemetry?.AcPowerConnected is not true)
                {
                    await PresetToolbar.ShowStatusAsync("模式切换后需要接通电源；未提交性能预设");
                    return false;
                }
                acknowledgements = string.Join("、", commandPlan.Steps
                    .Where(step => step.ReadbackPlan is null).Select(step => step.Label));
            }

            if (commandPlan.Steps.Count == 0)
            {
                await PresetToolbar.ShowStatusAsync("性能参数已与当前可读值一致");
                return ConfirmAppliedCurveMode(draft, commandPlan.Skipped.Count);
            }

            // Status animation must not delay the hardware transaction or its confirmed mode.
            if (isCurrentRequest?.Invoke() == false) return false;
            _ = PresetToolbar.ShowStatusAsync("正在应用完整预设");
            inFlight = "完整性能预设";
            var batch = new SetCpuTuningBatchCommand(Guid.NewGuid(), commandPlan.Steps
                .Select(step => ((SetCpuTuningCommand)step.Command).Plan).ToArray(), true)
            { NativeMode = nativeMode };
            CommandResult result = await session.ExecuteAsync(batch, CancellationToken.None, queueCancellationToken);
            if (result.State != CommandState.Applied || result.Error is not null)
            {
                AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Performance preset batch: {JsonSerializer.Serialize(result)}\n");
                string recovery = result.State == CommandState.RolledBack && result.Error?.Code != Jiaolong.Contracts.Errors.ErrorCode.RollbackFailed
                    ? "应用失败，已恢复本次修改" : result.State == CommandState.Rejected
                        ? "预设未写入" : "恢复未确认，请重新读取硬件状态";
                string reason = result.Error?.Details.GetValueOrDefault("reason") == "cpuThermalHeadroomRequired"
                    ? "CPU 温度较高，暂不能提高温度墙或功耗；降低参数仍可应用"
                    : result.Error?.Code.ToString() ?? string.Empty;
                await PresetToolbar.ShowStatusAsync(recovery + (reason.Length == 0 ? "" : $"；{reason}"));
                return false;
            }
            if (nativeMode is not null && session.State?.Controls.PerformanceMode != nativeMode ||
                commandPlan.Steps.Any(step => step.ReadbackPlan is not null &&
                !PerformanceCommandFactory.MatchesReadBack(session.State?.Controls.CpuTuning, step.ReadbackPlan)))
            {
                await PresetToolbar.ShowStatusAsync("服务已完成，但页面读回不一致；请重新读取，预设未标记为应用");
                return false;
            }
            applied = commandPlan.Steps.Count;
            inFlight = null;

            string feedback = $"已完成 {applied} 项" +
                (commandPlan.Skipped.Count == 0 ? "" : $"；未读回而跳过：{string.Join("、", commandPlan.Skipped)}") +
                (acknowledgements.Length == 0 ? "。可读回字段已核对。" : $"；{acknowledgements}仅服务确认接受，未读回实际设置。") +
                (commandPlan.Skipped.Count == 0 ? "" : " 预设仍标记为未完整应用。");
            _ = PresetToolbar.ShowStatusAsync(feedback);
            return ConfirmAppliedCurveMode(draft, commandPlan.Skipped.Count);
        }
        catch (OperationCanceledException) when (queueCancellationToken.IsCancellationRequested)
        {
            _ = PresetToolbar.SetDirtyStatusAsync(false);
            return false; // Superseded before submission; an in-flight transaction is never cancelled here.
        }
        catch
        {
            await PresetToolbar.ShowStatusAsync(
                $"{inFlight ?? "完整性能预设"}请求中断、结果未知；请重新读取硬件状态。");
            return false;
        }
        finally
        {
            applyInProgress = false;
            SetInteractionAvailability(currentState is not null);
            if (!PresetToolbar.IsEditingPreset && liveEditPending) liveEditTimer.Start();
        }
    }

    private bool ConfirmAppliedCurveMode(PerformanceDraft draft, int skipped)
    {
        if (skipped != 0) return false;
        liveCurveMode = draft.AdvancedCpuTuning?.ResolveCurveOptimizerMode(draft.NegativeCurveOptimizer)
            ?? (draft.NegativeCurveOptimizer.HasValue ? CpuCurveOptimizerMode.AllCore : CpuCurveOptimizerMode.Bios);
        return true;
    }

    private PerformancePresetCommandPlan CreateLiveEditCommandPlan(PerformanceDraft draft, CpuTuningState state)
    {
        bool unchangedTemperature = state.TemperatureLimitC == draft.TemperatureLimitC;
        bool unchangedSpl = state.SplWatts == draft.SplWatts;
        bool unchangedSppt = state.SpptWatts == draft.SpptWatts;
        var validationDraft = NormalizeUnchangedLiveValues(draft, state);
        var plan = PerformanceCommandFactory.CreatePresetCommands(
            validationDraft, state, cpuTuningCapabilityAvailable, advancedCapabilityAvailable,
            pboCapabilityAvailable, curveOptimizerCapabilityAvailable);
        if (plan.Error is not null) return plan;
        return plan with
        {
            Steps = plan.Steps.Where(step => step.Label switch
            {
                "温度墙" => !unchangedTemperature,
                "持续功耗 SPL" => !unchangedSpl,
                "短时功耗 SPPT" => !unchangedSppt,
                _ => true
            }).ToArray()
        };
    }

    private static PerformanceDraft NormalizeUnchangedLiveValues(PerformanceDraft draft, CpuTuningState state) => draft with
    {
        TemperatureLimitC = state.TemperatureLimitC == draft.TemperatureLimitC && (draft.TemperatureLimitC is < 45 or > 100)
            ? 70 : draft.TemperatureLimitC,
        SplWatts = state.SplWatts == draft.SplWatts && (draft.SplWatts is < 45 or > 75)
            ? 55 : draft.SplWatts,
        SpptWatts = state.SpptWatts == draft.SpptWatts && (draft.SpptWatts is < 45 or > 75)
            ? 55 : draft.SpptWatts
    };

    private async Task<PagePresetEnvelope?> LoadSelectedPresetAsync(bool applyToEditor, bool animate = false)
    {
        var key = selectedPreset;
        var revision = ++presetLoadRevision;
        presetEditorLoading = applyToEditor;
        if (applyToEditor) SetInteractionAvailability(false);
        selectedSavedDraft = null;
        try
        {
            PagePresetEnvelope? envelope = await presetStore.LoadAsync(
                ControlPageId.Performance,
                key,
                CancellationToken.None);
            if (envelope is null || revision != presetLoadRevision || selectedPreset != key || applyToEditor && !PresetToolbar.IsEditingPreset)
                return null;

            if (SavedPerformancePreset.ReadDraft(envelope, key) is { } draft)
            {
                selectedSavedDraft = draft;
                if (applyToEditor) ApplyDraft(draft, animate);
            }
            return envelope;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (revision == presetLoadRevision)
            {
                presetEditorLoading = false;
                SetInteractionAvailability(currentState is not null);
            }
        }
    }

    private async void OnPresetKeyChanged(object? sender, PresetKey key)
    {
        if (PresetToolbar.IsEditingPreset && dirty && TryCreateDraft() is { } retained) editorDrafts[selectedPreset] = retained;
        selectedPreset = key;
        selectedSavedDraft = null;
        long revision = presetLoadRevision + 1;
        PagePresetEnvelope? envelope = await LoadSelectedPresetAsync(applyToEditor: PresetToolbar.IsEditingPreset, animate: true);
        if (selectedPreset != key || revision != presetLoadRevision) return;
        await RefreshPresetSummariesAsync();
        if (selectedPreset != key || revision != presetLoadRevision || !PresetToolbar.IsEditingPreset) return;
        if (editorDrafts.TryGetValue(key, out var retainedDraft)) ApplyDraft(retainedDraft, animate: true);
        bool hasSavedDraft = envelope is not null && selectedSavedDraft is not null;
        SetDirty(editorDrafts.ContainsKey(key) || !hasSavedDraft);
        PresetToolbar.SetEditingState(key, dirty, hasSavedDraft);
        SetInteractionAvailability(currentState is not null);
    }

    private Task ApplyPresetRecommendationsAsync(PresetKey key, bool animate)
    {
        var profile = DefaultPerformancePresets.CreateDraft(key);
        TemperatureRow.RecommendedValue = profile.TemperatureLimitC;
        SustainedPowerRow.RecommendedValue = profile.SplWatts;
        BurstPowerRow.RecommendedValue = profile.SpptWatts;
        FrequencyRow.RecommendedValue = profile.AcMaxFrequencyMhz;
        if (!animate)
        {
            TemperatureRow.SetRecommendedValue(profile.TemperatureLimitC);
            SustainedPowerRow.SetRecommendedValue(profile.SplWatts);
            BurstPowerRow.SetRecommendedValue(profile.SpptWatts);
            FrequencyRow.SetRecommendedValue(profile.AcMaxFrequencyMhz);
            return Task.CompletedTask;
        }
        return Task.WhenAll(
            TemperatureRow.SetRecommendedValueAnimatedAsync(profile.TemperatureLimitC),
            SustainedPowerRow.SetRecommendedValueAnimatedAsync(profile.SplWatts),
            BurstPowerRow.SetRecommendedValueAnimatedAsync(profile.SpptWatts),
            FrequencyRow.SetRecommendedValueAnimatedAsync(profile.AcMaxFrequencyMhz));
    }

    private void ApplyDraft(PerformanceDraft draft, bool animate = false)
    {
        synchronizing = true;
        _ = ApplyPresetRecommendationsAsync(selectedPreset, animate);
        if (animate)
        {
            _ = TemperatureRow.SetValueAnimatedAsync(draft.TemperatureLimitC);
            _ = SustainedPowerRow.SetValueAnimatedAsync(draft.SplWatts);
            _ = BurstPowerRow.SetValueAnimatedAsync(draft.SpptWatts);
        }
        else
        {
            TemperatureRow.SetValue(draft.TemperatureLimitC);
            SustainedPowerRow.SetValue(draft.SplWatts);
            BurstPowerRow.SetValue(draft.SpptWatts);
        }
        acFrequencyMhz = draft.AcMaxFrequencyMhz;
        dcFrequencyMhz = draft.DcMaxFrequencyMhz;
        windowsPowerSchemeDraftId = draft.WindowsPowerSchemeId;
        UpdatePowerSchemeSelection();
        if (animate) _ = FrequencyRow.SetValueAnimatedAsync(editingAc ? acFrequencyMhz : dcFrequencyMhz);
        else FrequencyRow.SetValue(editingAc ? acFrequencyMhz : dcFrequencyMhz);
        AdvancedWorkspace.ApplyDraft(draft.AdvancedCpuTuning, draft.NegativeCurveOptimizer,
            draft.AcMinActiveCoresPercent, draft.DcMinActiveCoresPercent);
        AdvancedWorkspace.SetAllCoreCurveOffset(Math.Clamp(AdvancedWorkspace.AllCoreCurveValue, -30, 0));
        BoostToggle.IsOn = draft.IsBoostEnabled;
        synchronizing = false;
        RefreshBoundaryFromEditor();
        SetDirty(true);
    }

    private PerformanceDraft? TryCreateDraft()
    {
        if (currentState is null)
            return null;

        if (!PresetToolbar.IsEditingPreset &&
            (currentState.TemperatureLimitC is null || currentState.SplWatts is null || currentState.SpptWatts is null ||
             currentState.AcFrequency() is null || currentState.DcFrequency() is null || currentState.BoostEnabled is null ||
             TemperatureRow.Value is null || SustainedPowerRow.Value is null || BurstPowerRow.Value is null))
            return null;

        int temperature = (int)Math.Round(TemperatureRow.Value ?? TemperatureRow.RecommendedValue);
        int sustained = (int)Math.Round(SustainedPowerRow.Value ?? SustainedPowerRow.RecommendedValue);
        int burst = (int)Math.Round(BurstPowerRow.Value ?? BurstPowerRow.RecommendedValue);
        int ac = acFrequencyMhz ?? currentState.AcFrequency() ?? (int)Math.Round(FrequencyRow.RecommendedValue);
        int dc = dcFrequencyMhz ?? currentState.DcFrequency() ?? (int)Math.Round(FrequencyRow.RecommendedValue);

        return new PerformanceDraft
        {
            TemperatureLimitC = temperature,
            SplWatts = sustained,
            SpptWatts = burst,
            AcMaxFrequencyMhz = ac,
            DcMaxFrequencyMhz = dc,
            IsBoostEnabled = BoostToggle.IsOn,
            WindowsPowerSchemeId = windowsPowerSchemeDraftId ?? currentState.WindowsPowerSchemeId ?? Guid.Empty,
            AcMinActiveCoresPercent = AdvancedWorkspace.DraftCoreParking?.Ac,
            DcMinActiveCoresPercent = AdvancedWorkspace.DraftCoreParking?.Dc,
            NegativeCurveOptimizer = null,
            AdvancedCpuTuning = AdvancedWorkspace.ReadDraft()
        };
    }

    private void SetDirty(bool value, bool updateStatus = true)
    {
        if (value && selectedSavedDraft is not null && TryCreateDraft() is { } currentDraft &&
            JsonSerializer.Serialize(currentDraft, PresetJson) == JsonSerializer.Serialize(selectedSavedDraft, PresetJson))
            value = false;
        dirty = value;
        PresetToolbar.SetEditingState(selectedPreset, dirty: value, saved: selectedSavedDraft is not null && !value);
        if (updateStatus)
            _ = PresetToolbar.SetDirtyStatusAsync(value);
        SetInteractionAvailability(currentState is not null && !applyInProgress);
    }


    private async void OnAdvancedClick(object sender, RoutedEventArgs e) => await ShowAdvancedAnimatedAsync(!advancedOpen);

    private void OnCurveOffsetChanged(object? sender, double value)
    {
        if (!synchronizing) AdvancedWorkspace.SetAllCoreCurveOffset(Math.Clamp(value, -30, 0));
    }

    private void OnCurveModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!synchronizing && CurveModeSelector.SelectedIndex is >= 0 and <= 2)
        {
            if (!PresetToolbar.IsEditingPreset) liveCurveMode = (CpuCurveOptimizerMode)CurveModeSelector.SelectedIndex;
            AdvancedWorkspace.SelectCurveMode((CpuCurveOptimizerMode)CurveModeSelector.SelectedIndex);
            if (CurveModeSelector.SelectedIndex == 2) _ = ShowAdvancedAnimatedAsync(true);
        }
    }

    private void OnAdvancedChanged(object? sender, EventArgs e)
    {
        if (!synchronizing)
        {
            synchronizing = true;
            var advanced = AdvancedWorkspace.ReadDraft();
            if (advanced?.FastPptWatts is double fast) BurstPowerRow.SetValue(fast);
            if (advanced?.Mp1TemperatureC is int temperature) TemperatureRow.SetValue(temperature);
            synchronizing = false;
            RefreshBoundaryFromEditor();
            OnUserDraftEdited();
        }
    }

    private void ShowAdvanced(bool show)
    {
        advancedOpen = show;
        AdvancedWorkspace.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AdvancedStateText.Text = show ? "已展开" : "未展开";
        if (AdvancedChevronIcon.RenderTransform is RotateTransform chevron)
            chevron.Angle = show ? 90d : 0d;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            AdvancedButton,
            show ? "收起高级 CPU 调校" : "展开高级 CPU 调校");
        if (show)
            AdvancedWorkspace.SetAvailability(currentState is not null && !applyInProgress,
                advancedCapabilityAvailable && !applyInProgress, pboCapabilityAvailable && !applyInProgress);
    }

    private async Task ShowAdvancedAnimatedAsync(bool show)
    {
        if (!new UISettings().AnimationsEnabled)
        {
            ShowAdvanced(show);
            return;
        }

        if (show)
        {
            ShowAdvanced(true);
            AdvancedWorkspace.Height = double.NaN;
            AdvancedWorkspace.UpdateLayout();
            double targetHeight = AdvancedWorkspace.ActualHeight;
            AdvancedWorkspace.Height = 0d;
            await Task.WhenAll(
                AnimateAsync(AdvancedWorkspace, 0d, 1d, 12d, 0d, 220, EasingMode.EaseOut),
                AnimateAdvancedHeightAsync(0d, targetHeight, 220),
                AnimateChevronAsync(0d, 90d, 220, EasingMode.EaseOut));
            AdvancedWorkspace.Height = double.NaN;
        }
        else
        {
            double currentHeight = AdvancedWorkspace.ActualHeight;
            await Task.WhenAll(
                AnimateAsync(AdvancedWorkspace, 1d, 0d, 0d, -6d, 280, EasingMode.EaseIn),
                AnimateAdvancedHeightAsync(currentHeight, 0d, 280),
                AnimateChevronAsync(90d, 0d, 280, EasingMode.EaseIn));
            ShowAdvanced(false);
            AdvancedWorkspace.Height = double.NaN;
            AdvancedWorkspace.Opacity = 1d;
            AdvancedWorkspace.RenderTransform = null;
        }
    }

    private Task AnimateAdvancedHeightAsync(double from, double to, int durationMs)
    {
        var completion = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            double progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / durationMs, 0d, 1d);
            double eased = 1d - Math.Pow(1d - progress, 3d);
            AdvancedWorkspace.Height = from + (to - from) * eased;
            if (progress < 1d)
                return;

            CompositionTarget.Rendering -= handler;
            AdvancedWorkspace.Height = to;
            completion.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        return completion.Task;
    }

    private Task AnimateChevronAsync(double from, double to, int durationMs, EasingMode easingMode)
    {
        var completion = new TaskCompletionSource();
        var transform = (RotateTransform)AdvancedChevronIcon.RenderTransform;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "Angle");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => { transform.Angle = to; completion.TrySetResult(); };
        storyboard.Begin();
        return completion.Task;
    }

    private static Task AnimateAsync(FrameworkElement element, double fromOpacity, double toOpacity, double fromY, double toY, int milliseconds, EasingMode easingMode)
    {
        var completion = new TaskCompletionSource();
        var transform = new TranslateTransform { Y = fromY };
        element.RenderTransform = transform;
        element.Opacity = fromOpacity;
        var easing = new CubicEase { EasingMode = easingMode };
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = TimeSpan.FromMilliseconds(milliseconds), EasingFunction = easing };
        var translate = new DoubleAnimation { From = fromY, To = toY, Duration = TimeSpan.FromMilliseconds(milliseconds), EasingFunction = easing };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translate, transform);
        Storyboard.SetTargetProperty(translate, "Y");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translate);
        storyboard.Completed += (_, _) => completion.SetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private void RefreshBoundaryFromEditor()
    {
        bool wasSynchronizing = synchronizing;
        synchronizing = true;
        bool allCore = AdvancedWorkspace.CurveMode == CpuCurveOptimizerMode.AllCore;
        double curve = AdvancedWorkspace.AllCoreCurveValue;
        CurveModeSelector.SelectedIndex = (int)AdvancedWorkspace.CurveMode;
        CurveOptimizerRow.SetValue(PresetToolbar.IsEditingPreset || liveEditPending || liveEditApplying || liveCurveReadbackKnown ? curve : null);
        CurveOptimizerRow.SetDescription(allCore
            ? "全核负偏移"
            : AdvancedWorkspace.CurveMode == CpuCurveOptimizerMode.PerCore ? "逐核偏移" : "沿用 BIOS");
        CurveOptimizerRow.SetEditorEnabled(allCore && curveOptimizerCapabilityAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading);
        CurveModeSelector.IsEnabled = curveOptimizerCapabilityAvailable && !applyInProgress && !liveEditApplying && !presetEditorLoading;
        synchronizing = wasSynchronizing;
        CpuBoundary.ApplyLimits(
            currentState?.TemperatureLimitC,
            currentState?.SplWatts,
            editingAc ? currentState?.AcFrequency() : currentState?.DcFrequency(),
            voltage: null,
            automaticVoltage: true);
    }

    private void UpdateCoreTopology(int? enabledCores)
    {
        AdvancedWorkspace.SetCoreCount(enabledCores);
    }

    private static bool IsCapabilityAvailable(HomeStateSnapshot snapshot, string key) =>
        snapshot.Capabilities.Items.Any(item =>
            string.Equals(item.Key, key, StringComparison.Ordinal) &&
            item.State == CapabilityState.Available);

    private static Brush Brush(string key) =>
        (Brush)Application.Current.Resources[key];
}
