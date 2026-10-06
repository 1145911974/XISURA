using System.Diagnostics;
using System.Numerics;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Branding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class HomeHero : UserControl
{
    private Storyboard? viewStoryboard;
    private readonly Stopwatch radarClock = new();
    private readonly double[] radarValues = new double[5];
    private double[] radarFrom = new double[5];
    private double[] radarTarget = new double[5];
    private Color[] radarColors = new Color[3];
    private Color[] radarColorFrom = new Color[3];
    private Color[] radarColorTarget = new Color[3];
    private TimeSpan radarDuration;
    private bool radarRenderingSubscribed;
    private bool adaptiveEffectsVisible = true;
    private bool showingLogo = true;
    private bool suppressAdaptiveModeChanged;
    private bool adaptiveRequestPending;
    private bool displayedAdaptiveEnabled;
    private Storyboard? automaticHaloStoryboard;
    private CompositeTransform[] automaticHaloTransforms = [];
    private static readonly Vector2[] AutomaticEdgeOffsets =
        [new(0, -95), new(-100, 0), new(85, -70), new(90, 40), new(0, 100), new(-70, 50)];

    public event Action<bool>? AdaptiveModeChanged;
    public HeroLogoProfile LogoProfile { get; }
    public bool ReducedMotion { get; set; }

    public void SetBrandingState(string style, ControlModeId? mode, bool animate)
    {
        bool explore = style != "classic";
        LogoVisual.Visibility = explore ? Visibility.Collapsed : Visibility.Visible;
        WaveLogoVisual.Visibility = explore ? Visibility.Visible : Visibility.Collapsed;
        LogoVisualHost.Width = LogoVisualHost.Height = explore ? 320 : LogoProfile.Size;
        Canvas.SetLeft(LogoVisualHost, explore ? 144.5 : LogoProfile.Left);
        Canvas.SetTop(LogoVisualHost, explore ? 108 : LogoProfile.Top);
        WaveLogoVisual.ReduceMotion = ReducedMotion;
        WaveLogoVisual.SetState(mode, animate && explore);
    }

    public void ApplyHardwareIdentity(HardwareIdentity identity)
    {
        CpuModelText.Text = string.IsNullOrWhiteSpace(identity.CpuModel) || identity.CpuModel == "未知"
            ? "CPU 未知"
            : identity.CpuModel;
        GpuModelText.Text = string.IsNullOrWhiteSpace(identity.GpuName) || identity.GpuName == "未知"
            ? "GPU 未知"
            : identity.GpuName;
    }

    public void ApplyAdaptiveModeState(bool? enabled, bool available, bool pending = false)
    {
        var previousEnabled = AutomaticModeButton.IsChecked == true;
        var targetEnabled = enabled ?? false;
        adaptiveRequestPending = pending;
        displayedAdaptiveEnabled = targetEnabled;
        suppressAdaptiveModeChanged = true;
        try
        {
            AutomaticModeButton.IsEnabled = available;
            AutomaticModeButton.IsHitTestVisible = !pending;
            AutomaticModeButton.IsChecked = targetEnabled;
            if (previousEnabled != targetEnabled)
                AnimateAutomaticHalos(targetEnabled);
            AutomationProperties.SetName(
                AutomaticModeButton,
                !available
                    ? "自适应模式不可用"
                    : enabled switch
                    {
                        true => "自适应模式已开启",
                        false => "自适应模式已关闭",
                        _ => "自适应模式"
                    });
            AdaptiveModeTitleText.Text = "自适应模式";
        }
        finally
        {
            suppressAdaptiveModeChanged = false;
        }
    }

    public HomeHero()
    {
        var loaded = HeroLogoProfileStore.LoadOrDefault(HeroLogoProfileStore.OutputProfilePath());
        LogoProfile = loaded.Profile;
        if (loaded.Error is not null) Debug.WriteLine(loaded.Error);
        InitializeComponent();
        SetBrandingState("explore", null, animate: false);
        automaticHaloTransforms =
        [
            AutoHalo01Transform, AutoHalo02Transform, AutoHalo03Transform,
            AutoHalo04Transform, AutoHalo05Transform, AutoHalo06Transform
        ];
        Loaded += (_, _) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(AutoModeHaloLayer);
            var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2(158, 148);
            geometry.CornerRadius = new Vector2(28);
            visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
            ApplyAutomaticVisual(adaptiveEffectsVisible && AutomaticModeButton.IsChecked == true);
        };
        radarColors = RadarEnergyBrush.GradientStops.Select(stop => stop.Color).ToArray();
        LogoVisualHost.Translation = new Vector3(0, 0, 24);
        Unloaded += OnUnloaded;
        ApplyPerformanceProfile(PerformanceRadarProfile.ForMode(PrototypePerformanceMode.Office, "Normal", null), TimeSpan.Zero);
    }

    public void ApplyMode(PrototypeModeTheme theme, string turboTier, string? customProfile, TimeSpan duration)
    {
        var mode = theme.Mode;
        LogoVisual.ReducedMotion = ReducedMotion;
        LogoVisual.TransitionDuration = duration;
        if (mode == PrototypePerformanceMode.Custom && customProfile is not null)
            LogoVisual.CustomProfile = customProfile;
        LogoVisual.Mode = mode;
        ApplyPerformanceProfile(
            PerformanceRadarProfile.ForMode(mode, turboTier, customProfile),
            duration,
            RadarColors(theme));
    }

    public void ApplyPerformanceProfile(PerformanceRadarProfile profile, TimeSpan? duration = null) =>
        ApplyPerformanceProfile(profile, duration, radarColors);

    private void ApplyPerformanceProfile(
        PerformanceRadarProfile profile,
        TimeSpan? duration,
        IReadOnlyList<Color> targetColors)
    {
        profile = profile.Normalized();
        var target = profile.Values.Select(value => (double)value).ToArray();
        var effectiveDuration = ReducedMotion ? TimeSpan.Zero : duration ?? TimeSpan.FromMilliseconds(220);
        if (effectiveDuration <= TimeSpan.Zero)
        {
            StopRadarAnimation();
            target.CopyTo(radarValues, 0);
            radarColors = targetColors.ToArray();
            RenderRadarFrame();
            return;
        }

        radarFrom = (double[])radarValues.Clone();
        radarTarget = target;
        radarColorFrom = (Color[])radarColors.Clone();
        radarColorTarget = targetColors.ToArray();
        radarDuration = effectiveDuration;
        radarClock.Restart();
        if (!radarRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnRadarRendering;
            radarRenderingSubscribed = true;
        }
    }

    private void OnRadarRendering(object? sender, object e)
    {
        var progress = Math.Clamp(radarClock.Elapsed.TotalMilliseconds / radarDuration.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        for (var index = 0; index < radarValues.Length; index++)
            radarValues[index] = radarFrom[index] + ((radarTarget[index] - radarFrom[index]) * eased);
        for (var index = 0; index < radarColors.Length; index++)
            radarColors[index] = LerpColor(radarColorFrom[index], radarColorTarget[index], eased);
        RenderRadarFrame();
        if (progress >= 1) StopRadarAnimation();
    }

    private void RenderRadarFrame()
    {
        const double center = 180;
        const double radius = 104;
        var points = new PointCollection();
        for (var i = 0; i < radarValues.Length; i++)
        {
            var angle = (-90 + i * 72) * Math.PI / 180;
            var scaledRadius = radius * radarValues[i] / 100;
            points.Add(new Point(
                center + Math.Cos(angle) * scaledRadius,
                center + Math.Sin(angle) * scaledRadius));
        }

        RadarEnergyFill.Points = points;
        for (var index = 0; index < radarColors.Length; index++)
            RadarEnergyBrush.GradientStops[index].Color = radarColors[index];
        var values = radarValues.Select(value => (int)Math.Round(value)).ToArray();
        CpuValue.Text = values[0].ToString();
        GpuValue.Text = values[1].ToString();
        CoolingValue.Text = values[2].ToString();
        ResponseValue.Text = values[3].ToString();
        QuietValue.Text = values[4].ToString();
        AutomationProperties.SetName(
            PerformanceRadar,
            $"性能雷达图，CPU {values[0]}，GPU {values[1]}，散热 {values[2]}，响应 {values[3]}，静音 {values[4]}");
    }

    private void StopRadarAnimation()
    {
        radarClock.Reset();
        if (!radarRenderingSubscribed) return;
        CompositionTarget.Rendering -= OnRadarRendering;
        radarRenderingSubscribed = false;
    }

    public void SetHighContrast(bool enabled)
    {
        WaveLogoVisual.SetHighContrast(enabled);
        PerformanceRadarGrid.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        var valueBrush = Brush(enabled ? "TextPrimaryBrush" : "ModeAccentBrush");
        RadarEnergyFill.Fill = enabled ? Transparent() : RadarEnergyBrush;
        RadarEnergyFill.Stroke = valueBrush;
        CpuValue.Foreground = valueBrush;
        GpuValue.Foreground = valueBrush;
        CoolingValue.Foreground = valueBrush;
        ResponseValue.Foreground = valueBrush;
        QuietValue.Foreground = valueBrush;
    }

    private void OnViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string view }) return;
        var showLogo = view == "Logo";
        if (showLogo == showingLogo) return;
        showingLogo = showLogo;
        LogoVisualHost.IsHitTestVisible = showLogo;
        PerformanceRadar.IsHitTestVisible = !showLogo;
        var current = new[]
        {
            HeroSelectionTransform.X, LogoVisualHost.Opacity, PerformanceRadar.Opacity,
            LogoViewScale.ScaleX, RadarViewScale.ScaleX,
        };
        viewStoryboard?.Stop();
        HeroSelectionTransform.X = current[0];
        LogoVisualHost.Opacity = current[1];
        PerformanceRadar.Opacity = current[2];
        LogoViewScale.ScaleX = LogoViewScale.ScaleY = current[3];
        RadarViewScale.ScaleX = RadarViewScale.ScaleY = current[4];

        var duration = ReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(190);
        if (duration <= TimeSpan.Zero)
        {
            SetViewVisuals(showLogo);
            return;
        }

        var storyboard = new Storyboard();
        AddAnimation(storyboard, HeroSelectionTransform, "X", current[0], showLogo ? 0 : 99, duration, dependent: true);
        AddAnimation(storyboard, LogoVisualHost, "Opacity", current[1], showLogo ? 1 : 0, duration, dependent: false);
        AddAnimation(storyboard, PerformanceRadar, "Opacity", current[2], showLogo ? 0 : 1, duration, dependent: false);
        AddAnimation(storyboard, LogoViewScale, "ScaleX", current[3], showLogo ? 1 : 0.96, duration, dependent: false);
        AddAnimation(storyboard, LogoViewScale, "ScaleY", current[3], showLogo ? 1 : 0.96, duration, dependent: false);
        AddAnimation(storyboard, RadarViewScale, "ScaleX", current[4], showLogo ? 0.96 : 1, duration, dependent: false);
        AddAnimation(storyboard, RadarViewScale, "ScaleY", current[4], showLogo ? 0.96 : 1, duration, dependent: false);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(viewStoryboard, storyboard)) return;
            SetViewVisuals(showLogo);
            storyboard.Stop();
            viewStoryboard = null;
        };
        viewStoryboard = storyboard;
        storyboard.Begin();
    }

    private void SetViewVisuals(bool showLogo)
    {
        HeroSelectionTransform.X = showLogo ? 0 : 99;
        LogoVisualHost.Opacity = showLogo ? 1 : 0;
        PerformanceRadar.Opacity = showLogo ? 0 : 1;
        LogoViewScale.ScaleX = LogoViewScale.ScaleY = showLogo ? 1 : 0.96;
        RadarViewScale.ScaleX = RadarViewScale.ScaleY = showLogo ? 0.96 : 1;
    }

    private static Color[] RadarColors(PrototypeModeTheme theme)
    {
        return
        [
            WithAlpha(ParseHex(theme.GlowHex), 0xB8),
            WithAlpha(ParseHex(theme.AccentHex), 0x66),
            WithAlpha(Microsoft.UI.Colors.White, 0x78),
        ];
    }

    private void OnAutomaticModeChanged(object sender, RoutedEventArgs e)
    {
        if (suppressAdaptiveModeChanged) return;
        if (adaptiveRequestPending)
        {
            ApplyAdaptiveModeState(displayedAdaptiveEnabled, AutomaticModeButton.IsEnabled, pending: true);
            return;
        }
        var enabled = AutomaticModeButton.IsChecked == true;
        AutomationProperties.SetName(AutomaticModeButton, enabled ? "自适应模式已开启" : "自适应模式已关闭");
        AdaptiveModeTitleText.Text = "自适应模式";
        AnimateAutomaticHalos(enabled);
        AdaptiveModeChanged?.Invoke(enabled);
    }

    public void SetAdaptiveEffectsVisible(bool visible)
    {
        adaptiveEffectsVisible = visible;
        automaticHaloStoryboard?.Stop();
        AutoModeHaloLayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var enabled = visible && AutomaticModeButton.IsChecked == true;
        ApplyAutomaticVisual(enabled);
    }

    private void ApplyAutomaticVisual(bool enabled)
    {
        AutoModeHaloLayer.Opacity = enabled ? 0.95 : 0;
        for (var index = 0; index < automaticHaloTransforms.Length; index++)
        {
            var transform = automaticHaloTransforms[index];
            transform.ScaleX = transform.ScaleY = enabled ? 1.04 + index % 3 * 0.08 : 0.72;
            transform.TranslateX = enabled ? 0 : AutomaticEdgeOffsets[index].X;
            transform.TranslateY = enabled ? 0 : AutomaticEdgeOffsets[index].Y;
        }
    }

    private void AnimateAutomaticHalos(bool enabled)
    {
        var opacity = AutoModeHaloLayer.Opacity;
        var current = automaticHaloTransforms.Select(transform =>
            (transform.ScaleX, transform.ScaleY, transform.TranslateX, transform.TranslateY)).ToArray();
        automaticHaloStoryboard?.Stop();
        enabled &= adaptiveEffectsVisible;
        AutoModeHaloLayer.Opacity = opacity;
        for (var index = 0; index < automaticHaloTransforms.Length; index++)
        {
            var transform = automaticHaloTransforms[index];
            (transform.ScaleX, transform.ScaleY, transform.TranslateX, transform.TranslateY) = current[index];
        }
        if (ReducedMotion || !adaptiveEffectsVisible)
        {
            ApplyAutomaticVisual(enabled);
            return;
        }

        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(enabled ? 980 : 880);
        AddAnimation(storyboard, AutoModeHaloLayer, "Opacity", opacity, enabled ? 0.95 : 0, duration, false);
        for (var index = 0; index < automaticHaloTransforms.Length; index++)
        {
            var transform = automaticHaloTransforms[index];
            var targetScale = enabled ? 1.04 + (index % 3) * 0.08 : 0.72;
            var delay = TimeSpan.FromMilliseconds(index % 3 * 55);
            AddAnimation(storyboard, transform, "ScaleX", transform.ScaleX, targetScale, duration, false, delay);
            AddAnimation(storyboard, transform, "ScaleY", transform.ScaleY, targetScale, duration, false, delay);
            AddAnimation(storyboard, transform, "TranslateX", transform.TranslateX,
                enabled ? 0 : AutomaticEdgeOffsets[index].X, duration, false, delay);
            AddAnimation(storyboard, transform, "TranslateY", transform.TranslateY,
                enabled ? 0 : AutomaticEdgeOffsets[index].Y, duration, false, delay);
        }

        automaticHaloStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(automaticHaloStoryboard, storyboard)) automaticHaloStoryboard = null;
        };
        storyboard.Begin();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRadarAnimation();
        automaticHaloStoryboard?.Stop();
        viewStoryboard?.Stop();
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        TimeSpan duration,
        bool dependent,
        TimeSpan? delay = null)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            BeginTime = delay,
            EnableDependentAnimation = dependent,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static SolidColorBrush Transparent() => new(Microsoft.UI.Colors.Transparent);
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static Color LerpColor(Color from, Color to, double progress) => Color.FromArgb(
        LerpByte(from.A, to.A, progress),
        LerpByte(from.R, to.R, progress),
        LerpByte(from.G, to.G, progress),
        LerpByte(from.B, to.B, progress));
    private static byte LerpByte(byte from, byte to, double progress) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * progress)), byte.MinValue, byte.MaxValue);
    private static Color ParseHex(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
