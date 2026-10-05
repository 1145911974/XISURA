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
    private bool hardwarePreviewAvailable;
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
        if (!followPreset && pendingHardwarePreview is null && !followSettingPending && !applying && LightingDraft.FromPlan(appliedLightingPlan) is { } actual)
        { independentDraft = draft = actual; Render(); }
        hardwarePreviewAvailable = snapshot.Controls.KeyboardLightingPreviewAvailable;
        nativeCycleAvailable = snapshot.Controls.KeyboardLightingNativeCycleAvailable;
        CycleButton.IsEnabled = nativeCycleAvailable;
        if (snapshot.Controls.PerformanceMode != PerformanceMode.Custom) confirmedCustomLighting = null;
        if (followPreset && !snapshot.Controls.KeyboardLightingPreviewActive && !hardwarePreviewOwned && !lightingFollower.IsApplying &&
            lastFollowedTarget == LightingPresetPolicy.ResolveTarget(snapshot.Controls, lightingSlots, confirmedCustomLighting, confirmedPerformanceLighting) &&
            lastFollowedPlan is { } expected && !LightingPresetPolicy.SameEffect(expected, appliedLightingPlan)) InvalidateLightingFollow();
        appliedLightingSeconds = snapshot.Controls.KeyboardLightingElapsedSeconds;
        appliedLightingClock.Restart();
        HardwareStatusText.Text = followPreset && followStatus is not null ? followStatus : DescribeHardwareStatus(appliedLightingPlan, appliedLightingSeconds);
        if (snapshot.Controls.KeyboardLightingPreviewActive)
            HardwareStatusText.Text = "正在设备上预览 · 离开页面后恢复正在使用的灯效";
        PresetToolbar.SetConfirmedActivePreset(followPreset && lastFollowedTarget is { } active && lastFollowedPlan is { } plan &&
            LightingPresetPolicy.SameEffect(plan, appliedLightingPlan) ? active : null);
        lightingAvailable = snapshot.Capabilities.Items.Any(item => item.Key == "keyboardLighting" && item.State == CapabilityState.Available);
        UpdateActionAvailability();
        if (snapshot.Controls.KeyboardLightingError is { } error && error != lastEffectError)
            _ = PresetToolbar.ShowStatusAsync("灯光执行异常，请重新应用预设");
        lastEffectError = snapshot.Controls.KeyboardLightingError;
        _ = FollowCurrentPresetAsync();
    }
    private void UpdateActionAvailability() => PresetToolbar.SetActionAvailability(followPreset && !loading && !saving && !applying,
        followPreset && !loading && !saving && !applying && lightingAvailable && session is not null && saved.Contains(editing) && !dirty.Contains(editing));
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
    private double PreviewSeconds() => appliedLightingSeconds is double seconds && appliedLightingPlan == draft.ToPlan()
        ? seconds + appliedLightingClock.Elapsed.TotalSeconds
        : previewClock.Elapsed.TotalSeconds + previewTimeOffset;

    private async void OnUsePreset(object? sender, EventArgs e)
    {
        if (!followPreset || applying || saving || loading || !lightingAvailable || session is null) return;
        CommitHex();
        if (!saved.Contains(editing) || dirty.Contains(editing)) { await PresetToolbar.ShowStatusAsync("请先保存当前预设"); return; }
        var key = editing;
        int applyRevision = revision;
        applying = true;
        EditorHost.IsEnabled = false;
        PresetToolbar.IsEnabled = false;
        UpdateActionAvailability();
        try
        {
            var stored = await presets.LoadAsync(ControlPageId.Lighting, key, CancellationToken.None);
            var snapshot = stored?.SchemaVersion == 1 ? stored.Payload.Deserialize<LightingDraft>() : null;
            if (snapshot?.IsValid() != true)
            {
                saved.Remove(key);
                await PresetToolbar.ShowStatusAsync("已保存预设无效，请重新保存");
                return;
            }
            var result = await ApplySavedLightingAsync(snapshot.ToPlan() with { LogoEnabled = null });
            if (result.State == CommandState.Applied && result.Error is null)
                PresetToolbar.SetActivePreset(key);
            if (key == editing && revision == applyRevision)
                await PresetToolbar.ShowStatusAsync(result.State == CommandState.Applied && result.Error is null
                    ? "灯光已应用" : result.Error?.Code == Jiaolong.Contracts.Errors.ErrorCode.ConflictDetected
                        ? "二创控制台正在运行，请先退出后重试" : "应用失败，未确认灯光生效");
        }
        catch (Exception)
        {
            if (key == editing) await PresetToolbar.ShowStatusAsync("服务通信失败，请检查连接");
        }
        finally { applying = false; EditorHost.IsEnabled = !loading; PresetToolbar.IsEnabled = true; UpdateActionAvailability(); }
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
        InitializeHardwarePreview();
        BrightnessSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnBrightnessPointerReleased), true);
        BrightnessSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnBrightnessPointerReleased), true);
        EffectHelp.Help = new ParameterHelpContent(
            "常亮保持所选颜色；渐变连续换色，标准速度约六秒一轮；循环使用设备自动变色，色序与节拍由设备控制。",
            "循环模式的颜色和速度由设备控制，不提供独立调节。",
            "开启跟随预设，灯光随当前整机模式及槽位使用已保存配置；日常可选常亮减少视觉干扰。",
            "保持当前灯效或关闭亮度；关闭跟随后，独立设置立即生效并保持。",
            "灯效只有可选模式，没有数值极限；循环色序与节拍不可调，未保存预设仅临时预览，离页恢复原灯效。",
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
            "独立模式调节立即生效；预设编辑只临时预览，须保存并使用才保留。");
        PresetToolbar.SelectedKey = editing;
        PresetToolbar.SetSlotSummary("键盘灯效 · 颜色 · 亮度 · A 面标志");
        UpdateActionAvailability();
        PresetToolbar.SelectedKeyChanged += async (_, key) => await SelectPresetAsync(key);
        PresetToolbar.SaveRequested += OnSave;
        PresetToolbar.UseRequested += OnUsePreset;
        ColorWheel.ColorChanged += (_, color) => ChangeColor(color);
        ColorWheel.InteractionChanged += (_, active) => { colorDragging = active; UpdatePreviewMotion(); };
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
            if (!drafts.ContainsKey(editing)) await SelectPresetAsync(editing, false);
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
        if (!followPreset)
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
    private void OnBrightnessPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool previous = syncing; syncing = true;
        BrightnessSlider.Value = draft.Brightness;
        syncing = previous;
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
        if (syncing || loading) return;
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
        if (preserve && !loading) { CommitHex(); if (followPreset) drafts[editing] = draft; }
        if (followPreset) await RestoreHardwarePreviewAsync();
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
        if (version != loadVersion) return;
        drafts[key] = next;
        if (!followPreset) next = independentDraft ?? LightingDraft.FromPlan(appliedLightingPlan) ?? next;
        draft = next; Render(); loading = false; EditorHost.IsEnabled = true;
        UpdateActionAvailability();
        PresetToolbar.SetEditingState(key, dirty.Contains(key), saved.Contains(key));
        if (error is not null) await PresetToolbar.ShowStatusAsync(error);
        else await PresetToolbar.SetDirtyStatusAsync(dirty.Contains(key));
    }
    private async void OnSave(object? sender, EventArgs e)
    {
        if (!followPreset || loading || saving || applying) return;
        CommitHex();
        var key = editing; var snapshot = draft; int savedRevision = revision;
        saving = true; UpdateActionAvailability();
        try
        {
            await presets.SaveAsync(new PagePresetEnvelope(1, ControlPageId.Lighting, key, PresetToolbar.SelectedDisplayName, JsonSerializer.SerializeToElement(snapshot), DateTimeOffset.UtcNow), CancellationToken.None);
            saved.Add(key);
            if (key == lastFollowedTarget && !LightingPresetPolicy.SameEffect(snapshot.ToPlan(), lastFollowedPlan))
            { lastFollowedTarget = null; PresetToolbar.SetConfirmedActivePreset(null); }
            if (!followPreset) drafts[key] = snapshot;
            if (drafts.GetValueOrDefault(key) == snapshot) dirty.Remove(key);
            if (key == editing && savedRevision == revision)
            {
                dirty.Remove(key); PresetToolbar.SetEditingState(key, false, true); await PresetToolbar.ShowSavedStatusAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { await PresetToolbar.ShowStatusAsync("保存失败，请重试"); }
        finally { saving = false; UpdateActionAvailability(); }

    }
}
