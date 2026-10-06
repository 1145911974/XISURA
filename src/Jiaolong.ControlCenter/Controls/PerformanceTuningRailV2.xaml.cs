using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class PerformanceTuningRailV2 : UserControl
{
    private const double ThumbDiameter = 14d;
    private bool synchronizing;
    private double? currentValue;
    private Storyboard? recommendationStoryboard;
    private TaskCompletionSource? recommendationCompletion;

    public PerformanceTuningRailV2()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => StopRecommendationAnimation();
    }

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100d;
    public double StepFrequency { get; set; } = 1d;
    public double RecommendedValue { get; set; }
    public double LimitValue { get; set; } = 100d;
    public double DisplayScale { get; set; } = 1d;
    public string Unit { get; set; } = string.Empty;
    public string MinimumLabel { get; set; } = "最低";
    public string MaximumLabel { get; set; } = "极限";
    public bool ShowCurrentLabel { get; set; } = true;

    public double? Value => currentValue;

    public event EventHandler<double>? ValueChanged;

    public void SetValue(double? value)
    {
        currentValue = value is double actual && double.IsFinite(actual)
            ? actual
            : null;
        synchronizing = true;
        InputSlider.IsEnabled = currentValue.HasValue;
        if (currentValue is double next)
            InputSlider.Value = Math.Clamp(next, Minimum, Maximum);
        synchronizing = false;
        UpdateVisuals();
    }

    public void SetRecommendedValue(double value)
    {
        StopRecommendationAnimation();
        RecommendedValue = Math.Clamp(value, Minimum, Maximum);
        UpdateLabels();
        UpdateVisuals();
    }

    public Task SetRecommendedValueAnimatedAsync(double value)
    {
        double width = RailCanvas.ActualWidth;
        double oldX = Project(RecommendedValue, width) +
            (RecommendedMarker.RenderTransform is TranslateTransform current ? current.X : 0d);
        SetRecommendedValue(value);
        var duration = Application.Current.Resources.TryGetValue("ControlStateTransitionDuration", out object resource) &&
            resource is Duration motionDuration ? motionDuration.TimeSpan : TimeSpan.FromMilliseconds(180);
        if (!IsLoaded || width <= 0d || duration <= TimeSpan.Zero || !new UISettings().AnimationsEnabled)
            return Task.CompletedTask;

        double newX = Project(RecommendedValue, width);
        var transform = new TranslateTransform { X = oldX - newX };
        RecommendedMarker.RenderTransform = transform;
        var completion = new TaskCompletionSource();
        recommendationCompletion = completion;
        var animation = new DoubleAnimation
        {
            From = oldX - newX,
            To = 0d,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(recommendationStoryboard, storyboard)) return;
            StopRecommendationAnimation();
        };
        recommendationStoryboard = storyboard;
        storyboard.Begin();
        return completion.Task;
    }

    private void StopRecommendationAnimation()
    {
        recommendationStoryboard?.Stop();
        recommendationStoryboard = null;
        RecommendedMarker.RenderTransform = null;
        recommendationCompletion?.TrySetResult();
        recommendationCompletion = null;
    }

    public void Configure()
    {
        synchronizing = true;
        InputSlider.Minimum = Minimum;
        InputSlider.Maximum = Maximum;
        InputSlider.StepFrequency = StepFrequency;
        synchronizing = false;
        UpdateLabels();
        SetValue(currentValue);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => Configure();

    private void OnInputValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (synchronizing || !IsLoaded || InputSlider.FocusState == FocusState.Unfocused)
            return;

        currentValue = e.NewValue;
        UpdateVisuals();
        ValueChanged?.Invoke(this, e.NewValue);
    }

    private void OnInputSizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisuals();

    private void UpdateLabels()
    {
        RecommendedText.Text = string.Empty;
        MaximumText.Text = Format(Maximum);
        MinimumText.Text = Format(Minimum);
    }

    private void UpdateVisuals()
    {
        double width = RailCanvas.ActualWidth;
        if (width <= 0d)
            return;

        bool hasRecommendation = double.IsFinite(RecommendedValue);
        RecommendedMarker.Visibility = hasRecommendation ? Visibility.Visible : Visibility.Collapsed;
        if (hasRecommendation) Canvas.SetLeft(RecommendedMarker, Project(RecommendedValue, width) - 1d);
        double labelCanvasWidth = TopLabels.ActualWidth > 0d ? TopLabels.ActualWidth : width + 32d;

        bool available = currentValue.HasValue;
        CurrentThumb.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        CurrentCore.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        CurrentText.Visibility = ShowCurrentLabel ? Visibility.Visible : Visibility.Collapsed;
        CurrentText.Text = available ? Format(currentValue!.Value) : string.Empty;
        if (!available)
        {
            Canvas.SetLeft(CurrentText, 16d);
            ActiveFill.Width = 0d;
            return;
        }

        double currentX = Project(currentValue!.Value, width);
        ActiveFill.Width = Math.Max(0d, currentX);
        Canvas.SetLeft(CurrentThumb, currentX - ThumbDiameter / 2d);
        Canvas.SetLeft(CurrentCore, currentX - 2d);
        CurrentText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredCurrentLeft = Math.Clamp(
            currentX + 16d - CurrentText.DesiredSize.Width / 2d,
            0d,
            Math.Max(0d, labelCanvasWidth - CurrentText.DesiredSize.Width));
        Canvas.SetLeft(CurrentText, desiredCurrentLeft);
    }

    private double Project(double value, double width)
    {
        double radius = ThumbDiameter / 2d;
        double progress = Maximum <= Minimum ? 0d : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0d, 1d);
        return radius + progress * Math.Max(0d, width - ThumbDiameter);
    }

    private static double PositionClamped(FrameworkElement element, double center, double canvasWidth)
    {
        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = Math.Clamp(
            center - element.DesiredSize.Width / 2d,
            0d,
            Math.Max(0d, canvasWidth - element.DesiredSize.Width));
        Canvas.SetLeft(element, left);
        return left;
    }

    private string Format(double value) =>
        PerformanceDisplayValue.ToDisplay(value, DisplayScale).ToString("0.#");
}

internal static class PerformanceRailLabelGeometry
{
    internal static double ResolveLimitLeft(
        double desiredLeft,
        double limitWidth,
        double currentLeft,
        double currentWidth,
        double gap,
        double canvasWidth)
    {
        double maximumLeft = Math.Max(0d, canvasWidth - limitWidth);
        double collisionFreeLeft = currentLeft + currentWidth + gap;
        return Math.Clamp(Math.Max(desiredLeft, collisionFreeLeft), 0d, maximumLeft);
    }

    internal static double ResolveCurrentLeft(
        double desiredCenter,
        double currentWidth,
        double limitLeft,
        double gap,
        double canvasWidth)
    {
        double desiredLeft = Math.Clamp(
            desiredCenter - currentWidth / 2d,
            0d,
            Math.Max(0d, canvasWidth - currentWidth));
        return Math.Max(0d, Math.Min(desiredLeft, limitLeft - gap - currentWidth));
    }
}
