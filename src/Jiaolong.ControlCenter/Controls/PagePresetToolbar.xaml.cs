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
    private bool requestedUseEnabled = true;
    private int statusVersion;
    private bool statusVisible;
    private string statusDetail = string.Empty;
    private Storyboard? transition;
    private TaskCompletionSource? transitionCompletion;

    public PagePresetToolbar()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            ++statusVersion;
            StopTransition();
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
    public string StatusDetail => statusDetail;
    public event EventHandler<PresetKey>? SelectedKeyChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? UseRequested;
    public event EventHandler<bool>? FollowPresetChanged;

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

    public void SetEditingState(PresetKey key, bool dirty, bool saved) => PresetPicker.SetEditingState(key, dirty, saved);
    public void SetActivePreset(PresetKey key) => PresetPicker.SetActivePreset(key);
    public void SetConfirmedActivePreset(PresetKey? key) => PresetPicker.SetConfirmedActivePreset(key);
    public void EnablePresetReset(EventHandler<PresetKey> handler) => PresetPicker.EnableReset(handler);
    public void SetSlotSummary(int slot, string summary) => PresetPicker.SetSlotSummary(slot, summary);
    public void ShowPicker() { if (followPreset) PresetPicker.ShowPicker(); }
    public void SetSlotSummary(string summary) => PresetPicker.SetSlotSummary(summary);
    public void SetActionAvailability(bool saveEnabled, bool useEnabled)
    {
        requestedSaveEnabled = saveEnabled;
        requestedUseEnabled = useEnabled;
        ApplyActionAvailability();
    }

    private void ApplyActionAvailability()
    {
        PresetPicker.IsEnabled = followPreset;
        if (!followPreset) PresetPicker.HidePicker();
        SaveButton.IsEnabled = followPreset && requestedSaveEnabled;
        UseButton.IsEnabled = followPreset && requestedUseEnabled;
        PresetPicker.SetResetEnabled(followPreset && requestedSaveEnabled);
    }
    public Task SetDirtyStatusAsync(bool dirty) => dirty ? ShowStatusAsync("有未保存的更改") : HideStatusAsync();
    public Task ShowSavedStatusAsync() => ShowStatusAsync("已保存", autoHide: true);
    public Task ShowStatusAsync(string text) => ShowStatusAsync(text, autoHide: !text.StartsWith("正在", StringComparison.Ordinal));
    public Task ShowTransientStatusAsync(string text) => ShowStatusAsync(text, autoHide: true);

    private void OnSelectedKeyChanged(object? sender, PresetKey key) => SelectedKeyChanged?.Invoke(this, key);
    private void OnSaveClick(object sender, RoutedEventArgs e) { if (followPreset && requestedSaveEnabled) SaveRequested?.Invoke(this, EventArgs.Empty); }
    private void OnUseClick(object sender, RoutedEventArgs e) { if (followPreset && requestedUseEnabled) UseRequested?.Invoke(this, EventArgs.Empty); }

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

        if (!autoHide || version != statusVersion) return;
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
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
