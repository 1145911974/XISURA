using System.Globalization;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class PerformanceParameterRowV2 : UserControl
{
    private bool synchronizing;
    private bool configured;
    private bool hasPendingValue;
    private double? pendingValue;
    private bool editorEnabled = true;
    private string? observedHelpCurrentValue;
    private DispatcherQueueTimer? valueAnimationTimer;
    private TaskCompletionSource? valueAnimationCompletion;
    private double? valueAnimationTarget;
    private Storyboard? editorAvailabilityStoryboard;
    private bool? editorVisualEnabled;

    public PerformanceParameterRowV2()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object? IconContent { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100d;
    public double StepFrequency { get; set; } = 1d;
    public double RecommendedValue { get; set; }
    public double LimitValue { get; set; } = 100d;
    public double DisplayScale { get; set; } = 1d;
    public string Unit { get; set; } = string.Empty;
    public string MinimumLabel { get; set; } = "最低";
    public string MaximumLabel { get; set; } = "极限";
    public double TrailingWidth { get; set; } = 52d;
    public object? AccessoryContent { get; set; }
    public string HelpEffect { get; set; } = string.Empty;
    public string HelpRecommendedRange { get; set; } = string.Empty;
    public string HelpSafeRange { get; set; } = string.Empty;
    public string HelpExtremeLimit { get; set; } = string.Empty;
    public string HelpRisk { get; set; } = string.Empty;

    public double? Value => valueAnimationTarget ?? (hasPendingValue ? pendingValue : Rail.Value);

    public void SetEditorEnabled(bool enabled)
    {
        editorEnabled = enabled;
        Rail.IsEnabled = enabled;
        UpdateEditorAvailability();
    }

    public void SetDescription(string description)
    {
        Description = description;
        DescriptionText.Text = description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetHelpCurrentValue(string current)
    {
        observedHelpCurrentValue = current;
        HelpButton.Help = HelpButton.Help with { CurrentValue = current };
    }

    public event EventHandler<double>? ValueChanged;

    public void SetValue(double? value)
    {
        StopValueAnimation();
        SetValueCore(value);
    }

    private void SetValueCore(double? value)
    {
        if (!configured)
        {
            pendingValue = value;
            hasPendingValue = true;
            return;
        }

        hasPendingValue = false;
        pendingValue = null;
        synchronizing = true;
        Rail.SetValue(value);
        string text = Rail.Value is double actual ? FormatInput(actual) : string.Empty;
        if (ValueBox.Text != text) ValueBox.Text = text;
        UpdateEditorAvailability();
        synchronizing = false;
    }

    private void UpdateEditorAvailability()
    {
        bool enabled = editorEnabled && Value.HasValue;
        ValueBox.IsEnabled = enabled;
        if (editorVisualEnabled == enabled)
            return;

        editorVisualEnabled = enabled;
        double targetOpacity = enabled ? 1d : 0.45d;
        TimeSpan duration = ControlStateDuration();
        double currentOpacity = ValueBox.Opacity;
        editorAvailabilityStoryboard?.Stop();
        editorAvailabilityStoryboard = null;
        ValueBox.Opacity = currentOpacity;
        if (!IsLoaded || !new UISettings().AnimationsEnabled || duration <= TimeSpan.Zero)
        {
            ValueBox.Opacity = targetOpacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = currentOpacity,
            To = targetOpacity,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, ValueBox);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(editorAvailabilityStoryboard, storyboard)) return;
            ValueBox.Opacity = targetOpacity;
            editorAvailabilityStoryboard = null;
        };
        editorAvailabilityStoryboard = storyboard;
        storyboard.Begin();
    }

    private static TimeSpan ControlStateDuration()
    {
        if (Application.Current.Resources.TryGetValue("ControlStateTransitionDuration", out object value) &&
            value is Duration duration)
            return duration.TimeSpan;
        return TimeSpan.FromMilliseconds(180);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        double? target = Value;
        StopValueAnimation();
        SetValueCore(target);
        editorAvailabilityStoryboard?.Stop();
        editorAvailabilityStoryboard = null;
        ValueBox.Opacity = editorVisualEnabled == false ? 0.45d : 1d;
    }

    public Task SetValueAnimatedAsync(double? value)
    {
        StopValueAnimation();
        var duration = ControlStateDuration();
        if (!configured || !IsLoaded || duration <= TimeSpan.Zero || !new UISettings().AnimationsEnabled ||
            Rail.Value is not double from || value is not double to || !double.IsFinite(to))
        {
            SetValueCore(value);
            return Task.CompletedTask;
        }

        var clock = Stopwatch.StartNew();
        to = Math.Clamp(to, Minimum, Maximum);
        valueAnimationTarget = to;
        synchronizing = true;
        string text = FormatInput(to);
        if (ValueBox.Text != text) ValueBox.Text = text;
        synchronizing = false;
        UpdateEditorAvailability();
        valueAnimationCompletion = new TaskCompletionSource();
        valueAnimationTimer = DispatcherQueue.CreateTimer();
        valueAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
        valueAnimationTimer.Tick += (sender, _) =>
        {
            if (!ReferenceEquals(sender, valueAnimationTimer)) return;
            double progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
            double eased = 1d - Math.Pow(1d - progress, 3d);
            // Only the rail interpolates; saving/applying always reads the final editor target.
            synchronizing = true;
            Rail.SetValue(from + (to - from) * eased);
            synchronizing = false;
            if (progress >= 1d)
            {
                SetValueCore(to);
                StopValueAnimation();
            }
        };
        valueAnimationTimer.Start();
        return valueAnimationCompletion.Task;
    }

    private void StopValueAnimation()
    {
        valueAnimationTimer?.Stop();
        valueAnimationTimer = null;
        valueAnimationTarget = null;
        valueAnimationCompletion?.TrySetResult();
        valueAnimationCompletion = null;
    }

    public void SetRecommendedValue(double value) => Rail.SetRecommendedValue(value);
    public Task SetRecommendedValueAnimatedAsync(double value) => Rail.SetRecommendedValueAnimatedAsync(value);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (configured)
            return;
        configured = true;
        TitleText.Text = Title;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ValueBox, Title);
        DescriptionText.Text = Description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
        IconPresenter.Content = IconContent;
        UnitText.Text = Unit;
        TrailingColumn.Width = new GridLength(TrailingWidth);
        AccessoryPresenter.Content = AccessoryContent;
        Rail.Minimum = Minimum;
        Rail.Maximum = Maximum;
        Rail.StepFrequency = StepFrequency;
        Rail.RecommendedValue = RecommendedValue;
        Rail.LimitValue = LimitValue;
        Rail.DisplayScale = DisplayScale;
        Rail.Unit = Unit;
        Rail.MinimumLabel = MinimumLabel;
        Rail.MaximumLabel = MaximumLabel;
        Rail.Configure();
        Rail.ValueChanged += OnRailValueChanged;
        HelpButton.ParameterName = Title;
        HelpButton.Help = new ParameterHelpContent(
            HelpEffect,
            "等待硬件回读",
            HelpRecommendedRange,
            HelpSafeRange,
            HelpExtremeLimit,
            HelpRisk);
        SetValue(hasPendingValue ? pendingValue : null);
        hasPendingValue = false;
        pendingValue = null;
    }

    private void OnRailValueChanged(object? sender, double value)
    {
        if (synchronizing)
            return;

        StopValueAnimation();
        synchronizing = true;
        ValueBox.Text = FormatInput(value);
        synchronizing = false;
        Publish(value);
    }

    private void OnValueBoxTextChanged(object sender, TextChangedEventArgs args)
    {
        if (synchronizing || ValueBox.FocusState == FocusState.Unfocused || !TryParseInput(ValueBox.Text, out double value))
            return;

        StopValueAnimation();
        value = Math.Clamp(PerformanceDisplayValue.ToRaw(value, DisplayScale), Minimum, Maximum);
        synchronizing = true;
        Rail.SetValue(value);
        synchronizing = false;
        Publish(value);
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs args)
    {
        double? value = TryParseInput(ValueBox.Text, out double parsed)
            ? Math.Clamp(PerformanceDisplayValue.ToRaw(parsed, DisplayScale), Minimum, Maximum)
            : Rail.Value;
        SetValue(value);
    }

    private void Publish(double value)
    {
        HelpButton.Help = HelpButton.Help with
        {
            CurrentValue = observedHelpCurrentValue is null
                ? $"编辑值 {FormatInput(value)} {Unit}".TrimEnd()
                : $"编辑值 {FormatInput(value)} {Unit}；{observedHelpCurrentValue}"
        };
        ValueChanged?.Invoke(this, value);
    }

    private static bool TryParseInput(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);

    private string FormatInput(double value) =>
        PerformanceDisplayValue.ToDisplay(value, DisplayScale).ToString("0.##", CultureInfo.CurrentCulture);
}

internal static class PerformanceDisplayValue
{
    internal static double ToDisplay(double value, double scale) => value * Normalize(scale);
    internal static double ToRaw(double value, double scale) => value / Normalize(scale);
    private static double Normalize(double scale) => double.IsFinite(scale) && scale > 0d ? scale : 1d;
}
