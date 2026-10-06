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
    private double lastDrawSeconds = double.NegativeInfinity;
    private readonly RectangleGeometry viewportClip = new();
    private readonly PathGeometry lineGeometry = new();
    private readonly PathGeometry areaGeometry = new();
    public double? DisplayedValue { get; private set; }
    public double HeadOpacity => HeadPoint.Opacity;
    public event EventHandler? FrameRendered;

    public TelemetryCurve()
    {
        InitializeComponent();
        Viewport.Clip = viewportClip;
        CurveLine.Data = lineGeometry;
        CurveArea.Data = areaGeometry;
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
        if (clock.Elapsed.TotalSeconds - lastDrawSeconds < 1d / 30d) return;
        Draw();
    }

    private void Draw()
    {
        if (Viewport is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double now = clock.Elapsed.TotalSeconds;
        lastDrawSeconds = now;
        var frame = series.GetFrame(now, CanAnimate);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (viewportClip.Rect != bounds) viewportClip.Rect = bounds;
        PathFigure? figure = null;
        PathFigure? fill = null;
        int figureCount = 0;
        int curveCount = 0;
        Point previous = default;
        foreach (var sample in frame.Samples)
        {
            var point = new Point(TelemetryCurveWindow.Position(sample.Seconds, now, windowSeconds) * ActualWidth, Y(sample.Value));
            if (figure is null || sample.StartsSegment)
            {
                FinishFigure(figure, fill, curveCount, previous);
                figure = GetFigure(lineGeometry, figureCount, point, false);
                fill = GetFigure(areaGeometry, figureCount++, new Point(point.X, ActualHeight), true);
                SetLine(fill, 0, point);
                curveCount = 0;
            }
            else
            {
                AddCurve(figure, curveCount, previous, point);
                AddCurve(fill!, curveCount + 1, previous, point);
                curveCount++;
            }
            previous = point;
        }
        FinishFigure(figure, fill, curveCount, previous);
        while (lineGeometry.Figures.Count > figureCount) lineGeometry.Figures.RemoveAt(lineGeometry.Figures.Count - 1);
        while (areaGeometry.Figures.Count > figureCount) areaGeometry.Figures.RemoveAt(areaGeometry.Figures.Count - 1);
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

    private void FinishFigure(PathFigure? figure, PathFigure? fill, int curves, Point end)
    {
        if (figure is null || fill is null) return;
        SetLine(fill, curves + 1, new Point(end.X, ActualHeight));
        TrimSegments(figure, curves);
        TrimSegments(fill, curves + 2);
    }

    private static PathFigure GetFigure(PathGeometry geometry, int index, Point start, bool closed)
    {
        if (index == geometry.Figures.Count) geometry.Figures.Add(new PathFigure { IsClosed = closed });
        var figure = geometry.Figures[index];
        figure.StartPoint = start;
        return figure;
    }

    private static T Segment<T>(PathFigure figure, int index) where T : PathSegment, new()
    {
        if (index == figure.Segments.Count) figure.Segments.Add(new T());
        else if (figure.Segments[index] is not T) figure.Segments[index] = new T();
        return (T)figure.Segments[index];
    }

    private static void TrimSegments(PathFigure figure, int count)
    {
        while (figure.Segments.Count > count) figure.Segments.RemoveAt(figure.Segments.Count - 1);
    }

    private static void SetLine(PathFigure figure, int index, Point point) => Segment<LineSegment>(figure, index).Point = point;

    private static void AddCurve(PathFigure figure, int index, Point from, Point to)
    {
        double midpoint = (from.X + to.X) / 2;
        var segment = Segment<BezierSegment>(figure, index);
        segment.Point1 = new Point(midpoint, from.Y);
        segment.Point2 = new Point(midpoint, to.Y);
        segment.Point3 = to;
    }
}
