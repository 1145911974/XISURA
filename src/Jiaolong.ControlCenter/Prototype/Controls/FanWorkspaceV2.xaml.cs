using System.Diagnostics;
using System.Text.Json;
using Jiaolong_ControlCenter.Services;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class FanWorkspaceV2 : UserControl
{
    private readonly UISettings uiSettings = new();
    private double? targetCpuRpm;
    private double? targetGpuRpm;
    private double smoothedCpuRpm;
    private double smoothedGpuRpm;
    private long lastFrameTimestamp;
    private bool rendering;
    private bool pageActive;
    private bool reducedMotion;
    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            if (FanCurveWorkspace is not null) FanCurveWorkspace.ReducedMotion = value;
            if (value)
            {
                StopStrategyPanelTransition();
                SettleStrategyPanels();
            }
        }
    }
    private readonly TelemetryTransition[] fanTemperatures = [new(), new()];
    private readonly Stopwatch fanTextClock = Stopwatch.StartNew();
    private bool syncingStrongCooling;
    public event Action<bool>? StrongCoolingRequested;
    public void SetPageActive(bool active) { pageActive = active; UpdateMotionState(); }
    private readonly ControlPresetStore presetStore = new();
    private readonly Dictionary<PresetKey, FanCurveState> curveDrafts = [];
    private PresetKey editingKey = PresetKey.Create(ControlModeId.Gaming, 1);
    private int presetLoadVersion;
    private bool syncingPreset;
    private bool importingPreset;
    private bool savingPreset;
    private bool applyingPreset;
    private bool fanEditorLoading;
    private HomeControlSession? session;
    private bool maximumRpmAvailable;
    private bool automaticCeilingActive;
    private Storyboard? strategyPanelTransition;
    public void AttachSession(HomeControlSession value)
    {
        session = value;
        if (value.State is { } state) ApplyControlState(state);
    }
    public void ApplyControlState(HomeStateSnapshot state)
    {
        UpdateFanActiveBadge(state);
        if (!PresetToolbar.IsEditingPreset && !applyingPreset && !liveFanQueued) RestoreLiveFanState(state);
        maximumRpmAvailable = state.Controls.FanAutomaticCeilingAvailable;
        automaticCeilingActive = state.Controls.FanAutomaticCeilingActive;
        if (StrategyHelp is not null)
            StrategyHelp.Help = StrategyHelp.Help with
            {
                CurrentValue = !maximumRpmAvailable
                    ? "服务未报告 EC 自动目标上限能力。"
                    : automaticCeilingActive ? "当前 EC 自动目标上限已启用。" : "当前 EC 自动目标上限未启用。"
            };
        UpdateMaximumRpmAvailability();
    }
    private readonly HashSet<PresetKey> dirtyPresets = [];
    private readonly HashSet<PresetKey> savedPresets = [];
    private int draftRevision;
    private FanCurveState CaptureDraft() => FanCurveWorkspace.Export() with
    {
        Strategy = AutoStrategy.IsChecked == true ? "Auto" : FixedStrategy.IsChecked == true ? "Fixed" : "Curve",
        FixedRpm = (int)FixedTarget.Value,
        MaximumRpm = AutoStrategy.IsChecked == true && MaximumRpmToggle.IsOn ? (int)MaximumRpmTarget.Value : null
    };
    private void RestoreDraft(FanCurveState state)
    {
        importingPreset = true;
        FanCurveWorkspace.Import(state);
        (state.Strategy == "Auto" ? AutoStrategy : state.Strategy == "Fixed" ? FixedStrategy : CurveStrategy).IsChecked = true;
        FixedTarget.Value = state.FixedRpm;
        MaximumRpmToggle.IsOn = state.Strategy == "Auto" && state.MaximumRpm.HasValue;
        MaximumRpmTarget.Value = state.Strategy == "Auto" ? state.MaximumRpm ?? 5800 : 5800;
        UpdateStrategyPanels();
        UpdateMaximumRpmAvailability();
        importingPreset = false;
    }
    private void MarkDraftChanged()
    {
        if (importingPreset || fanEditorLoading || !IsLoaded) return;
        if (!PresetToolbar.IsEditingPreset) UpdateLiveFanPresentation();
        if (!PresetToolbar.IsEditingPreset) { PresetToolbar.SetCurrentSettingsModified(); QueueLiveFanChange(); return; }
        draftRevision++;
        dirtyPresets.Add(editingKey);
        PresetToolbar.SetEditingState(editingKey, true, false);
        PresetToolbar.SetActionAvailability(!savingPreset && !applyingPreset, true);
        _ = PresetToolbar.SetDirtyStatusAsync(true);
    }

    public FanWorkspaceV2()
    {
        InitializeComponent();
        IsFollowingPreset = followPreferences.Load().FanFollowPreset;
        PresetToolbar.ConfigureFollowPreset(IsFollowingPreset);
        PresetToolbar.FollowPresetChanged += OnFollowPresetChanged;
        FanCurveWorkspace.ReducedMotion = reducedMotion;
        ApplyStrongCoolingState(null, false);
        OverviewHelp.Help = new Jiaolong_ControlCenter.Controls.ParameterHelpContent(
            "分别显示两只风扇的实际转速与温度，帮助观察散热、噪声与双扇差异；叶轮随各自回读转速平滑变化。",
            "以实时遥测为准",
            "结合温度和持续负载看趋势，不用动画快慢代替实际 RPM。",
            "这是只读总览，保持当前控制策略；0 RPM 表示回读停转，未知时不模拟旋转。",
            "竖条 0–7000 RPM 只是显示量程，不是风扇硬件最高值；实际读数始终保留。",
            "动画速度不代表叶轮物理速度，不能据此判断安全转速。");
        StrategyHelp.Help = new Jiaolong_ControlCenter.Controls.ParameterHelpContent(
            "EC 自动交由主板调速；固定目标保持指定 RPM；温度曲线随温度改变目标。提高目标通常增强散热并增加噪声，降低可能让温度上升。",
            "日常页面调整当前电脑；管理预设时仅编辑，保存并应用才下发。EC 自动目标上限状态以服务报告为准。",
            "日常优先 EC 自动，或使用当前模式的推荐曲线；固定目标及 EC 上限软件范围 1800–5800 RPM。",
            "保留 EC 自动；降低噪声时逐步调整并观察温度，不把低转速直接当作安全值。",
            "5800 RPM 仅为目标输入上限；CPU 达 95°C 或 GPU 达 87°C 会立即交还 EC，保护优先。强冷采用风扇支持的最高目标。",
            "EC 上限不是实际转速硬上限：超过目标 100 RPM 才临时接管，降温 3°C 后交还 EC。能力和启用状态以服务报告为准，强冷不改变性能模式。");
        Loaded += OnLoaded;
        PresetToolbar.SelectedKey = editingKey;
        PresetToolbar.SetSlotSummary("风扇曲线 · 固定目标 · 控制策略");
        PresetToolbar.SetActionAvailability(true, false);
        PresetToolbar.SaveRequested += OnSavePreset;
        PresetToolbar.UseRequested += OnUsePreset;
        PresetToolbar.EditingModeChanged += OnFanEditingModeChanged;
        PresetToolbar.PresetUseRequested += OnFanPresetUseRequested;
        PresetToolbar.SaveAsRequested += OnFanSaveAsRequested;
        FixedTargetSlider.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => fanSliderPointerHeld = true), true);
        FixedTargetSlider.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => fanSliderPointerHeld = false), true);
        FixedTargetSlider.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => fanSliderPointerHeld = false), true);
        FanCurveWorkspace.DraftChanged += (_, _) => MarkDraftChanged();
        PresetToolbar.SelectedKeyChanged += async (_, key) => { if (!syncingPreset && PresetToolbar.IsEditingPreset) await SelectPresetAsync(key); };
        Loaded += (_, _) => { if (!PresetToolbar.IsEditingPreset && session?.State is { } current) RestoreLiveFanState(current); };
        Unloaded += (_, _) => StopMotion();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateMotionState());
    }

    private async Task SelectPresetAsync(PresetKey key, bool preserve = true)
    {
        if (!PresetToolbar.IsEditingPreset) return;
        if (preserve && !fanEditorLoading) curveDrafts[editingKey] = CaptureDraft();
        draftRevision++;
        int version = ++presetLoadVersion;
        fanEditorLoading = true;
        IsEnabled = false;
        PresetToolbar.SetActionAvailability(false, false);
        editingKey = key;
        syncingPreset = true;
        PresetToolbar.SelectedKey = key;
        syncingPreset = false;
        FanCurveWorkspace.SetPresetCaption(PresetToolbar.SelectedDisplayName);
        PresetToolbar.SetEditingState(key, dirtyPresets.Contains(key), savedPresets.Contains(key));
        _ = PresetToolbar.SetDirtyStatusAsync(dirtyPresets.Contains(key));
        int profile = key.Mode == ControlModeId.Office ? 0 : key.Mode == ControlModeId.Turbo ? 2 : 1;
        FanCurveWorkspace.SelectProfile(profile);
        if (curveDrafts.TryGetValue(key, out var draft))
        {
            RestoreDraft(draft);
            fanEditorLoading = false;
            IsEnabled = true;
            FanCurveWorkspace.IsEnabled = true;
            PresetToolbar.SetActionAvailability(true, true);
            return;
        }
        var defaults = new FanCurveDraft(profile);
        RestoreDraft(new(profile, false, defaults.Cpu.ToArray(), defaults.Gpu.ToArray(), defaults.Shared.ToArray()));
        FanCurveWorkspace.IsEnabled = false;
        PresetToolbar.SetActionAvailability(false, false);
        try
        {
            var saved = await presetStore.LoadAsync(ControlPageId.Fan, key, CancellationToken.None);
            if (version != presetLoadVersion || !PresetToolbar.IsEditingPreset || PresetToolbar.SelectedKey != key) return;
            if (saved is not null)
            {
                var state = saved.Payload.Deserialize<FanCurveState>();
                if (saved.SchemaVersion == 1 && state?.IsValid() == true && state.Profile == profile)
                {
                    RestoreDraft(state);
                    savedPresets.Add(key);
                    PresetToolbar.SetEditingState(key, dirtyPresets.Contains(key), true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (version == presetLoadVersion) await PresetToolbar.ShowStatusAsync("读取预设失败，显示推荐曲线");
        }
        finally
        {
            if (version == presetLoadVersion)
            {
                fanEditorLoading = false;
                IsEnabled = true;
                FanCurveWorkspace.IsEnabled = true;
                PresetToolbar.SetActionAvailability(true, true);
                curveDrafts[key] = CaptureDraft();
            }
        }
    }

    private static string FormatWarning(FanCurveState state, FanCurveWarning warning) =>
        state.Strategy == "Fixed"
            ? $"{warning.Series} {warning.StartC}–{warning.EndC}°C：固定目标 {state.FixedRpm / 100 * 100} RPM，本档建议约 {(1800 + warning.RecommendedPercent * 40 + 99) / 100 * 100} RPM"
            : $"{warning.Series} {warning.StartC}–{warning.EndC}°C：目标最低 {warning.MinimumPercent}%，本档建议约 {warning.RecommendedPercent}%";

    private async void OnSavePreset(object? sender, EventArgs e) => await SaveFanPresetAsync();

    private async Task<bool> SaveFanPresetAsync(PresetKey? target = null)
    {
        if (savingPreset || applyingPreset || fanEditorLoading) return false;
        if (!PresetToolbar.IsEditingPreset && (liveFanQueued || session?.State is null))
        { await PresetToolbar.ShowStatusAsync("请等待当前风扇设置读回后再保存"); return false; }
        var saveKey = target ?? (PresetToolbar.IsEditingPreset ? editingKey : PresetToolbar.CurrentSourceKey);
        if (saveKey is null) { await PresetToolbar.ShowStatusAsync("请选择另存为的位置"); return false; }
        savingPreset = true;
        var key = saveKey.Value;
        int profile = key.Mode == ControlModeId.Office ? 0 : key.Mode == ControlModeId.Turbo ? 2 : 1;
        var state = CaptureDraft() with { Profile = profile };
        if (!PresetToolbar.IsEditingPreset)
        {
            var plan = session!.State!.Controls.ActiveFanControlPlan;
            var defaults = new FanCurveDraft(profile);
            var cpu = plan?.Points.Select(p => new CurvePoint(p.TemperatureC, p.Percent)).ToArray() ?? defaults.Cpu.ToArray();
            var gpu = plan?.GpuPoints?.Select(p => new CurvePoint(p.TemperatureC, p.Percent)).ToArray() ?? cpu;
            state = new FanCurveState(profile, plan?.GpuPoints is null, cpu, gpu, cpu, plan?.Strategy ?? "Auto", plan?.FixedRpm ?? 3000)
                { MaximumRpm = plan?.MaximumRpm };
        }
        int revision = draftRevision;
        if (!state.IsValid()) { savingPreset = false; await PresetToolbar.ShowStatusAsync("预设数值无效"); return false; }
        PresetToolbar.SetActionAvailability(false, false);
        try
        {
            var warnings = FanCurveSafety.Assess(state);
            if (warnings.Count > 0)
            {
                string intervals = string.Join("\n", warnings.Select(w => FormatWarning(state, w)));
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "高温段风扇转速偏低",
                    Content = new TextBlock
                    {
                        Text = $"{intervals}\n\n可能导致温度过高。请提高转速、降低负载，或准备其他散热方式。此操作仅保存本地预设，尚未应用到硬件。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = "仍要保存",
                    CloseButtonText = "继续修改",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
            }
            await presetStore.SaveAsync(new PagePresetEnvelope(1, ControlPageId.Fan, key,
                PresetToolbar.DisplayNameFor(key), JsonSerializer.SerializeToElement(state), DateTimeOffset.UtcNow), CancellationToken.None);
            if (appliedFanKey == key && (appliedFanPreset is not { } applied ||
                applied.Profile != state.Profile || applied.IsShared != state.IsShared || applied.Strategy != state.Strategy ||
                applied.FixedRpm != state.FixedRpm || applied.MaximumRpm != state.MaximumRpm ||
                !applied.Cpu.SequenceEqual(state.Cpu) || !applied.Gpu.SequenceEqual(state.Gpu) || !applied.Shared.SequenceEqual(state.Shared)))
            {
                appliedFanKey = null; appliedFanPreset = null;
                PresetToolbar.SetConfirmedActivePreset(null);
            }
            curveDrafts[key] = state;
            savedPresets.Add(key);
            if (!PresetToolbar.IsEditingPreset && session?.State is { } current)
            { appliedFanPreset = state; appliedFanKey = key; UpdateFanActiveBadge(current); }
            if (editingKey == key && revision == draftRevision)
            {
                dirtyPresets.Remove(key);
                PresetToolbar.SetEditingState(key, false, true);
                await PresetToolbar.ShowSavedStatusAsync();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await PresetToolbar.ShowStatusAsync("保存失败，请重试");
            return false;
        }
        finally
        {
            savingPreset = false;
            PresetToolbar.SetActionAvailability(true, PresetToolbar.IsEditingPreset);
        }
    }

    private async void OnUsePreset(object? sender, EventArgs e)
    {
        if (PresetToolbar.IsEditingPreset)
        {
            if (!await SaveFanPresetAsync()) return;
            if (await ApplyFanStateAsync()) PresetToolbar.ExitPresetManagement();
        }
        else await ApplyFanStateAsync(CaptureDraft());
    }

    private async void OnFanPresetUseRequested(object? sender, PresetKey key)
    {
        if (savingPreset || applyingPreset || fanEditorLoading) return;
        if (PresetToolbar.IsEditingPreset) { await SelectPresetAsync(key); return; }
        ++liveFanRevision; liveFanQueued = false;
        editingKey = key;
        await ApplyFanStateAsync();
        if (session?.State is { } current) RestoreLiveFanState(current);
    }

    private async void OnFanEditingModeChanged(object? sender, bool editing)
    {
        ++liveFanRevision; liveFanQueued = false; ++presetLoadVersion;
        if (editing) await SelectPresetAsync(PresetToolbar.SelectedKey, preserve: false);
        else
        {
            if (!fanEditorLoading && dirtyPresets.Contains(editingKey)) curveDrafts[editingKey] = CaptureDraft();
            fanEditorLoading = false;
            IsEnabled = true;
            if (session?.State is { } current) RestoreLiveFanState(current);
            PresetToolbar.SetActionAvailability(true, false);
            if (pendingFanFollowTarget is { } pending)
            { pendingFanFollowTarget = null; followFanTarget = null; SetFollowPresetTarget(pending); }
        }
    }

    private async void OnFanSaveAsRequested(object? sender, PresetKey key) => await SaveFanPresetAsync(key);

    private async Task<bool> ApplyFanStateAsync(FanCurveState? liveState = null)
    {
        if (applyingPreset || savingPreset || fanEditorLoading || session is null) return false;
        if (session.State?.Capabilities.Items.Any(item => item.Key == "fanControl" && item.State == CapabilityState.Available) != true)
        {
            await PresetToolbar.ShowStatusAsync("风扇驱动或设备链路不可用");
            return false;
        }
        var key = editingKey;
        applyingPreset = true;
        IsEnabled = false;
        PresetToolbar.SetActionAvailability(false, false);
        try
        {
            var saved = liveState is null ? await presetStore.LoadAsync(ControlPageId.Fan, key, CancellationToken.None) : null;
            var state = liveState ?? (saved?.SchemaVersion == 1 ? saved.Payload.Deserialize<FanCurveState>() : null);
            int profile = key.Mode == ControlModeId.Office ? 0 : key.Mode == ControlModeId.Turbo ? 2 : 1;
            if (state?.IsValid() != true || liveState is null && state.Profile != profile)
            {
                savedPresets.Remove(key);
                await PresetToolbar.ShowStatusAsync("已保存预设无效，请重新保存");
                return false;
            }
            var warnings = FanCurveSafety.Assess(state);
            string intervals = string.Join("\n", warnings.Select(w => FormatWarning(state, w)));
            string strategy = state.Strategy switch
            {
                "Auto" => state.MaximumRpm is int ceiling
                    ? $"EC 自动最大目标转速 {ceiling} RPM（约束控制目标，不是实时转速硬上限）"
                    : "交还 EC 自动控制",
                "Fixed" => $"固定转速 {state.FixedRpm} RPM",
                _ => state.IsShared ? "CPU/GPU 共享温度曲线" : "CPU/GPU 独立温度曲线"
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = warnings.Count > 0 ? "高温段风扇转速偏低" : "使用风扇预设",
                Content = new TextBlock
                {
                    Text = $"将使用「{(saved?.DisplayName ?? "实时设置")}」：{strategy}。" +
                        (warnings.Count == 0 ? "" : $"\n\n{intervals}\n\n应用后可能导致温度过高。请提高转速、降低负载，或准备其他散热方式。"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "确认应用",
                CloseButtonText = warnings.Count > 0 ? "继续修改" : "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (warnings.Count > 0 && await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await PresetToolbar.ShowStatusAsync("已取消；没有提交命令");
                return false;
            }
            bool automaticCeilingRequested = state.Strategy == "Auto" && state.MaximumRpm is not null;
            if (automaticCeilingRequested && session.State?.Controls.FanAutomaticCeilingAvailable != true)
            {
                await PresetToolbar.ShowStatusAsync("当前服务不支持 EC 自动目标上限，请先更新服务；未提交风扇控制");
                return false;
            }

            HardwareCommand command = state.Strategy == "Auto" && !automaticCeilingRequested
                ? new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested)
                : new SetFanControlCommand(Guid.NewGuid(), new FanControlPlan(
                    (state.IsShared ? state.Shared : state.Cpu).Select(p => new FanPoint(p.Temperature, p.TargetPercent)).ToArray())
                {
                    GpuPoints = state.IsShared ? null : state.Gpu.Select(p => new FanPoint(p.Temperature, p.TargetPercent)).ToArray(),
                    Strategy = state.Strategy,
                    FixedRpm = state.Strategy == "Fixed" ? state.FixedRpm : null,
                    MaximumRpm = automaticCeilingRequested ? state.MaximumRpm : null
                }, RiskConfirmed: true);
            var result = await session.ExecuteAsync(command, CancellationToken.None);
            bool succeeded = result.State == CommandState.Applied && result.Error is null;
            await PresetToolbar.ShowStatusAsync(succeeded
                ? state.Strategy == "Auto"
                    ? automaticCeilingRequested ? $"已设置 EC 自动最大目标转速 {state.MaximumRpm} RPM；实际 RPM 请查看风扇总览" : "已交还 EC 自动控制"
                    : "指令已下发 · 请查看实际 RPM"
                : result.Error?.Code == Jiaolong.Contracts.Errors.ErrorCode.ConflictDetected
                    ? "二创控制台正在运行，请先退出后重试"
                    : $"应用失败：{result.Error?.Code}");
            appliedFanPreset = succeeded && liveState is null ? state : null;
            appliedFanKey = appliedFanPreset is null ? null : key;
            if (succeeded && liveState is null)
            {
                try { followPreferences.Update(value => value with { FanPresetSlots = new(value.FanPresetSlots) { [key.Mode.ToString()] = key.Slot } }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await PresetToolbar.ShowStatusAsync("配置已应用，但记忆档位保存失败"); }
            }
            if (session.State is { } confirmed) UpdateFanActiveBadge(confirmed);
            else PresetToolbar.SetConfirmedActivePreset(null);
            return succeeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            await PresetToolbar.ShowStatusAsync("读取或应用预设失败，请检查服务连接");
            return false;
        }
        catch (Exception)
        {
            await PresetToolbar.ShowStatusAsync("服务通信失败，未确认风扇生效");
            return false;
        }
        finally
        {
            applyingPreset = false;
            IsEnabled = true;
            PresetToolbar.SetActionAvailability(true, PresetToolbar.IsEditingPreset);
        }
    }

    public void ApplyTelemetry(HardwareSnapshot snapshot)
    {
        double seconds = pageActive && IsLoaded && !ReducedMotion && uiSettings.AnimationsEnabled && XamlRoot?.IsHostVisible == true
            ? TelemetryTransition.LiveSampleDurationSeconds : 0;
        fanTemperatures[0].SetTarget(snapshot.CpuTemperatureC, fanTextClock.Elapsed.TotalSeconds, seconds);
        fanTemperatures[1].SetTarget(snapshot.GpuTemperatureC, fanTextClock.Elapsed.TotalSeconds, seconds);
        if (targetCpuRpm is null) smoothedCpuRpm = ValidRpm(snapshot.CpuFanRpm) ?? 0;
        if (targetGpuRpm is null) smoothedGpuRpm = ValidRpm(snapshot.GpuFanRpm) ?? 0;
        targetCpuRpm = ValidRpm(snapshot.CpuFanRpm);
        targetGpuRpm = ValidRpm(snapshot.GpuFanRpm);
        if (seconds == 0)
        {
            smoothedCpuRpm = targetCpuRpm ?? 0;
            smoothedGpuRpm = targetGpuRpm ?? 0;
        }
        UpdateGauges(smoothedCpuRpm, smoothedGpuRpm);
        UpdateMotionState();
    }

    private void OnStrategyChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool enabled = (sender as FrameworkElement)?.Tag?.ToString() == "Fixed";
        if (FixedTarget is not null) FixedTarget.IsEnabled = enabled;
        if (FixedTargetSlider is not null) FixedTargetSlider.IsEnabled = enabled;
        UpdateStrategyPanels();
        UpdateMaximumRpmAvailability();
        if (!PresetToolbar.IsEditingPreset && !importingPreset) UpdateLiveFanPresentation();
        if (FanCurveWorkspace is not null) MarkDraftChanged();
    }
    private void UpdateStrategyPanels()
    {
        if (AutomaticCeilingPanel is null || FixedTargetPanel is null) return;
        StopStrategyPanelTransition();
        if (!IsLoaded || ReducedMotion || !uiSettings.AnimationsEnabled)
        {
            SettleStrategyPanels();
            return;
        }
        var storyboard = new Storyboard();
        foreach (FrameworkElement panel in new FrameworkElement[] { AutomaticCeilingPanel, FixedTargetPanel })
        {
            var active = ReferenceEquals(panel, AutomaticCeilingPanel)
                ? AutoStrategy.IsChecked == true : FixedStrategy.IsChecked == true;
            var fade = new DoubleAnimation
            {
                From = panel.Opacity, To = active ? 1 : 0.48,
                Duration = TimeSpan.FromMilliseconds(210),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, panel);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            storyboard.Children.Add(fade);
        }
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(storyboard, strategyPanelTransition)) return;
            StopStrategyPanelTransition();
            SettleStrategyPanels();
        };
        strategyPanelTransition = storyboard;
        storyboard.Begin();
    }

    private void StopStrategyPanelTransition()
    {
        var automaticOpacity = AutomaticCeilingPanel.Opacity;
        var fixedOpacity = FixedTargetPanel.Opacity;
        strategyPanelTransition?.Stop();
        strategyPanelTransition = null;
        AutomaticCeilingPanel.Opacity = automaticOpacity;
        FixedTargetPanel.Opacity = fixedOpacity;
    }

    private void SettleStrategyPanels()
    {
        AutomaticCeilingPanel.Opacity = AutoStrategy.IsChecked == true ? 1 : 0.48;
        FixedTargetPanel.Opacity = FixedStrategy.IsChecked == true ? 1 : 0.48;
    }
    private bool syncingFixedTarget;
    private void UpdateMaximumRpmAvailability()
    {
        if (MaximumRpmToggle is null || MaximumRpmTarget is null) return;
        MaximumRpmToggle.IsEnabled = maximumRpmAvailable && AutoStrategy.IsChecked == true;
        MaximumRpmTarget.IsEnabled = MaximumRpmToggle.IsEnabled && MaximumRpmToggle.IsOn;
    }
    private void OnMaximumRpmChanged(object sender, RoutedEventArgs e)
    {
        UpdateMaximumRpmAvailability();
        MarkDraftChanged();
    }
    private void OnMaximumRpmValueChanged(object? sender, double value) => MarkDraftChanged();
    private void OnFixedSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (syncingFixedTarget || FixedTarget is null) return;
        syncingFixedTarget = true; FixedTarget.Value = e.NewValue; syncingFixedTarget = false;
        MarkDraftChanged();
    }
    private void OnFixedValueChanged(object? sender, double value)
    {
        if (syncingFixedTarget || FixedTargetSlider is null) return;
        syncingFixedTarget = true; FixedTargetSlider.Value = value; syncingFixedTarget = false;
        MarkDraftChanged();
    }
    private void OnStrongCoolingChanged(object sender, RoutedEventArgs e)
    {
        if (syncingStrongCooling) return;
        bool cooling = StrongCoolingButton.IsChecked == true;
        StrongCoolingRequested?.Invoke(cooling);
    }

    public void ApplyStrongCoolingState(bool? enabled, bool available)
    {
        syncingStrongCooling = true;
        try
        {
            StrongCoolingButton.IsEnabled = available && enabled.HasValue;
            StrongCoolingButton.IsChecked = enabled == true;
        }
        finally { syncingStrongCooling = false; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateMotionState();

    private void UpdateMotionState()
    {
        if (!IsLoaded || !pageActive || ReducedMotion || Visibility != Visibility.Visible || !uiSettings.AnimationsEnabled || XamlRoot?.IsHostVisible != true)
        {
            StopMotion();
            return;
        }
        StartRendering();
    }

    private void StartRendering()
    {
        if (rendering) return;
        lastFrameTimestamp = Stopwatch.GetTimestamp();

        CompositionTarget.Rendering += OnRendering;
        rendering = true;
    }

    private void OnRendering(object? sender, object e)
    {
        if (!IsLoaded || !pageActive || ReducedMotion || Visibility != Visibility.Visible || !uiSettings.AnimationsEnabled || XamlRoot?.IsHostVisible != true)
        {
            StopMotion();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double deltaSeconds = Math.Clamp((now - lastFrameTimestamp) / (double)Stopwatch.Frequency, 0d, 1d / 15d);
        lastFrameTimestamp = now;
        smoothedCpuRpm = targetCpuRpm is null ? 0 : FanMotionMath.SmoothToward(smoothedCpuRpm, targetCpuRpm ?? 0, deltaSeconds, .55);
        smoothedGpuRpm = targetGpuRpm is null ? 0 : FanMotionMath.SmoothToward(smoothedGpuRpm, targetGpuRpm ?? 0, deltaSeconds, .55);

        CpuFan.SetRotationSpeed(FanMotionMath.MapRpmToRevolutionsPerSecond(targetCpuRpm));
        GpuFan.SetRotationSpeed(FanMotionMath.MapRpmToRevolutionsPerSecond(targetGpuRpm));

        UpdateGauges(smoothedCpuRpm, smoothedGpuRpm);
    }

    private void UpdateGauges(double cpuRpm, double gpuRpm)
    {
        foreach (var value in fanTemperatures) value.Advance(fanTextClock.Elapsed.TotalSeconds);
        CpuTemperatureText.Text = Format(fanTemperatures[0].Value, "°C");
        GpuTemperatureText.Text = Format(fanTemperatures[1].Value, "°C");
        CpuRpmText.Text = FormatRpm(targetCpuRpm.HasValue ? cpuRpm : null);
        GpuRpmText.Text = FormatRpm(targetGpuRpm.HasValue ? gpuRpm : null);
        double cpuStrength = FanMotionMath.NormalizeFanStrength(cpuRpm);
        double gpuStrength = FanMotionMath.NormalizeFanStrength(gpuRpm);
        CpuFan.SetStrength(cpuStrength);
        GpuFan.SetStrength(gpuStrength);
        CpuSpeedFill.Height = cpuStrength * 119;
        GpuSpeedFill.Height = gpuStrength * 119;

    }

    private void StopMotion()
    {
        if (rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            rendering = false;
        }
        lastFrameTimestamp = 0;
        CpuFan.StopRotation();
        GpuFan.StopRotation();
        CpuFan.SetStrength(0);
        GpuFan.SetStrength(0);

    }

    private static double? ValidRpm(double? value) => value is double rpm && double.IsFinite(rpm) && rpm >= 0 ? rpm : null;
    private static string FormatRpm(double? value) => value is double rpm && double.IsFinite(rpm) ? $"{rpm:0} RPM" : "-- RPM";
    private static string Format(double? value, string unit) => value is double actual && double.IsFinite(actual) ? $"{actual:0} {unit}" : $"-- {unit}";
}
