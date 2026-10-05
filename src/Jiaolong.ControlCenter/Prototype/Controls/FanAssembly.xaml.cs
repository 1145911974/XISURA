using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media.Animation;
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class FanAssembly : UserControl
{
    private const int BladeCount = 9;
    private Storyboard? rotation;
    private long rotationStarted;
    private double startingAngle, startingSpeed, requestedSpeed;
    private const double RampSeconds = .55;

    public void SetRotationSpeed(double revolutionsPerSecond)
    {
        double speed = Math.Max(0, revolutionsPerSecond) * 360;
        if (speed == 0) { StopRotation(); return; }
        double elapsed = rotation is null ? 0 : Stopwatch.GetElapsedTime(rotationStarted).TotalSeconds;
        if (rotation is not null && Math.Abs(speed - requestedSpeed) < 7 && elapsed < 30) return;
        double ramp = Math.Min(elapsed, RampSeconds);
        double angle = rotation is null ? RotorRotation.Angle : startingAngle
            + FanMotionMath.RotationDistance(startingSpeed, requestedSpeed, elapsed, RampSeconds);
        double previousSpeed = rotation is null ? 0 : startingSpeed + (requestedSpeed - startingSpeed) * ramp / RampSeconds;
        rotation?.Stop();
        startingAngle = angle % 360;
        startingSpeed = previousSpeed;
        requestedSpeed = speed;
        RotorRotation.Angle = startingAngle;
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = startingAngle });
        double sum = previousSpeed + speed;
        double rampAngle = startingAngle + sum * RampSeconds / 2;
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(RampSeconds)), Value = rampAngle,
            KeySpline = new KeySpline
            {
                ControlPoint1 = new Point(1d / 3, 2 * previousSpeed / (3 * sum)),
                ControlPoint2 = new Point(2d / 3, (3 * previousSpeed + speed) / (3 * sum))
            }
        });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(60)), Value = rampAngle + speed * (60 - RampSeconds) });
        Storyboard.SetTarget(animation, RotorRotation);
        Storyboard.SetTargetProperty(animation, "Angle");
        rotation = new Storyboard();
        rotation.Children.Add(animation);
        rotationStarted = Stopwatch.GetTimestamp();
        rotation.Begin();
    }

    public void StopRotation()
    {
        if (rotation is null) return;
        double elapsed = Stopwatch.GetElapsedTime(rotationStarted).TotalSeconds;
        double angle = startingAngle + FanMotionMath.RotationDistance(startingSpeed, requestedSpeed, elapsed, RampSeconds);
        rotation.Stop(); rotation = null;
        RotorRotation.Angle = angle % 360;
    }

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor), typeof(Color), typeof(FanAssembly),
        new PropertyMetadata(Color.FromArgb(255, 255, 36, 79), OnAccentColorChanged));

    public FanAssembly()
    {
        InitializeComponent();
        BuildRotor();
        ApplyAccent(AccentColor);
        Unloaded += (_, _) => StopRotation();
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public FrameworkElement RotorElement => RotorSurface;

    public void SetRotorAngle(double angle) => RotorRotation.Angle = angle;

    public void SetStrength(double strength)
    {
        strength = Math.Clamp(strength, 0d, 1d);
        GlowRing.Opacity = 0.46 + strength * 0.36;
        AmbientHalo.Opacity = 0.55 + strength * 0.20;
    }

    private void BuildRotor()
    {
        RotorSurface.Children.Clear();
        double step = 360d / BladeCount;
        for (int index = 0; index < BladeCount; index++)
        {
            var blade = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = CreateBladeGeometry(),
                Fill = CreateBladeBrush(),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 5, 7, 10)),
                StrokeThickness = 3,
                RenderTransform = new RotateTransform
                {
                    Angle = index * step,
                    CenterX = 256,
                    CenterY = 256
                }
            };
            RotorSurface.Children.Add(blade);
        }
    }

    private static Geometry CreateBladeGeometry()
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(230, 195),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(185, 155),
            Point2 = new Point(169, 83),
            Point3 = new Point(193, 51)
        });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(305, 47),
            Size = new Size(216, 216),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(278, 91),
            Point2 = new Point(298, 154),
            Point3 = new Point(281, 195)
        });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(263, 191),
            Point2 = new Point(245, 191),
            Point3 = new Point(230, 195)
        });
        return new PathGeometry { Figures = { figure } };
    }

    private static Brush CreateBladeBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.18, 0),
            EndPoint = new Point(0.86, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 73, 78, 86), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 31, 35, 42), Offset = 0.46 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 9, 11, 15), Offset = 1 });
        return brush;
    }

    private static void OnAccentColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((FanAssembly)sender).ApplyAccent((Color)args.NewValue);

    private void ApplyAccent(Color color)
    {
        GlowRing.Stroke = new SolidColorBrush(Color.FromArgb(210, color.R, color.G, color.B));
        var halo = new RadialGradientBrush();
        foreach (var (offset, alpha) in new (double, byte)[] { (0, 0), (.72, 0), (.81, 150), (.88, 70), (.95, 18), (1, 0) })
            halo.GradientStops.Add(new GradientStop { Offset = offset, Color = Color.FromArgb(alpha, color.R, color.G, color.B) });
        AmbientHalo.Fill = halo;
    }
}
