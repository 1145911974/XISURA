using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class PerformanceParameterRow : UserControl
{
    private const double ThumbDiameter = 20d;
    private bool ready;
    private bool synchronizing;
    private double value;

    public PerformanceParameterRow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object? IconContent { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100d;
    public double StepFrequency { get; set; } = 1d;
    public double RecommendedValue { get; set; }
    public double ControlLimitValue { get; set; } = 100d;
    public string Unit { get; set; } = string.Empty;
    public double TrailingWidth { get; set; } = 54d;
    public object? AccessoryContent { get; set; }
    public string MinimumLabel { get; set; } = "最低";
    public string MaximumLabel { get; set; } = "极限";
    public string HelpEffect { get; set; } = string.Empty;
    public string HelpRecommendedRange { get; set; } = string.Empty;
    public string HelpSafeRange { get; set; } = string.Empty;
    public string HelpExtremeLimit { get; set; } = string.Empty;
    public string HelpRisk { get; set; } = string.Empty;

    public double Value
    {
        get => value;
        set
        {
            double next = Clamp(value);
            if (Math.Abs(this.value - next) < double.Epsilon)
                return;

            this.value = next;
            if (ready)
                SynchronizeControls(next, raiseChanged: false);
        }
    }

    public event EventHandler<double>? ValueChanged;

    public void SetRecommendedValue(double recommendedValue)
    {
        RecommendedValue = recommendedValue;
        if (ready)
        {
            RecommendationValueText.Text = $"推荐 {Format(recommendedValue)} {Unit}".TrimEnd();
            UpdateScaleVisuals();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleText.Text = Title;
        DescriptionText.Text = Description;
        IconPresenter.Content = IconContent;
        UnitText.Text = Unit;
        TrailingColumn.Width = new GridLength(TrailingWidth);
        AccessoryPresenter.Content = AccessoryContent;
        MinimumValueText.Text = $"{MinimumLabel} {Format(Minimum)} {Unit}".TrimEnd();
        MaximumValueText.Text = $"{MaximumLabel} {Format(Maximum)} {Unit}".TrimEnd();
        RecommendationValueText.Text = $"推荐 {Format(RecommendedValue)} {Unit}".TrimEnd();
        HelpButton.ParameterName = Title;
        HelpButton.Help = new ParameterHelpContent(
            HelpEffect,
            $"{Format(value)} {Unit}".TrimEnd(),
            HelpRecommendedRange,
            HelpSafeRange,
            HelpExtremeLimit,
            HelpRisk);

        synchronizing = true;
        ValueSlider.Minimum = Minimum;
        ValueSlider.Maximum = Maximum;
        ValueSlider.StepFrequency = StepFrequency;
        ValueBox.Minimum = Minimum;
        ValueBox.Maximum = Maximum;
        ValueBox.SmallChange = StepFrequency;
        synchronizing = false;
        ready = true;
        SynchronizeControls(Clamp(value), raiseChanged: false);
    }

    private void OnSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!ready || synchronizing)
            return;

        SynchronizeControls(e.NewValue, raiseChanged: true);
    }

    private void OnNumberBoxValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!ready || synchronizing || !double.IsFinite(args.NewValue))
            return;

        SynchronizeControls(args.NewValue, raiseChanged: true);
    }

    private void OnNumberBoxPointerWheelChanged(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void SynchronizeControls(double next, bool raiseChanged)
    {
        next = Clamp(next);
        synchronizing = true;
        value = next;
        ValueSlider.Value = next;
        ValueBox.Value = next;
        CurrentValueText.Text = Format(next);
        HelpButton.Help = HelpButton.Help with { CurrentValue = $"{Format(next)} {Unit}".TrimEnd() };
        synchronizing = false;
        UpdateScaleVisuals();

        if (raiseChanged)
            ValueChanged?.Invoke(this, next);
    }

    private void OnScaleOverlaySizeChanged(object sender, SizeChangedEventArgs e) => UpdateScaleVisuals();

    private void UpdateScaleVisuals()
    {
        double width = ScaleOverlay.ActualWidth;
        if (width <= 0d)
            return;

        Position(CurrentValueBadge, PerformanceScaleGeometry.Project(value, Minimum, Maximum, width, ThumbDiameter));
        Position(CurrentValueGlow, PerformanceScaleGeometry.Project(value, Minimum, Maximum, width, ThumbDiameter));
        Position(RecommendationMarker, PerformanceScaleGeometry.Project(RecommendedValue, Minimum, Maximum, width, ThumbDiameter));
        double controlLimitX = PerformanceScaleGeometry.Project(ControlLimitValue, Minimum, Maximum, width, ThumbDiameter);
        Position(ControlLimitMarker, controlLimitX);
        Position(ControlLimitText, controlLimitX);
        ControlLimitText.Text = Format(ControlLimitValue);
    }

    private static void Position(FrameworkElement element, double center)
    {
        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(element, center - element.DesiredSize.Width / 2d);
    }

    private double Clamp(double candidate) =>
        double.IsFinite(candidate) ? Math.Clamp(candidate, Minimum, Maximum) : Minimum;

    private static string Format(double number) => number.ToString("0.#");
}

internal static class PerformanceScaleGeometry
{
    internal static double Project(double value, double minimum, double maximum, double railWidth, double thumbDiameter)
    {
        if (!double.IsFinite(railWidth) || railWidth <= 0d)
            return 0d;

        double radius = Math.Clamp(thumbDiameter / 2d, 0d, railWidth / 2d);
        if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return radius;

        double progress = Math.Clamp((value - minimum) / (maximum - minimum), 0d, 1d);
        return radius + progress * (railWidth - 2d * radius);
    }
}
