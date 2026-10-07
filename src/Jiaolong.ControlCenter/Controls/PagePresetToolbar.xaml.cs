using Jiaolong.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class PagePresetToolbar : UserControl
{
    private bool followPreset = true;
    private bool requestedSaveEnabled = true;
    private bool requestedUseEnabled;
    private int statusVersion;
    private bool statusVisible;
    private string statusDetail = string.Empty;
    private Storyboard? transition;
    private TaskCompletionSource? transitionCompletion;
    private Storyboard? managementTransition;
    private const double EditingActionsWidth = 264;

    public PagePresetToolbar()
    {
        InitializeComponent();
        PresetPicker.SetManagementMode(false);
        PresetPicker.PresetInvoked += (_, key) =>
        {
            if (!IsEditingPreset) PresetUseRequested?.Invoke(this, key);
        };
        ApplyActionAvailability();
        EditingActionsHost.SizeChanged += (_, _) => EditingActionsClip.Rect = new(0, 0, EditingActionsHost.ActualWidth, 52);
        Unloaded += (_, _) =>
        {
            ++statusVersion;
            StopTransition();
            StopManagementTransition();
            SettleManagementLayout(IsEditingPreset);
            statusVisible = false;
            PresetStatusHost.IsHitTestVisible = false;
            PresetStatusHost.Opacity = 0;
            PresetStatusHost.Visibility = Visibility.Visible;
            PresetPickerTranslation.X = 162;
        };
    }

    internal Microsoft.UI.Xaml.Media.TranslateTransform ModeSelectorTranslation => PresetPickerTranslation;
    public PresetKey SelectedKey { get => PresetPicker.SelectedKey; set => PresetPicker.SelectedKey = value; }
    public string SelectedDisplayName => PresetPicker.SelectedDisplayName;
    public string DisplayNameFor(PresetKey key) => PresetPicker.DisplayNameFor(key);
    public string StatusDetail => statusDetail;
    public event EventHandler<PresetKey>? SelectedKeyChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? UseRequested;
    public event EventHandler<bool>? FollowPresetChanged;
    public event EventHandler<bool>? EditingModeChanged;
    public event EventHandler<PresetKey>? PresetUseRequested;
    public event EventHandler<PresetKey>? SaveAsRequested;
    public bool IsEditingPreset { get; private set; }
    public PresetKey? CurrentSourceKey { get; private set; }
    private bool currentModified;
    private PresetKey? submittedPreset;

    public void EnterPresetManagement() => SetManagementMode(true);
    public void ExitPresetManagement() => SetManagementMode(false);
    private void OnManageClick(object sender, RoutedEventArgs e)
    {
        SetManagementMode(!IsEditingPreset);
        if (IsEditingPreset) ShowPicker();
    }
    private void SetManagementMode(bool editing)
    {
        if (IsEditingPreset == editing) return;
        IsEditingPreset = editing;
        PresetPicker.HidePicker();
        PresetPicker.SetManagementMode(editing);
        ApplyActionAvailability();
        UpdateContext();
        TransitionManagementLayout(editing);
        EditingModeChanged?.Invoke(this, editing);
    }
    public void SetCurrentPreset(PresetKey? key, bool modified = false)
    {
        CurrentSourceKey = key;
        currentModified = modified;
        PresetPicker.SetConfirmedActivePreset(modified ? null : key);
        if (key.HasValue && !modified) submittedPreset = null;
        UpdateContext();
        ApplyActionAvailability();
    }
    public void SetCurrentSettingsModified()
    {
        currentModified = true;
        submittedPreset = null;
        PresetPicker.SetConfirmedActivePreset(null);
        UpdateContext();
    }
    public void SetSubmittedPreset(PresetKey key)
    {
        submittedPreset = key;
        UpdateContext();
    }
    private void UpdateContext()
    {
        if (ContextText is null) return;
        string current = CurrentSourceKey is { } key
            ? $"当前使用：{PresetPicker.DisplayNameFor(key)}{(currentModified ? " · 已调整" : string.Empty)}"
            : "当前电脑 · 手动设置";
        if (submittedPreset is { } submitted) current += $"　｜　已提交：{PresetPicker.DisplayNameFor(submitted)} · 部分设置未读回";
        if (ContextText.Text == current) return;
        ContextText.Text = current;
        ToolTipService.SetToolTip(ContextText, ContextText.Text);
    }

    public void ConfigureFollowPreset(bool enabled)
    {
        FollowPresetButton.Visibility = Visibility.Visible;
        SetFollowPreset(enabled);
    }

    public void SetFollowPreset(bool enabled)
    {
        followPreset = enabled;
        FollowPresetButton.IsChecked = enabled;
        ApplyActionAvailability();
    }

    private void OnFollowPresetClick(object sender, RoutedEventArgs e)
    {
        SetFollowPreset(FollowPresetButton.IsChecked == true);
        FollowPresetChanged?.Invoke(this, followPreset);
    }

    public void SetEditingState(PresetKey key, bool dirty, bool saved) { PresetPicker.SetEditingState(key, dirty, saved); UpdateContext(); }
    public void SetActivePreset(PresetKey key) { PresetPicker.SetActivePreset(key); SetCurrentPreset(key); }
    public void SetConfirmedActivePreset(PresetKey? key)
    {
        PresetPicker.SetConfirmedActivePreset(key);
        if (key.HasValue) SetCurrentPreset(key);
        else if (CurrentSourceKey.HasValue) { currentModified = true; UpdateContext(); }
    }
    public void EnablePresetReset(EventHandler<PresetKey> handler) => PresetPicker.EnableReset(handler);
    public void SetSlotSummary(int slot, string summary) => PresetPicker.SetSlotSummary(slot, summary);
    public void ShowPicker() => PresetPicker.ShowPicker();
    public void SetSlotSummary(string summary) => PresetPicker.SetSlotSummary(summary);
    public void SetActionAvailability(bool saveEnabled, bool useEnabled)
    {
        requestedSaveEnabled = saveEnabled;
        requestedUseEnabled = useEnabled;
        ApplyActionAvailability();
    }

    private void ApplyActionAvailability()
    {
        PresetPicker.IsEnabled = requestedSaveEnabled || requestedUseEnabled;
        ManageButton.IsEnabled = IsEditingPreset && (requestedSaveEnabled || requestedUseEnabled);
        ManagePresetMenuItem.IsEnabled = requestedSaveEnabled || requestedUseEnabled;
        ManagePresetMenuItem.Visibility = IsEditingPreset ? Visibility.Collapsed : Visibility.Visible;
        SaveCurrentMenuItem.Visibility = IsEditingPreset ? Visibility.Collapsed : Visibility.Visible;
        SaveCurrentMenuItem.IsEnabled = requestedSaveEnabled && CurrentSourceKey.HasValue;
        SaveButton.IsTabStop = ManageButton.IsTabStop = IsEditingPreset;
        ToolTipService.SetToolTip(SaveButton, IsEditingPreset ? "保存编辑草稿，不改变电脑" : "将当前设置保存到来源预设");
        ManageButton.Content = "返回当前电脑";
        UseButton.Text = IsEditingPreset ? "保存并应用" : "应用调整";
        UseButton.Visibility = IsEditingPreset || requestedUseEnabled ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = requestedSaveEnabled && IsEditingPreset;
        SaveAsButton.IsEnabled = requestedSaveEnabled;
        UseButton.IsEnabled = requestedUseEnabled;
        PresetPicker.SetResetEnabled(IsEditingPreset && requestedSaveEnabled);
    }

    private void StopManagementTransition()
    {
        if (managementTransition is null) return;
        // Preserve the displayed frame when a transition is reversed or interrupted.
        EditingActionsHost.Width = EditingActionsHost.Width;
        EditingActionsHost.Opacity = EditingActionsHost.Opacity;
        managementTransition.Stop();
        managementTransition = null;
    }

    private void SettleManagementLayout(bool editing)
    {
        EditingActionsHost.Width = editing ? EditingActionsWidth : 0;
        EditingActionsHost.Opacity = editing ? 1 : 0;
        EditingActionsHost.IsHitTestVisible = editing;
    }

    private void TransitionManagementLayout(bool editing)
    {
        StopManagementTransition();
        EditingActionsHost.IsHitTestVisible = editing;
        if (!IsLoaded || !new UISettings().AnimationsEnabled)
        {
            SettleManagementLayout(editing);
            return;
        }
        var storyboard = new Storyboard();
        AddAnimation(storyboard, EditingActionsHost, "Width", EditingActionsHost.Width, editing ? EditingActionsWidth : 0);
        AddAnimation(storyboard, EditingActionsHost, "Opacity", EditingActionsHost.Opacity, editing ? 1 : 0);
        if (storyboard.Children.Count == 0) { SettleManagementLayout(editing); return; }
        managementTransition = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(managementTransition, storyboard)) return;
            SettleManagementLayout(editing);
            storyboard.Stop();
            managementTransition = null;
        };
        storyboard.Begin();
    }
    public Task SetDirtyStatusAsync(bool dirty) => dirty ? ShowStatusAsync("有未保存的更改") : HideStatusAsync();
    public Task ShowSavedStatusAsync() => ShowStatusAsync("已保存", autoHide: true);
    public Task ShowStatusAsync(string text) => IsFailureFeedback(text)
        ? ShowFailureDialogAsync(text) : ShowStatusAsync(text, autoHide: !text.StartsWith("正在", StringComparison.Ordinal));
    public Task ShowTransientStatusAsync(string text) => ShowStatusAsync(text, autoHide: true);

    private static bool IsFailureFeedback(string text) =>
        new[] { "失败", "不可用", "未应用", "未提交", "未发送", "未完成", "未确认", "不支持", "无效", "请求中断", "结果未知", "无法", "超出", "缺少", "不一致" }
            .Any(text.Contains) && !text.StartsWith("已完成", StringComparison.Ordinal);

    private bool failureDialogOpen;
    private async Task ShowFailureDialogAsync(string text)
    {
        await HideStatusAsync();
        if (failureDialogOpen || XamlRoot?.IsHostVisible != true) return;
        failureDialogOpen = true;
        try
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "操作未完成", Content = text, CloseButtonText = "知道了" }.ShowAsync();
        }
        catch (Exception error) { Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"Preset operation dialog: {error.Message}\n"); }
        finally { failureDialogOpen = false; }
    }

    private void OnSelectedKeyChanged(object? sender, PresetKey key) { UpdateContext(); SelectedKeyChanged?.Invoke(this, key); }
    private void OnSaveClick(object sender, RoutedEventArgs e) { if (requestedSaveEnabled) SaveRequested?.Invoke(this, EventArgs.Empty); }
    private void OnUseClick(object sender, RoutedEventArgs e) { if (requestedUseEnabled) UseRequested?.Invoke(this, EventArgs.Empty); }
    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (!requestedSaveEnabled) return;
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var key in PresetKey.All)
            picker.Items.Add(new ComboBoxItem { Content = PresetPicker.DisplayNameFor(key), Tag = key });
        picker.SelectedIndex = PresetKey.All.ToList().IndexOf(SelectedKey);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "另存为预设", PrimaryButtonText = "保存", CloseButtonText = "取消",
            Content = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "选择保存位置。已有配置会被替换，电脑当前设置保持不变。", TextWrapping = TextWrapping.Wrap }, picker } }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && picker.SelectedItem is ComboBoxItem { Tag: PresetKey chosenKey })
            SaveAsRequested?.Invoke(this, chosenKey);
    }

    private async Task ShowStatusAsync(string text, bool autoHide)
    {
        if (statusVisible && statusDetail == text && !autoHide) return;
        int version = ++statusVersion;
        StopTransition();
        if (statusVisible && PresetStatusHost.Opacity > .01 && statusDetail != text)
        {
            // Keep the occupied space and some visibility while replacing the status label.
            await TransitionAsync(.25, PresetPickerTranslation.X);
            if (version != statusVersion) return;
        }
        statusDetail = text;
        int separator = text.IndexOfAny(['。', '；', '\n']);
        string summary = separator > 0 ? text[..separator] : text;
        PresetStatusText.Text = summary.Length > 9 ? summary[..8] + "…" : summary;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PresetStatusHost, text);
        ToolTipService.SetToolTip(PresetStatusHost, text);
        PresetStatusHost.Visibility = Visibility.Visible;
        PresetStatusHost.IsHitTestVisible = true;
        statusVisible = true;
        await TransitionAsync(1, 0);

        if (autoHide && version == statusVersion) _ = HideStatusAfterDelayAsync(version);
    }

    private async Task HideStatusAfterDelayAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (version == statusVersion) await HideStatusAsync(version);
    }

    private Task HideStatusAsync() => HideStatusAsync(++statusVersion);

    private async Task HideStatusAsync(int version)
    {
        if (version != statusVersion) return;
        StopTransition();
        statusVisible = false;
        PresetStatusHost.IsHitTestVisible = false;
        await TransitionAsync(0, 162);
        if (version == statusVersion) PresetStatusHost.Visibility = Visibility.Visible;
    }

    private void StopTransition()
    {
        if (transition is null) return;
        // Commit the displayed values before removing animation precedence.
        PresetStatusHost.Opacity = PresetStatusHost.Opacity;
        PresetPickerTranslation.X = PresetPickerTranslation.X;
        transition.Stop();
        transition = null;
        transitionCompletion?.TrySetResult();
        transitionCompletion = null;
    }

    private Task TransitionAsync(double opacity, double pickerX)
    {
        StopTransition();
        if (!IsLoaded || !new UISettings().AnimationsEnabled)
        {
            PresetStatusHost.Opacity = opacity;
            PresetPickerTranslation.X = pickerX;
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storyboard = new Storyboard();
        AddAnimation(storyboard, PresetStatusHost, "Opacity", PresetStatusHost.Opacity, opacity);
        AddAnimation(storyboard, PresetPickerTranslation, "X", PresetPickerTranslation.X, pickerX);
        if (storyboard.Children.Count == 0) return Task.CompletedTask;
        transition = storyboard;
        transitionCompletion = completion;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(transition, storyboard)) return;
            PresetStatusHost.Opacity = opacity;
            PresetPickerTranslation.X = pickerX;
            storyboard.Stop();
            transition = null;
            transitionCompletion = null;
            completion.TrySetResult();
        };
        storyboard.Begin();
        return completion.Task;
    }

    private static void AddAnimation(Storyboard storyboard, DependencyObject target, string property, double from, double to)
    {
        if (Math.Abs(from - to) < .001) return;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(240),
            FillBehavior = FillBehavior.HoldEnd,
            EnableDependentAnimation = property == "Width",
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
