using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using System.Diagnostics;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public enum FanCurveSeries { Cpu, Gpu }

public readonly record struct CurvePoint(int Temperature, int TargetPercent);

public sealed class CurvePointSelectedEventArgs(FanCurveSeries series, int index, CurvePoint point) : EventArgs
{
    public FanCurveSeries Series { get; } = series;
    public int Index { get; } = index;
    public CurvePoint Point { get; } = point;
}

public sealed class FanCurvePlot : UserControl
{
    private const float LeftInset = 54;
    private const float TopInset = 18;
    private const float RightInset = 18;
    private const float BottomInset = 36;
    private static readonly Color CpuColor = Color.FromArgb(255, 255, 52, 91);
    private static readonly Color GpuColor = Color.FromArgb(255, 0, 218, 255);
    private readonly CanvasControl surface = new() { ClearColor = Colors.Transparent };
    private readonly FanCurveDraft[] drafts = [new(0), new(1), new(2)];
    private readonly Stopwatch motionClock = Stopwatch.StartNew();
    private readonly MotionSettingsService motionSettings = new();
    private static readonly TimeSpan LayoutTransitionDuration = TimeSpan.FromMilliseconds(220);
    private CurveFrame? transitionFrom;
    private CurveFrame? transitionTo;
    private double transitionStarted;
    private bool transitionSubscribed;
    private bool reducedMotion;
    private int profile = 1;
    private bool dragging;
    public FanCurveDraft Draft => drafts[profile];
    public int PointCount => GetPoints(activeSeries).Count;
    public int Profile => profile;
    public FanCurveState Export() => new(profile, Draft.IsShared, Draft.Cpu.ToArray(), Draft.Gpu.ToArray(), Draft.Shared.ToArray());
    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            if (value)
            {
                StopLayoutTransition();
                surface.Invalidate();
            }
        }
    }

    public void Import(FanCurveState state)
    {
        if (!state.IsValid()) throw new ArgumentException("曲线预设无效");
        var previous = CaptureFrame();
        CancelPlacement();
        profile = state.Profile;
        Draft.IsShared = state.IsShared;
        Draft.Cpu.Clear(); Draft.Cpu.AddRange(state.Cpu);
        Draft.Gpu.Clear(); Draft.Gpu.AddRange(state.Gpu);
        Draft.Shared.Clear(); Draft.Shared.AddRange(state.Shared);
        StartLayoutTransition(previous);
        Refresh();
    }
    public void SelectProfile(int value)
    {
        int next = Math.Clamp(value, 0, 2);
        if (profile == next) return;
        var previous = CaptureFrame();
        CancelPlacement();
        profile = next;
        StartLayoutTransition(previous);
        Refresh();
    }
    public event EventHandler? DraftChanged;
    public void SetShared(bool value)
    {
        if (Draft.IsShared == value) return;
        var previous = CaptureFrame();
        CancelPlacement();
        Draft.IsShared = value;
        StartLayoutTransition(previous);
        Refresh();
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ResetRecommended()
    {
        StopLayoutTransition();
        CancelPlacement();
        Draft.Reset(Draft.IsShared);
        Refresh();
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }
    public bool IsAddingPoint { get; private set; }
    public void AddPoint()
    {
        IsAddingPoint = !IsAddingPoint;
        RaiseSelectionChanged();
    }
    private void CancelPlacement()
    {
        IsAddingPoint = false;
        dragging = false;
        surface.ReleasePointerCaptures();
        surface.Invalidate();
    }
    public void DeletePoint()
    {
        StopLayoutTransition();
        var points = GetPoints(activeSeries);
        if (points.Count <= 2) return;
        points.RemoveAt(selectedIndex);
        Refresh();
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Refresh()
    {
        selectedIndex = Math.Clamp(selectedIndex, 0, PointCount - 1);
        surface.Invalidate(); RaiseSelectionChanged();
    }
    private FanCurveSeries activeSeries = FanCurveSeries.Cpu;
    private int selectedIndex = 4;

    public FanCurvePlot()
    {
        Content = surface;
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => surface.Invalidate());
        Unloaded += (_, _) => StopLayoutTransition();
        surface.Draw += Draw;
        surface.PointerPressed += OnPointerPressed;
        SizeChanged += (_, _) => surface.Invalidate();
        surface.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            Point p = e.GetCurrentPoint(surface).Position;
            UpdateSelectedPoint(TemperatureAt(p.X), TargetAt(p.Y));
        };
        surface.PointerReleased += (_, e) => { dragging = false; surface.ReleasePointerCapture(e.Pointer); surface.Invalidate(); };
        surface.PointerCaptureLost += (_, _) => { dragging = false; surface.Invalidate(); };
        surface.DoubleTapped += (_, e) =>
        {
            Point p = e.GetPosition(surface);
            if (!InsidePlot(p)) return;
            IsAddingPoint = false;
            selectedIndex = FanCurveDraft.Add(GetPoints(activeSeries), TemperatureAt(p.X), TargetAt(p.Y));
            Refresh();
            DraftChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler<CurvePointSelectedEventArgs>? SelectionChanged;

    public FanCurveSeries ActiveSeries => activeSeries;
    public int SelectedIndex => selectedIndex;
    public CurvePoint SelectedPoint => GetPoints(activeSeries)[selectedIndex];

    public void SetActiveSeries(FanCurveSeries series)
    {
        if (activeSeries == series) return;
        var previous = CaptureFrame();
        CancelPlacement();
        activeSeries = series;
        selectedIndex = Math.Min(selectedIndex, GetPoints(series).Count - 1);
        StartLayoutTransition(previous);
        surface.Invalidate();
        RaiseSelectionChanged();
    }

    public void UpdateSelectedPoint(int temperature, int targetPercent)
    {
        StopLayoutTransition();
        FanCurveDraft.Update(GetPoints(activeSeries), selectedIndex, temperature, targetPercent);
        surface.Invalidate();
        RaiseSelectionChanged();
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        if (width <= LeftInset + RightInset || height <= TopInset + BottomInset) return;

        CanvasDrawingSession drawing = args.DrawingSession;
        DrawGrid(drawing, width, height);
        double progress = TransitionProgress();
        if (transitionFrom is not null && transitionTo is not null)
            DrawFrame(sender, drawing, width, height, transitionFrom, (float)(1 - progress));
        DrawFrame(sender, drawing, width, height, transitionTo ?? CaptureFrame(), (float)progress);
        if (dragging) DrawDragTooltip(drawing, width, height, SelectedPoint, ActiveSeries == FanCurveSeries.Cpu ? CpuColor : GpuColor);
    }

    private static void DrawDragTooltip(CanvasDrawingSession drawing, float width, float height, CurvePoint point, Color accent)
    {
        const float tooltipWidth = 142f;
        const float tooltipHeight = 38f;
        int rpm = Math.Clamp((int)Math.Round((1800 + Math.Clamp(point.TargetPercent, 0, 100) * 40) / 100d) * 100, 1800, 5800);
        float nodeX = MapX(point.Temperature, width);
        float nodeY = MapY(point.TargetPercent, height);
        float x = Math.Clamp(nodeX + 12f, LeftInset + 2f, width - RightInset - tooltipWidth - 2f);
        float y = Math.Clamp(nodeY - tooltipHeight - 10f, TopInset + 2f, height - BottomInset - tooltipHeight - 2f);
        var bounds = new Rect(x, y, tooltipWidth, tooltipHeight);
        drawing.FillRectangle(bounds, Color.FromArgb(242, 16, 20, 27));
        drawing.DrawRectangle(bounds, Color.FromArgb(230, accent.R, accent.G, accent.B), 1.2f);
        using var labelFormat = new CanvasTextFormat { FontFamily = "Segoe UI Variable", FontSize = 11.5f };
        using var detailFormat = new CanvasTextFormat { FontFamily = "Segoe UI Variable", FontSize = 10f };
        drawing.DrawText($"目标 {rpm} RPM", x + 7f, y + 3f, Colors.White, labelFormat);
        drawing.DrawText($"{point.Temperature}°C  ·  {point.TargetPercent}%", x + 7f, y + 20f, Color.FromArgb(205, 220, 225, 232), detailFormat);
    }

    private void DrawFrame(CanvasControl sender, CanvasDrawingSession drawing, float width, float height, CurveFrame frame, float opacity)
    {
        if (opacity <= 0) return;
        var cpuColor = IsEnabled ? CpuColor : Color.FromArgb(255, 160, 165, 174);
        var gpuColor = IsEnabled ? GpuColor : Color.FromArgb(255, 160, 165, 174);
        var recommendation = FanCurveDraft.Recommended(frame.Profile, frame.IsShared || frame.ActiveSeries == FanCurveSeries.Gpu);
        using (var stroke = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash })
            for (int i = 1; i < recommendation.Length; i++)
                drawing.DrawLine(MapX(recommendation[i - 1].Temperature, width), MapY(recommendation[i - 1].TargetPercent, height),
                    MapX(recommendation[i].Temperature, width), MapY(recommendation[i].TargetPercent, height),
                    WithOpacity(Color.FromArgb(85, 210, 215, 224), opacity), 1, stroke);
        if (!frame.IsShared)
        {
            DrawSmoothCurve(sender, drawing, width, height, frame.Gpu, gpuColor, frame.ActiveSeries == FanCurveSeries.Gpu, opacity);
            DrawNodes(drawing, width, height, frame.Gpu, gpuColor, frame.ActiveSeries == FanCurveSeries.Gpu, FanCurveSeries.Gpu, opacity);
        }
        var cpu = frame.IsShared ? frame.Shared : frame.Cpu;
        DrawSmoothCurve(sender, drawing, width, height, cpu, cpuColor, frame.IsShared || frame.ActiveSeries == FanCurveSeries.Cpu, opacity);
        DrawNodes(drawing, width, height, cpu, cpuColor, frame.IsShared || frame.ActiveSeries == FanCurveSeries.Cpu,
            frame.ActiveSeries == FanCurveSeries.Gpu && frame.IsShared ? FanCurveSeries.Gpu : FanCurveSeries.Cpu, opacity);
    }

    private void StartLayoutTransition(CurveFrame previous)
    {
        motionSettings.Refresh();
        if (!IsLoaded || ReducedMotion || !new UISettings().AnimationsEnabled || motionSettings.IsReducedMotionEnabled ||
            XamlRoot?.IsHostVisible != true)
        {
            StopLayoutTransition();
            return;
        }

        StopLayoutTransition();
        transitionFrom = previous;
        transitionTo = CaptureFrame();
        transitionStarted = motionClock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnLayoutTransitionFrame;
        transitionSubscribed = true;
    }

    private void OnLayoutTransitionFrame(object? sender, object args)
    {
        if (!IsLoaded || XamlRoot?.IsHostVisible != true || TransitionProgress() >= 1)
        {
            StopLayoutTransition();
            surface.Invalidate();
            return;
        }
        surface.Invalidate();
    }

    private double TransitionProgress() => transitionFrom is null || transitionTo is null
        ? 1
        : Math.Clamp((motionClock.Elapsed.TotalSeconds - transitionStarted) / LayoutTransitionDuration.TotalSeconds, 0, 1);

    private void StopLayoutTransition()
    {
        if (transitionSubscribed) CompositionTarget.Rendering -= OnLayoutTransitionFrame;
        transitionSubscribed = false;
        transitionFrom = null;
        transitionTo = null;
    }

    private CurveFrame CaptureFrame() => new(profile, Draft.IsShared, activeSeries,
        Draft.Cpu.ToArray(), Draft.Gpu.ToArray(), Draft.Shared.ToArray());

    private static Color WithOpacity(Color color, float opacity) =>
        Color.FromArgb((byte)Math.Clamp((int)Math.Round(color.A * opacity), 0, 255), color.R, color.G, color.B);

    private static void DrawGrid(CanvasDrawingSession drawing, float width, float height)
    {
        Color grid = Color.FromArgb(34, 255, 255, 255);
        Color axis = Color.FromArgb(88, 255, 255, 255);
        using var labelFormat = new CanvasTextFormat { FontFamily = "Segoe UI Variable", FontSize = 10.5f };
        for (int percent = 0; percent <= 100; percent += 25)
        {
            float y = MapY(percent, height);
            drawing.DrawLine(LeftInset, y, width - RightInset, y, percent == 0 ? axis : grid, percent == 0 ? 1.2f : 0.8f);
            drawing.DrawText(percent.ToString(), 12, y - 7, Color.FromArgb(145, 210, 214, 220), labelFormat);
        }
        for (int temperature = 30; temperature <= 100; temperature += 10)
        {
            float x = MapX(temperature, width);
            drawing.DrawLine(x, TopInset, x, height - BottomInset, temperature == 30 ? axis : grid, temperature == 30 ? 1.2f : 0.8f);
            drawing.DrawText(temperature.ToString(), x - 8, height - BottomInset + 10, Color.FromArgb(145, 210, 214, 220), labelFormat);
        }
    }

    private static void DrawSafetyBoundary(CanvasDrawingSession drawing, float width, float height, int temperature, Color color)
    {
        float x = MapX(temperature, width);
        drawing.DrawLine(x, TopInset, x, height - BottomInset, Color.FromArgb(80, color.R, color.G, color.B), 5f);
        drawing.DrawLine(x, TopInset, x, height - BottomInset, Color.FromArgb(190, color.R, color.G, color.B), 1.2f);
    }

    private static void DrawSmoothCurve(CanvasControl sender, CanvasDrawingSession drawing, float width, float height, IReadOnlyList<CurvePoint> points, Color color, bool active, float opacity)
    {
        var mapped = new Vector2[points.Count];
        for (int index = 0; index < points.Count; index++)
            mapped[index] = new Vector2(MapX(points[index].Temperature, width), MapY(points[index].TargetPercent, height));

        using var builder = new CanvasPathBuilder(sender);
        builder.BeginFigure(mapped[0]);
        for (int index = 0; index < mapped.Length - 1; index++)
        {
            Vector2 next = mapped[index + 1];
            builder.AddLine(next);
        }
        builder.EndFigure(CanvasFigureLoop.Open);
        using CanvasGeometry geometry = CanvasGeometry.CreatePath(builder);
        using var stroke = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        drawing.DrawGeometry(geometry, WithOpacity(Color.FromArgb((byte)(active ? 255 : 190), color.R, color.G, color.B), opacity), active ? 3.4f : 2.7f, stroke);
    }

    private void DrawNodes(CanvasDrawingSession drawing, float width, float height, IReadOnlyList<CurvePoint> points, Color color, bool active, FanCurveSeries series, float opacity)
    {
        for (int index = 0; index < points.Count; index++)
        {
            float x = MapX(points[index].Temperature, width);
            float y = MapY(points[index].TargetPercent, height);
            bool selected = active && index == selectedIndex && series == activeSeries;
            drawing.FillCircle(x, y, selected ? 7f : active ? 5f : 3.5f, WithOpacity(Color.FromArgb(255, 9, 11, 15), opacity));
            drawing.DrawCircle(x, y, selected ? 7f : active ? 5f : 3.5f, WithOpacity(Color.FromArgb((byte)(active ? 255 : 150), color.R, color.G, color.B), opacity), selected ? 3f : 2f);
            if (selected) drawing.DrawCircle(x, y, 11f, WithOpacity(Color.FromArgb(75, color.R, color.G, color.B), opacity), 3f);
        }
    }


    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointer = args.GetCurrentPoint(surface);
        if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pointer.Properties.IsLeftButtonPressed) return;
        Point position = pointer.Position;
        if (!InsidePlot(position)) return;
        var points = GetPoints(activeSeries);
        float width = (float)surface.ActualWidth;
        float height = (float)surface.ActualHeight;
        int nearest = -1;
        double nearestDistance = 18;
        for (int index = 0; index < points.Count; index++)
        {
            double distance = Math.Sqrt(Math.Pow(position.X - MapX(points[index].Temperature, width), 2) + Math.Pow(position.Y - MapY(points[index].TargetPercent, height), 2));
            if (distance >= nearestDistance) continue;
            nearest = index;
            nearestDistance = distance;
        }
        if (nearest < 0 || IsAddingPoint)
        {
            int previousCount = points.Count;
            nearest = FanCurveDraft.Add(points, TemperatureAt(position.X), TargetAt(position.Y));
            if (points.Count != previousCount) DraftChanged?.Invoke(this, EventArgs.Empty);
        }
        IsAddingPoint = false;
        selectedIndex = nearest;
        dragging = surface.CapturePointer(args.Pointer);
        args.Handled = true;
        surface.Invalidate();
        RaiseSelectionChanged();
    }

    private sealed record CurveFrame(int Profile, bool IsShared, FanCurveSeries ActiveSeries,
        CurvePoint[] Cpu, CurvePoint[] Gpu, CurvePoint[] Shared);

    private bool InsidePlot(Point p) => p.X >= LeftInset && p.X <= surface.ActualWidth - RightInset
        && p.Y >= TopInset && p.Y <= surface.ActualHeight - BottomInset;
    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, new CurvePointSelectedEventArgs(activeSeries, selectedIndex, SelectedPoint));
    private List<CurvePoint> GetPoints(FanCurveSeries series) => Draft.Points(series);
    private int TemperatureAt(double x) => (int)Math.Round(30 + Math.Clamp((x - LeftInset) / Math.Max(1, surface.ActualWidth - LeftInset - RightInset), 0, 1) * 70);
    private int TargetAt(double y) => (int)Math.Round(100 - Math.Clamp((y - TopInset) / Math.Max(1, surface.ActualHeight - TopInset - BottomInset), 0, 1) * 100);
    private static float MapX(int temperature, float width) => LeftInset + (temperature - 30) / 70f * (width - LeftInset - RightInset);
    private static float MapY(int percent, float height) => TopInset + (100 - percent) / 100f * (height - TopInset - BottomInset);
}
