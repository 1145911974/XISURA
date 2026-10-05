using System.Diagnostics;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class TelemetryCurve : UserControl
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly UISettings settings = new();
    private TelemetryCurveSeries series = new(60);
    private double windowSeconds = 60;
    private bool subscribed;
    private bool reducedMotion;
    public double? DisplayedValue { get; private set; }
    public double HeadOpacity => HeadPoint.Opacity;
    public event EventHandler? FrameRendered;

    public TelemetryCurve()
    {
        InitializeComponent();
        Loaded += (_, _) => { Draw(); StartMotion(); };
        Unloaded += (_, _) => StopMotion();
        SizeChanged += (_, _) => Draw();
    }

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(TelemetryCurve), new PropertyMetadata(null));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(TelemetryCurve), new PropertyMetadata(null));
    public static readonly DependencyProperty AreaBrushProperty = DependencyProperty.Register(
        nameof(AreaBrush), typeof(Brush), typeof(TelemetryCurve), new PropertyMetadata(null));
    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush? AreaBrush { get => (Brush?)GetValue(AreaBrushProperty); set => SetValue(AreaBrushProperty, value); }
    public double MinimumValue { get; set; }
    public double MaximumValue { get; set; } = 100;
    public double WindowSeconds
    {
        get => windowSeconds;
        set
        {
            if (value <= 0 || !double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == windowSeconds) return;
            windowSeconds = value;
            series = new(value);
        }
    }
    public bool ReducedMotion
    {
        get => reducedMotion;
        set { reducedMotion = value; Draw(); if (value) StopMotion(); else StartMotion(); }
    }

    public void UpdateSample(double? value, DateTimeOffset? observedAtUtc = null)
    {
        if (!series.UpdateSample(value, clock.Elapsed.TotalSeconds, CanAnimate, observedAtUtc)) return;
        Draw();
        StartMotion();
    }

    private bool CanAnimate => !reducedMotion && settings.AnimationsEnabled;
    private bool Visible()
    {
        if (!IsLoaded || XamlRoot?.IsHostVisible != true) return false;
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private void StartMotion()
    {
        if (subscribed || !CanAnimate || !Visible()) return;
        CompositionTarget.Rendering += OnRendering;
        subscribed = true;
    }

    private void StopMotion()
    {
        if (!subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        subscribed = false;
    }

    private void OnRendering(object? sender, object args)
    {
        if (!Visible()) { StopMotion(); return; }
        if (!CanAnimate) { Draw(); StopMotion(); return; }
        Draw();
    }

    private void Draw()
    {
        if (Viewport is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double now = clock.Elapsed.TotalSeconds;
        var frame = series.GetFrame(now, CanAnimate);
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
        var line = new PathGeometry();
        var area = new PathGeometry();
        PathFigure? figure = null;
        PathFigure? fill = null;
        Point previous = default;
        foreach (var sample in frame.Samples)
        {
            var point = new Point(TelemetryCurveWindow.Position(sample.Seconds, now, windowSeconds) * ActualWidth, Y(sample.Value));
            if (figure is null || sample.StartsSegment)
            {
                CloseArea(fill, previous);
                figure = new PathFigure { StartPoint = point };
                fill = new PathFigure { StartPoint = new Point(point.X, ActualHeight), IsClosed = true };
                fill.Segments.Add(new LineSegment { Point = point });
                line.Figures.Add(figure);
                area.Figures.Add(fill);
            }
            else
            {
                AddCurve(figure, previous, point);
                AddCurve(fill!, previous, point);
            }
            previous = point;
        }
        CloseArea(fill, previous);
        CurveLine.Data = line;
        CurveArea.Data = area;
        HeadPoint.Opacity = frame.HeadOpacity;
        HeadHalo.Opacity = frame.HeadOpacity * .22;
        if (frame.HeadValue is double value && frame.HeadSeconds is double seconds)
        {
            double x = TelemetryCurveWindow.Position(seconds, now, windowSeconds) * ActualWidth;
            double y = Y(value);
            Canvas.SetLeft(HeadPoint, x - HeadPoint.Width / 2);
            Canvas.SetTop(HeadPoint, y - HeadPoint.Height / 2);
            Canvas.SetLeft(HeadHalo, x - HeadHalo.Width / 2);
            Canvas.SetTop(HeadHalo, y - HeadHalo.Height / 2);
        }
        DisplayedValue = frame.HeadOpacity > 0 ? frame.HeadValue : null;
        FrameRendered?.Invoke(this, EventArgs.Empty);
        if (frame.Samples.Count == 0) StopMotion();
    }

    private double Y(double value) => ActualHeight * (1 - Math.Clamp(
        (value - MinimumValue) / Math.Max(1e-6, MaximumValue - MinimumValue), 0, 1));

    private void CloseArea(PathFigure? fill, Point end)
    {
        if (fill is not null) fill.Segments.Add(new LineSegment { Point = new Point(end.X, ActualHeight) });
    }

    private static void AddCurve(PathFigure figure, Point from, Point to)
    {
        double midpoint = (from.X + to.X) / 2;
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(midpoint, from.Y), Point2 = new Point(midpoint, to.Y), Point3 = to
        });
    }
}
