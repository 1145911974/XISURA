using System.Text.Json;
using Jiaolong.Contracts.Commands;
using System.Diagnostics;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Controls;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class LightingWorkspaceV2 : UserControl
{
    private readonly ControlPresetStore presets = new();
    private readonly UserPreferencesStore preferences = new();
    private readonly Dictionary<PresetKey, LightingDraft> drafts = [];
    private readonly HashSet<PresetKey> dirty = [];
    private readonly HashSet<PresetKey> saved = [];
    private PresetKey editing = PresetKey.Create(ControlModeId.Gaming, 1);
    private LightingDraft draft = LightingDraft.Default;
    private bool syncing = true, loading, saving;
    private int loadVersion, revision;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch previewClock = new();
    private readonly UISettings uiSettings = new();
    private bool pageActive, reduceMotion;
    private bool colorFocused, colorDragging;
    private LightingColor displayedColor;
    private double previewTimeOffset;
    private HomeControlSession? session;
    private bool lightingAvailable, applying;
    private bool nativeCycleAvailable;
    private bool? hardwareLogoEnabled;
    private bool logoAvailable = true;
    private string? lastEffectError;
    private KeyboardLightingPlan? appliedLightingPlan;
    private double? appliedLightingSeconds;
    private readonly Stopwatch appliedLightingClock = new();
    public void AttachSession(HomeControlSession value)
    {
        session = value;
        if (followPreset) followPoll.Start();
        value.ConfigurationRestorationCompleted += () => DispatcherQueue.TryEnqueue(() => _ = FollowCurrentPresetAsync());
    }
    public event Action<bool>? LidLogoRequested;
    public void ApplyLogoReadback(bool? enabled, bool available = true)
    {
        if (enabled.HasValue) hardwareLogoEnabled = enabled;
        logoAvailable = available;
        bool previous = syncing; syncing = true;
        LogoToggle.IsOn = hardwareLogoEnabled ?? draft.Logo;
        LogoToggle.IsEnabled = available;
        syncing = previous;
    }
    public void ApplyState(HomeStateSnapshot snapshot)
    {
        appliedLightingPlan = snapshot.Controls.KeyboardLighting;
        if (LightingDraft.FromPlan(appliedLightingPlan) is { } actual)
        {
            independentDraft = actual;
            if (!PresetToolbar.IsEditingPreset && pendingHardwarePreview is null && !colorDragging && !lightingSliderDragging && !colorFocused && !followSettingPending && !applying && !lightingFollower.IsApplying && draft != actual)
            { draft = actual; Render(); }
        }
        nativeCycleAvailable = snapshot.Controls.KeyboardLightingNativeCycleAvailable;
        CycleButton.IsEnabled = nativeCycleAvailable;
        if (snapshot.Controls.PerformanceMode != PerformanceMode.Custom) confirmedCustomLighting = null;
        appliedLightingSeconds = snapshot.Controls.KeyboardLightingElapsedSeconds;
        appliedLightingClock.Restart();
        HardwareStatusText.Text = followPreset && followStatus is not null ? followStatus : DescribeHardwareStatus(appliedLightingPlan, appliedLightingSeconds);
        if (snapshot.Controls.KeyboardLightingPreviewActive)
            HardwareStatusText.Text = "正在设备上预览 · 离开页面后恢复正在使用的灯效";
        if (lastFollowedTarget is { } active && lastFollowedPlan is { } plan)
            PresetToolbar.SetCurrentPreset(active, !LightingPresetPolicy.SameEffect(plan, appliedLightingPlan));
        lightingAvailable = snapshot.Capabilities.Items.Any(item => item.Key == "keyboardLighting" && item.State == CapabilityState.Available);
        UpdateActionAvailability();
        if (snapshot.Controls.KeyboardLightingError is { } error && error != lastEffectError)
            _ = PresetToolbar.ShowStatusAsync("灯光执行异常，请重新应用预设");
        lastEffectError = snapshot.Controls.KeyboardLightingError;
        _ = FollowCurrentPresetAsync();
    }
    private void UpdateActionAvailability()
    {
        bool ready = !loading && !saving && !applying && !followSettingPending;
        PresetToolbar.SetActionAvailability(ready, ready && PresetToolbar.IsEditingPreset && lightingAvailable && session is not null);
        EditorHost.IsEnabled = ready && (PresetToolbar.IsEditingPreset || lightingAvailable && independentDraft is not null);
    }
    internal static string DescribeHardwareStatus(KeyboardLightingPlan? plan, double? elapsedSeconds)
    {
        if (plan is null) return "设备状态：暂无法读取";
        string effect = plan.Effect switch { "Gradient" => "渐变", "Cycle" => "循环", _ => "常亮" };
        string color = plan.Red is byte red && plan.Green is byte green && plan.Blue is byte blue
            ? $"#{red:X2}{green:X2}{blue:X2}" : "颜色未返回";
        string level = plan.BrightnessLevel is int brightness ? $"{brightness} 档" : "档位未返回";
        string logo = plan.LogoEnabled is bool enabled ? (enabled ? "开" : "关") : "未返回";
        string source = elapsedSeconds is null ? "硬件读回" : "软件灯效执行状态";
        return plan.Effect == "Cycle"
            ? $"{source}：循环 · 设备自动变色 · 亮度 {level} · A 面标志 {logo}"
            : $"{source}：{effect} · {color} · 亮度 {level} · A 面标志 {logo}";
    }
    private double PreviewSeconds() => appliedLightingSeconds is double seconds && LightingPresetPolicy.SameEffect(draft.ToPlan(), appliedLightingPlan)
        ? seconds + appliedLightingClock.Elapsed.TotalSeconds
        : previewClock.Elapsed.TotalSeconds + previewTimeOffset;

    private async void OnUsePreset(object? sender, EventArgs e)
    {
        if (applying || saving || loading || !lightingAvailable || session is null) return;
        if (PresetToolbar.IsEditingPreset)
        {
            CommitHex();
            if (!await SaveDraftAsync(editing, draft)) return;
        }
        await UsePresetAsync(editing);
    }

    private async Task UsePresetAsync(PresetKey key)
    {
        if (applying || saving || loading || followSettingPending || !lightingAvailable || session is null) return;
        await RestoreHardwarePreviewAsync();
        applying = true;
        EditorHost.IsEnabled = PresetToolbar.IsEnabled = false;
        UpdateActionAvailability();
        try
        {
            // Daily selection always reads the saved plan, never another preset's dirty draft.
            var stored = await presets.LoadAsync(ControlPageId.Lighting, key, CancellationToken.None);
            var snapshot = stored is null ? LightingDraft.Default : stored.SchemaVersion == 1 ? stored.Payload.Deserialize<LightingDraft>() : null;
            if (snapshot?.IsValid() != true)
            {
                await PresetToolbar.ShowStatusAsync("已保存预设无效，请重新保存");
                return;
            }
            var plan = snapshot.ToPlan() with { LogoEnabled = null };
            var result = await ApplySavedLightingAsync(plan);
            if (result.State == CommandState.Applied && result.Error is null)
            {
                lastFollowedTarget = key; lastFollowedPlan = plan;
                followStatus = null;
                independentDraft = LightingDraft.FromPlan(plan);
                lightingSlots[key.Mode.ToString()] = key.Slot;
                preferences.Update(current => current with { LightingPresetSlots = new(lightingSlots), IndependentLighting = plan });
                lastAutomaticTarget = session?.State is { } currentState
                    ? LightingPresetPolicy.ResolveTarget(currentState.Controls, lightingSlots, confirmedCustomLighting, confirmedPerformanceLighting) : null;
                PresetToolbar.SetActivePreset(key);
                PresetToolbar.SetCurrentPreset(key);
                if (!PresetToolbar.IsEditingPreset) { draft = independentDraft!; Render(); }
                await PresetToolbar.ShowStatusAsync("灯光已应用");
            }
            else await PresetToolbar.ShowStatusAsync(result.Error?.Code == Jiaolong.Contracts.Errors.ErrorCode.ConflictDetected
                ? "二创控制台正在运行，请先退出后重试" : "应用失败，未确认灯光生效");
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting preset use: {error}\n");
            await PresetToolbar.ShowStatusAsync("灯光应用或当前设置记录未完成，请检查连接和存储");
        }
        finally { applying = false; PresetToolbar.IsEnabled = true; UpdateActionAvailability(); }
    }

    private async void OnEditingModeChanged(object? sender, bool managing)
    {
        if (managing)
        {
            await RestoreHardwarePreviewAsync();
            independentDraft = LightingDraft.FromPlan(appliedLightingPlan) ?? independentDraft;
            await SelectPresetAsync(editing, false);
        }
        else
        {
            ++loadVersion; loading = false;
            drafts[editing] = draft;
            if (independentDraft is { } current) { draft = current; Render(); }
            UpdateActionAvailability();
            await PresetToolbar.SetDirtyStatusAsync(false);
            _ = FollowCurrentPresetAsync();
        }
    }
    public void SetPageActive(bool active)
    {
        pageActive = active;
        if (!active) _ = RestoreHardwarePreviewAsync();
        UpdatePreviewMotion();
    }
    private void UpdatePreviewMotion()
    {
        bool animate = IsLoaded && pageActive && !colorFocused && !colorDragging && draft.Brightness > 0 && draft.Effect == "Gradient" && !reduceMotion && uiSettings.AnimationsEnabled && XamlRoot?.IsHostVisible == true;
        if (animate) { previewClock.Start(); previewTimer.Start(); }
        else { previewClock.Stop(); previewTimer.Stop(); }
    }
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdatePreviewMotion();
        if (!sender.IsHostVisible) _ = RestoreHardwarePreviewAsync();
    }

    public LightingWorkspaceV2()
    {
        InitializeComponent();
        FollowPresetButton.RenderTransform = PresetToolbar.ModeSelectorTranslation;
        lightingFollower = new AdaptivePresetExecutor(ApplyFollowedPresetAsync);
        InitializeLightingControl();
        foreach (var slider in new[] { BrightnessSlider, SpeedSlider })
        {
            slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnLightingSliderPressed), true);
            slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnBrightnessPointerReleased), true);
            slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnBrightnessPointerReleased), true);
        }
        EffectHelp.Help = new ParameterHelpContent(
            "常亮保持所选颜色；渐变连续换色，标准速度约六秒一轮；循环使用设备自动变色，色序与节拍由设备控制。",
            "循环模式的颜色和速度由设备控制，不提供独立调节。",
            "开启随模式应用，切换整机模式时使用对应灯光预设；日常页面直接调节当前电脑。",
            "关闭随模式应用可保持手动灯效；预设管理中编辑不改变设备。",
            "灯效没有数值极限；循环色序与节拍不可调；管理中的机身图仅用于预览。",
            "A 面标志独立同步；缺少有效预设保持当前灯效，其他控制台运行时不写入。");
        previewTimer.Tick += (_, _) =>
        {
            UpdatePreviewMotion();
            if (previewTimer.IsEnabled) KeyboardPreview.SetLight(draft.PreviewColor(PreviewSeconds()), draft.Brightness);
        };
        BrightnessHelp.Help = new ParameterHelpContent(
            "设置整把键盘的亮度；越亮越容易看清键位，也更耗电、更显眼。这里不支持按键分区亮度。",
            "0 关闭 · 1 低 · 2 中 · 3 高",
            "2 档中亮度是默认参考；昏暗环境可用 1 档，按可见性调整。",
            "0 档关闭或 1 档低亮度，减少耗电和视觉干扰；关闭不清除所选颜色。",
            "3 档是设备可选最高亮度，没有连续亮度或逐键亮度设置。",
            "日常调节直接生效；管理中的编辑须保存，保存并应用才改变电脑。");
        PresetToolbar.SelectedKey = editing;
        PresetToolbar.SetSlotSummary("键盘灯效 · 颜色 · 亮度");
        UpdateActionAvailability();
        PresetToolbar.SelectedKeyChanged += async (_, key) => { if (PresetToolbar.IsEditingPreset) await SelectPresetAsync(key); };
        PresetToolbar.PresetUseRequested += async (_, key) => await UsePresetAsync(key);
        PresetToolbar.EditingModeChanged += OnEditingModeChanged;
        PresetToolbar.SaveAsRequested += async (_, key) =>
        {
            CommitHex();
            if (PresetToolbar.IsEditingPreset)
            {
                await SaveDraftAsync(key, draft);
                return;
            }
            await RestoreHardwarePreviewAsync();
            if (independentDraft is { } current) await SaveDraftAsync(key, current);
            else await PresetToolbar.ShowStatusAsync("当前灯光尚未读回，无法另存");
        };
        PresetToolbar.SaveRequested += OnSave;
        PresetToolbar.UseRequested += OnUsePreset;
        ColorWheel.ColorChanged += (_, color) => ChangeColor(color);
        ColorWheel.InteractionChanged += (_, active) => { colorDragging = active; UpdatePreviewMotion(); if (!active && !PresetToolbar.IsEditingPreset) ScheduleHardwarePreview(); };
        ColorEditor.GotFocus += (_, _) => { colorFocused = IsColorTextFocused(); UpdatePreviewMotion(); };
        ColorEditor.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            colorFocused = IsColorTextFocused(); UpdatePreviewMotion();
        });
        var colors = new[] { "#00D7E8", "#FF244F", "#FF8A24", "#FFD92F", "#39E67B", "#386BFF" };
        for (int i = 0; i < colors.Length; i++)
        {
            LightingColor.TryParse(colors[i], out var c);
            var button = new Button { Tag = colors[i], HorizontalAlignment = HorizontalAlignment.Stretch, Height = 29, Padding = new Thickness(0), CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(Color.FromArgb(235, c.R, c.G, c.B)) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"选择颜色 {colors[i]}");
            ToolTipService.SetToolTip(button, colors[i]); Grid.SetColumn(button, i);
            button.Click += (_, _) => { LightingColor.TryParse((string)button.Tag, out var selected); ChangeColor(selected); };
            Swatches.Children.Add(button);
        }
        Render(); syncing = false;
        Loaded += async (_, _) =>
        {
            syncing = true;
            var settings = preferences.Load();
            reduceMotion = settings.ReduceMotion;
            syncing = false;
            if (XamlRoot is not null) { XamlRoot.Changed -= OnRootChanged; XamlRoot.Changed += OnRootChanged; }
            if (PresetToolbar.IsEditingPreset && !drafts.ContainsKey(editing)) await SelectPresetAsync(editing, false);
            UpdatePreviewMotion();
        };
        Unloaded += (_, _) => { previewTimer.Stop(); previewClock.Stop(); _ = RestoreHardwarePreviewAsync(); if (XamlRoot is not null) XamlRoot.Changed -= OnRootChanged; };
    }
    private bool IsColorTextFocused()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is not TextBox) return false;
        while (focused is not null && focused != ColorEditor) focused = VisualTreeHelper.GetParent(focused);
        return focused == ColorEditor;
    }
    private void Render()
    {
        bool previous = syncing; syncing = true;
        StaticButton.IsChecked = draft.Effect == "Static";
        GradientButton.IsChecked = draft.Effect == "Gradient";
        CycleButton.IsChecked = draft.Effect == "Cycle";
        BrightnessSlider.Value = draft.Brightness; LogoToggle.IsOn = hardwareLogoEnabled ?? draft.Logo; LogoToggle.IsEnabled = logoAvailable;
        SpeedSlider.Value = draft.Speed; SpeedSlider.IsEnabled = draft.Effect == "Gradient";
        SpeedValueText.Text = draft.Effect == "Cycle" ? "自动" : $"{draft.Speed:0.##}×";
        LightingColor.TryParse(draft.Hex, out var color);
        bool colorEditable = draft.Effect == "Static";
        ColorEditorHost.IsEnabled = colorEditable; ColorEditorHost.Opacity = colorEditable ? 1 : .42;
        SwatchesHost.IsEnabled = colorEditable; SwatchesHost.Opacity = colorEditable ? 1 : .42;
        PresentColor(color);
        previewClock.Reset(); previewTimeOffset = 0; UpdatePreviewMotion();
        syncing = previous;
    }
    private void PresentColor(LightingColor color)
    {
        // Editor values represent the saved origin, never the animated preview color.
        bool previous = syncing; syncing = true; displayedColor = color;
        if (HexInput.Text != color.Hex) HexInput.Text = color.Hex;
        RedInput.Value = color.R;
        GreenInput.Value = color.G;
        BlueInput.Value = color.B;
        ColorWheel.SetColor(color); KeyboardPreview.SetLight(color, draft.Brightness);
        syncing = previous;
    }
    private void Changed()
    {
        if (syncing || loading) return;
        if (!PresetToolbar.IsEditingPreset)
        {
            independentDraft = draft; revision++;
            ScheduleHardwarePreview();
            return;
        }
        revision++; drafts[editing] = draft; dirty.Add(editing);
        PresetToolbar.SetEditingState(editing, true, false);
        UpdateActionAvailability();
        _ = PresetToolbar.SetDirtyStatusAsync(true);
    }
    private void ChangeColor(LightingColor color)
    {
        if (syncing || loading || draft.Effect != "Static") return;
        if (draft.Hex == color.Hex) return;
        draft = draft with { Hex = color.Hex }; Render(); Changed();
    }
    private void OnEffectClick(object sender, RoutedEventArgs e)
    {
        if (syncing || loading) return;
        string effect = (string)((ToggleButton)sender).Tag;
        bool changed = draft.Effect != effect;
        draft = draft with { Effect = effect }; Render(); if (changed) Changed();
    }
    private void OnBrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncing || loading) return;
        int level = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        if (level == draft.Brightness) return;
        draft = draft with { Brightness = level };
        KeyboardPreview.SetLight(displayedColor, level);
        UpdatePreviewMotion();
        Changed();
    }
    private bool lightingSliderDragging;
    private void OnLightingSliderPressed(object sender, PointerRoutedEventArgs e) => lightingSliderDragging = true;
    private void OnBrightnessPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        lightingSliderDragging = false;
        bool previous = syncing; syncing = true;
        BrightnessSlider.Value = draft.Brightness;
        syncing = previous;
        if (!PresetToolbar.IsEditingPreset) ScheduleHardwarePreview();
    }
    private void OnSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncing || loading) return;
        double elapsed = previewClock.Elapsed.TotalSeconds;
        // Preserve the current color phase while changing its rate.
        previewTimeOffset = (elapsed + previewTimeOffset) * draft.Speed / e.NewValue - elapsed;
        draft = draft with { Speed = e.NewValue };
        SpeedValueText.Text = draft.Effect == "Cycle" ? "自动" : $"{draft.Speed:0.##}×";
        Changed();
    }
    private void OnLogoChanged(object sender, RoutedEventArgs e)
    {
        if (syncing) return;
        LidLogoRequested?.Invoke(LogoToggle.IsOn);
    }
    private void OnRgbChanged(object? sender, double value)
    {
        if (syncing || loading) return;
        var color = new LightingColor((byte)RedInput.Value, (byte)GreenInput.Value, (byte)BlueInput.Value);
        if (color != displayedColor) ChangeColor(color);
    }
    private void CommitHex()
    {
        if (LightingColor.TryParse(HexInput.Text, out var color)) { if (color != displayedColor) ChangeColor(color); }
        else { HexInput.Text = displayedColor.Hex; _ = PresetToolbar.ShowStatusAsync("请输入六位 HEX 颜色"); }
    }
    private void OnHexCommit(object sender, RoutedEventArgs e) { if (!syncing && !loading) CommitHex(); }
    private void OnHexKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { CommitHex(); e.Handled = true; } }
    private void OnResetClick(object sender, RoutedEventArgs e) { draft = LightingDraft.Default; Render(); Changed(); }
    private async Task SelectPresetAsync(PresetKey key, bool preserve = true)
    {
        if (!PresetToolbar.IsEditingPreset) return;
        if (preserve && !loading) { CommitHex(); drafts[editing] = draft; }
        CancelPendingHardwarePreview();
        int version = ++loadVersion; revision++; editing = key;
        loading = true; EditorHost.IsEnabled = false; PresetToolbar.SetActionAvailability(false, false);
        string? error = null;
        var next = drafts.GetValueOrDefault(key, LightingDraft.Default);
        try
        {
            if (!drafts.ContainsKey(key))
            {
                var stored = await presets.LoadAsync(ControlPageId.Lighting, key, CancellationToken.None);
                if (stored is not null)
                {
                    var candidate = stored.Payload.Deserialize<LightingDraft>();
                    if (stored.SchemaVersion != 1 || candidate?.IsValid() != true) error = "预设格式无效，显示默认值";
                    else { next = candidate; saved.Add(key); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { error = "预设读取失败，显示默认值"; }
        if (version != loadVersion || !PresetToolbar.IsEditingPreset) return;
        drafts[key] = next;
        draft = next; Render(); loading = false; EditorHost.IsEnabled = true;
        UpdateActionAvailability();
        PresetToolbar.SetEditingState(key, dirty.Contains(key), saved.Contains(key));
        if (error is not null) await PresetToolbar.ShowStatusAsync(error);
        else await PresetToolbar.SetDirtyStatusAsync(dirty.Contains(key));
    }
    private async void OnSave(object? sender, EventArgs e)
    {
        if (loading || saving || applying) return;
        if (PresetToolbar.IsEditingPreset)
        {
            CommitHex();
            await SaveDraftAsync(editing, draft);
        }
        else if (PresetToolbar.CurrentSourceKey is { } source)
        {
            CommitHex();
            await RestoreHardwarePreviewAsync();
            if (independentDraft is { } current && await SaveDraftAsync(source, current))
            {
                lastFollowedTarget = source; lastFollowedPlan = current.ToPlan() with { LogoEnabled = null };
                PresetToolbar.SetCurrentPreset(source);
            }
        }
    }

    private async Task<bool> SaveDraftAsync(PresetKey key, LightingDraft snapshot)
    {
        if (loading || saving || applying || !snapshot.IsValid()) return false;
        int savedRevision = revision;
        saving = true; UpdateActionAvailability();
        try
        {
            var stored = await presets.LoadAsync(ControlPageId.Lighting, key, CancellationToken.None);
            string name = stored?.DisplayName ?? PresetToolbar.DisplayNameFor(key);
            await presets.SaveAsync(new PagePresetEnvelope(1, ControlPageId.Lighting, key, name, JsonSerializer.SerializeToElement(snapshot), DateTimeOffset.UtcNow), CancellationToken.None);
            saved.Add(key);
            if (drafts.GetValueOrDefault(key) == snapshot || key == editing && savedRevision == revision && PresetToolbar.IsEditingPreset)
            {
                drafts[key] = snapshot; dirty.Remove(key);
                PresetToolbar.SetEditingState(key, false, true);
            }
            if (key == lastFollowedTarget && lastFollowedPlan is { } currentPlan)
            {
                lastFollowedPlan = snapshot.ToPlan() with { LogoEnabled = null };
                PresetToolbar.SetCurrentPreset(key, !LightingPresetPolicy.SameEffect(lastFollowedPlan, appliedLightingPlan));
            }
            await PresetToolbar.ShowSavedStatusAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            await PresetToolbar.ShowStatusAsync("保存失败，请重试"); return false;
        }
        finally { saving = false; UpdateActionAvailability(); }
    }
}
