using System.Runtime.InteropServices;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.Storage;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuRouteDiagram : UserControl
{
    private static readonly Color Green = Color.FromArgb(255, 91, 237, 99);
    private static readonly Color Cyan = Color.FromArgb(255, 47, 224, 245);
    private static readonly Color AmdRed = Color.FromArgb(255, 255, 80, 88);
    private static readonly Color IntelBlue = Color.FromArgb(255, 41, 169, 255);
    private static readonly Color Muted = Color.FromArgb(255, 151, 160, 169);
    private readonly MotionSettingsService motionSettings = new();
    private Storyboard? modeTransition;
    private Storyboard? displayTransition;
    private UIElement[] transitionElements = [];
    private double[] transitionBaseOpacity = [];
    private MuxMode? configuredMode;
    private MuxMode? draftMode;
    private MuxMode? effectiveMode;
    private MuxMode? acceptedPendingMode;
    private string? displaySignature;
    private string? topologySignature;
    private string? mainWallpaperPath;
    private string? mainWallpaperDeviceName;
    private long mainWallpaperWriteTicks;
    private DateTime nextWallpaperReadUtc;
    private DateTime nextGpuBrandReadUtc;
    private GpuBrandReader.Brands gpuBrands;
    private string? gpuName;
    private DisplayView[] currentDisplays = [];
    private int displayAnimationVersion;
    private bool reducedMotion;

    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            if (value)
            {
                displayTransition?.Stop();
                StopModeTransition();
                RebuildDisplayCards(currentDisplays);
                RebuildTopology(currentDisplays);
                ActiveDisplaysHost.Opacity = TopologyHost.Opacity = 1;
                DisplaysTranslate.Y = TopologyTranslate.Y = 0;
            }
        }
    }

    public GpuRouteDiagram()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshLiveLabels();
        Unloaded += (_, _) =>
        {
            ++displayAnimationVersion;
            displayTransition?.Stop(); displayTransition = null; StopModeTransition();
            RebuildDisplayCards(currentDisplays); RebuildTopology(currentDisplays);
#if DEBUG
            routePreviewTimer?.Stop(); routePreviewTimer = null;
#endif
        };
    }

    public event EventHandler<MuxMode>? ModeRequested;
    public void SetAvailable(bool available) => HybridButton.IsEnabled = DiscreteButton.IsEnabled = available;

    public void SetGpuName(string? name)
    {
        if (gpuName == name) return;
        gpuName = name;
        if (currentDisplays.Length > 0)
            TransitionDisplays(currentDisplays);
    }

    public void RefreshDisplays() => RefreshLiveLabels();

    public void SetUnknownMode()
    {
        configuredMode = null;
        SetButtonState(HybridButton, false);
        SetButtonState(DiscreteButton, false);
        UpdateModeStatus();
    }

    public void SetMode(MuxMode mode)
    {
        bool animate = configuredMode is not null && configuredMode != mode;
        configuredMode = mode;
        SetButtonState(HybridButton, (draftMode ?? mode) == MuxMode.Hybrid);
        SetButtonState(DiscreteButton, (draftMode ?? mode) == MuxMode.Discrete);
        UpdateModeStatus();
        if (animate) AnimateModeChange(HybridButton, DiscreteButton, CurrentModeText, ConfiguredModeText);
    }

    public void SetDraftMode(MuxMode? mode)
    {
        bool changed = draftMode != mode;
        draftMode = mode;
        SetButtonState(HybridButton, (draftMode ?? configuredMode) == MuxMode.Hybrid);
        SetButtonState(DiscreteButton, (draftMode ?? configuredMode) == MuxMode.Discrete);
        if (changed) AnimateModeChange(HybridButton, DiscreteButton);
    }

    public void AnimateAcceptedRequest(MuxMode mode, bool requiresRestart)
    {
        acceptedPendingMode = requiresRestart ? mode : null;
        UpdateModeStatus();
        AnimateModeChange(mode == MuxMode.Hybrid ? HybridButton : DiscreteButton);
    }

    private void UpdateModeStatus()
    {
        CurrentModeText.Text = ModeLabel(effectiveMode);
        ConfiguredModeText.Text = configuredMode is null ? "未读回" : ModeLabel(configuredMode);
        CurrentModeText.Foreground = new SolidColorBrush(effectiveMode == MuxMode.Hybrid ? BrandColor(gpuBrands.Integrated) :
            effectiveMode == MuxMode.Discrete ? BrandColor(gpuBrands.Discrete) : Muted);
        ConfiguredModeText.Foreground = configuredMode is null
            ? new SolidColorBrush(Muted) : (Brush)Application.Current.Resources["ModeAccentBrush"];
        bool pending = configuredMode is not null &&
            (effectiveMode is not null && effectiveMode != configuredMode ||
             effectiveMode is null && acceptedPendingMode == configuredMode);
        PendingRestartText.Visibility = pending
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(CurrentModeText, effectiveMode is null
            ? "当前没有可用于确认生效模式的内屏输出路径"
            : "依据当前内屏的实际扫描输出适配器判断");
        ToolTipService.SetToolTip(ConfiguredModeText, "固件 MUX 设置读回");
    }

    private static string ModeLabel(MuxMode? mode) => mode switch
    {
        MuxMode.Hybrid => "混合输出",
        MuxMode.Discrete => "独显直连",
        _ => "未能判定"
    };

    private static void SetButtonState(Button button, bool selected)
    {
        var accent = (Brush)Application.Current.Resources["ModeAccentBrush"];
        button.Background = selected ? (Brush)Application.Current.Resources["ModeSelectionBrush"]
            : (Brush)Application.Current.Resources["PrototypeControlAcrylicBrush"];
        button.BorderBrush = selected ? accent : new SolidColorBrush(Color.FromArgb(100, 112, 124, 136));
        button.BorderThickness = new Thickness(1);
    }

    private void AnimateModeChange(params UIElement[] elements)
    {
        motionSettings.Refresh();
        StopModeTransition();
        if (ReducedMotion || motionSettings.IsReducedMotionEnabled || !new UISettings().AnimationsEnabled) return;
        transitionElements = elements;
        transitionBaseOpacity = elements.Select(element => element.Opacity).ToArray();
        modeTransition = new Storyboard();
        for (int index = 0; index < elements.Length; index++)
        {
            var animation = new DoubleAnimation
            {
                From = transitionBaseOpacity[index] * 0.76,
                To = transitionBaseOpacity[index],
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, elements[index]);
            Storyboard.SetTargetProperty(animation, "Opacity");
            modeTransition.Children.Add(animation);
        }
        modeTransition.Begin();
    }

    private void StopModeTransition()
    {
        modeTransition?.Stop();
        for (int index = 0; index < transitionElements.Length; index++)
            transitionElements[index].Opacity = transitionBaseOpacity[index];
        transitionElements = [];
        transitionBaseOpacity = [];
    }

#if DEBUG
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? routePreviewTimer;
    private void StartRoutePreview()
    {
        int count = 0;
        gpuBrands = new GpuBrandReader.Brands("NVIDIA", "AMD");
        void ShowNext()
        {
            count = count % 4 + 1;
            var displays = Enumerable.Range(0, count).Select(index => new DisplayView(
                new WindowsDisplayRouteReader.ActiveDisplayEndpoint($"PREVIEW{index}", index == 0,
                    index == 0 ? "内接显示" : "DisplayPort", index == 0 ? "AMD GPU" : "NVIDIA dGPU", $"预览屏幕 {index + 1}"),
                new MonitorSnapshot($"PREVIEW{index}", 2560, 1600, index == 0, 0, 0, 2560, 1600))).ToArray();
            currentDisplays = displays;
            effectiveMode = MuxMode.Hybrid;
            TransitionDisplays(displays);
            AutomationProperties.SetName(TopologyHost, $"GPU route preview {count}");
            AppRuntimeLog.Write($"GPU route preview {count}");
        }
        routePreviewTimer = DispatcherQueue.CreateTimer();
        routePreviewTimer.Interval = TimeSpan.FromSeconds(3);
        routePreviewTimer.Tick += (_, _) => ShowNext();
        ShowNext();
        routePreviewTimer.Start();
    }
#endif

    private void RefreshLiveLabels()
    {
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--gpu-route-preview"))
        {
            if (IsLoaded && routePreviewTimer is null) StartRoutePreview();
            return;
        }
#endif
        if (DateTime.UtcNow >= nextGpuBrandReadUtc)
        {
            gpuBrands = GpuBrandReader.Read();
            nextGpuBrandReadUtc = DateTime.UtcNow.AddSeconds(30);
        }
        var endpoints = WindowsDisplayRouteReader.ReadActiveEndpoints();
        var monitors = EnumerateMonitors();
        var displays = endpoints.Select(endpoint => new DisplayView(endpoint,
                monitors.FirstOrDefault(monitor => string.Equals(monitor.DeviceName, endpoint.DeviceName, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(display => display.Monitor.Primary)
            .ThenBy(display => display.Endpoint.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // Windows briefly returns no paths while rearranging displays; retain the last valid scene.
        if (displays.Length == 0)
        {
            if (currentDisplays.Length == 0)
            {
                RebuildDisplayCards([]);
                RebuildTopology([]);
                ActiveDisplaysHost.Opacity = TopologyHost.Opacity = 1;
            }
            return;
        }

        if (DateTime.UtcNow >= nextWallpaperReadUtc ||
            !string.Equals(mainWallpaperDeviceName, displays[0].Endpoint.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            nextWallpaperReadUtc = DateTime.UtcNow.AddSeconds(5);
            mainWallpaperDeviceName = displays[0].Endpoint.DeviceName;
            var primary = displays[0].Monitor;
            var wallpapers = DesktopWallpaperReader.Read();
            var match = wallpapers.FirstOrDefault(wallpaper => wallpaper.Left == primary.Left &&
                wallpaper.Top == primary.Top && wallpaper.Right == primary.Right && wallpaper.Bottom == primary.Bottom);
            if (string.IsNullOrEmpty(match.Path) && primary.Primary)
                match = wallpapers.FirstOrDefault(wallpaper => wallpaper.Left <= 0 && wallpaper.Right > 0 &&
                    wallpaper.Top <= 0 && wallpaper.Bottom > 0);
            mainWallpaperPath = !string.IsNullOrWhiteSpace(match.Path) && File.Exists(match.Path) ? match.Path : null;
            mainWallpaperWriteTicks = mainWallpaperPath is null ? 0 : File.GetLastWriteTimeUtc(mainWallpaperPath).Ticks;
        }

        var signature = string.Join("|", displays.Select(display =>
            $"{display.Endpoint.DeviceName}:{display.Endpoint.TargetName}:{display.Endpoint.Connector}:{display.Endpoint.AdapterName}:{display.Monitor.Width}x{display.Monitor.Height}:{display.Monitor.Primary}")) +
            $"|wallpaper:{mainWallpaperPath}:{mainWallpaperWriteTicks}|gpu:{gpuBrands.Discrete}:{gpuBrands.Integrated}";
        if (signature == displaySignature) return;
        var nextTopologySignature = string.Join("|", displays.Select(display =>
            $"{display.Endpoint.DeviceName}:{display.Endpoint.AdapterName}:{display.Endpoint.IsInternal}:{display.Monitor.Primary}"));
        displaySignature = signature;
        topologySignature = nextTopologySignature;
        currentDisplays = displays;
        effectiveMode = InferEffectiveMode(displays);
        UpdateModeStatus();
        // A metadata refresh can arrive mid-morph; preserve the same cancellation path.
        TransitionDisplays(displays);
    }

    private MuxMode? InferEffectiveMode(DisplayView[] displays)
    {
        var internalAdapters = displays.Where(display => display.Endpoint.IsInternal)
            .Select(display => display.Endpoint.AdapterName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (internalAdapters.Length != 1) return null;
        return IsDiscreteAdapter(internalAdapters[0]) ? MuxMode.Discrete :
            IsIntegratedAdapter(internalAdapters[0]) ? MuxMode.Hybrid : null;
    }

    private void TransitionDisplays(DisplayView[] displays)
    {
        int version = ++displayAnimationVersion;
        // Read animated positions before stopping so rapid topology changes continue in place.
        var oldCards = CaptureElements(ActiveDisplaysHost);
        var oldTopology = CaptureElements(TopologyHost);
        displayTransition?.Stop();
        RebuildDisplayCards(displays);
        RebuildTopology(displays);
        ActiveDisplaysHost.Opacity = TopologyHost.Opacity = 1;
        DisplaysTranslate.Y = TopologyTranslate.Y = 0;
        motionSettings.Refresh();
        if (!IsLoaded || ReducedMotion || motionSettings.IsReducedMotionEnabled || !new UISettings().AnimationsEnabled) return;
        var storyboard = new Storyboard();
        displayTransition = storyboard;
        var departing = new List<(Canvas Host, FrameworkElement Element)>();
        AnimateElements(ActiveDisplaysHost, oldCards, storyboard, departing);
        AnimateElements(TopologyHost, oldTopology, storyboard, departing);
        storyboard.Completed += (_, _) =>
        {
            if (version != displayAnimationVersion || !ReferenceEquals(displayTransition, storyboard)) return;
            foreach (var (host, element) in departing) host.Children.Remove(element);
            displayTransition = null;
        };
        storyboard.Begin();
    }

    private sealed record ElementFrame(FrameworkElement Element, double Left, double Top, double Opacity, double Width, double Height,
        Point[]? Points);

    private static Dictionary<string, ElementFrame> CaptureElements(Canvas host) => host.Children
        .OfType<FrameworkElement>().Where(element => element.Tag is string)
        .ToDictionary(element => (string)element.Tag, element => new ElementFrame(element,
            double.IsNaN(Canvas.GetLeft(element)) ? 0 : Canvas.GetLeft(element),
            double.IsNaN(Canvas.GetTop(element)) ? 0 : Canvas.GetTop(element), element.Opacity, element.ActualWidth, element.ActualHeight,
            element is Path { Data: PathGeometry geometry } ? GeometryPoints(geometry) : null));

    private static Point[] GeometryPoints(PathGeometry geometry)
    {
        var figure = geometry.Figures[0];
        return new[] { figure.StartPoint }.Concat(figure.Segments.SelectMany(segment => segment switch
        {
            LineSegment line => new[] { line.Point },
            BezierSegment curve => new[] { curve.Point1, curve.Point2, curve.Point3 },
            _ => Array.Empty<Point>()
        })).ToArray();
    }

    private static void AnimateElements(Canvas host, Dictionary<string, ElementFrame> old,
        Storyboard storyboard, List<(Canvas Host, FrameworkElement Element)> departing)
    {
        foreach (var element in host.Children.OfType<FrameworkElement>().ToArray())
        {
            if (element.Tag is not string key || !old.Remove(key, out var previous))
            {
                AddDisplayAnimation(storyboard, element, "Opacity", 0, element.Opacity, 260);
                continue;
            }
            AddDisplayAnimation(storyboard, element, "Opacity", previous.Opacity, element.Opacity, 260);
            if (element is Path { Data: PathGeometry geometry } && previous.Points is { } points)
            {
                var targets = new List<(DependencyObject Target, string Property)> { (geometry.Figures[0], "StartPoint") };
                foreach (var segment in geometry.Figures[0].Segments)
                    if (segment is LineSegment line) targets.Add((line, "Point"));
                    else if (segment is BezierSegment curve)
                    {
                        targets.Add((curve, "Point1")); targets.Add((curve, "Point2")); targets.Add((curve, "Point3"));
                    }
                var next = GeometryPoints(geometry);
                if (points.Length == next.Length)
                    for (int i = 0; i < points.Length; i++)
                    {
                        var animation = new PointAnimation { From = points[i], To = next[i],
                            Duration = TimeSpan.FromMilliseconds(320), EnableDependentAnimation = true,
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                        Storyboard.SetTarget(animation, targets[i].Target);
                        Storyboard.SetTargetProperty(animation, targets[i].Property);
                        storyboard.Children.Add(animation);
                    }
            }
            else
            {
                double left = double.IsNaN(Canvas.GetLeft(element)) ? 0 : Canvas.GetLeft(element);
                double top = double.IsNaN(Canvas.GetTop(element)) ? 0 : Canvas.GetTop(element);
                AddDisplayAnimation(storyboard, element, "(Canvas.Left)", previous.Left, left, 320);
                AddDisplayAnimation(storyboard, element, "(Canvas.Top)", previous.Top, top, 320);
                if (element is Border && double.IsFinite(element.Width) && double.IsFinite(element.Height))
                {
                    AddDisplayAnimation(storyboard, element, "Width", previous.Width, element.Width, 320);
                    AddDisplayAnimation(storyboard, element, "Height", previous.Height, element.Height, 320);
                }
            }
        }
        foreach (var frame in old.Values)
        {
            frame.Element.Tag = null; // Departures cannot be mistaken for live nodes on interruption.
            frame.Element.IsHitTestVisible = false;
            Canvas.SetLeft(frame.Element, frame.Left);
            Canvas.SetTop(frame.Element, frame.Top);
            if (frame.Element is Path { Data: PathGeometry departingGeometry } && frame.Points is { } departingPoints)
            {
                var figure = departingGeometry.Figures[0];
                int pointIndex = 0;
                figure.StartPoint = departingPoints[pointIndex++];
                foreach (var segment in figure.Segments)
                    if (segment is LineSegment line) line.Point = departingPoints[pointIndex++];
                    else if (segment is BezierSegment curve)
                    {
                        curve.Point1 = departingPoints[pointIndex++];
                        curve.Point2 = departingPoints[pointIndex++];
                        curve.Point3 = departingPoints[pointIndex++];
                    }
            }
            host.Children.Add(frame.Element);
            departing.Add((host, frame.Element));
            AddDisplayAnimation(storyboard, frame.Element, "Opacity", frame.Opacity, 0, 180);
        }
    }

    private static void AddDisplayAnimation(Storyboard storyboard, DependencyObject target, string property,
        double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(milliseconds), EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static string DisplayKey(DisplayView[] displays, int index)
    {
        static string Identity(DisplayView display) => string.Join("\u001f", display.Endpoint.DeviceName,
            display.Endpoint.TargetName, display.Endpoint.Connector);
        string identity = Identity(displays[index]);
        int occurrence = displays.Take(index).Count(display => Identity(display) == identity);
        return identity + "\u001f" + occurrence;
    }

    private void RebuildDisplayCards(DisplayView[] displays)
    {
        ActiveDisplaysHost.Children.Clear();
        if (displays.Length == 0)
        {
            ActiveDisplaysHost.Children.Add(new TextBlock { Text = "活动屏幕未读回", FontSize = 13, Opacity = 0.64 });
            return;
        }
        int columns = displays.Length <= 4 ? displays.Length : Math.Min(3, (int)Math.Ceiling(displays.Length / 2d));
        int rows = (int)Math.Ceiling((double)displays.Length / columns);
        const double gap = 8;
        const double inset = 3;
        double cardWidth = (544 - 2 * inset - gap * (columns - 1)) / columns;
        double cardHeight = (105 - 2 * inset - gap * (rows - 1)) / rows;
        for (int index = 0; index < displays.Length; index++)
        {
            var card = BuildDisplayCard(displays[index], index, cardWidth, cardHeight,
                index == 0 ? mainWallpaperPath : null);
            card.Tag = DisplayKey(displays, index);
            Canvas.SetLeft(card, inset + (index % columns) * (cardWidth + gap));
            Canvas.SetTop(card, inset + (index / columns) * (cardHeight + gap));
            ActiveDisplaysHost.Children.Add(card);
        }
    }

    private Border BuildDisplayCard(DisplayView display, int index, double width, double height,
        string? wallpaperPath)
    {
        string role = index == 0 ? "主屏" : index == 1 ? "副屏" : $"屏幕 {index + 1}";
        string kind = display.Endpoint.IsInternal ? "笔记本内屏" : "外接显示器";
        string resolution = display.Monitor.Width > 0 ? $"{display.Monitor.Width} × {display.Monitor.Height}" : "分辨率未读回";
        bool compact = width < 190 || height < 70;
        bool integrated = IsIntegratedAdapter(display.Endpoint.AdapterName);
        Color accent = IsDiscreteAdapter(display.Endpoint.AdapterName) ? BrandColor(gpuBrands.Discrete) :
            integrated ? BrandColor(gpuBrands.Integrated) : Muted;
        var card = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, accent.R, accent.G, accent.B)),
            Background = (Brush)Application.Current.Resources["PrototypeInsetCardBrush"]
        };
        var content = new Grid
        {
            Padding = new Thickness(compact ? 3 : 8, 5, compact ? 3 : 8, 5),
            Width = width > 400 ? 390 : width - 2,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        double imageWidth = compact ? 45 : width > 400 ? 180 : displaysImageWidth(width);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(imageWidth) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var screenImage = BuildDeviceIllustration(display.Endpoint.IsInternal, index,
            imageWidth - 3, Math.Min(height - 8, compact ? 43 : 90), wallpaperPath);
        content.Children.Add(screenImage);
        var details = new StackPanel { Spacing = compact ? 0 : 2, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(compact ? 2 : 4, 0, 0, 0) };
        var roleText = new TextBlock
        {
            Text = $"{role} · {kind}", FontSize = compact ? 10 : 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var outputText = new TextBlock
        {
            Text = IsDiscreteAdapter(display.Endpoint.AdapterName) ? "dGPU 扫描输出" :
                integrated && display.Endpoint.IsInternal && effectiveMode == MuxMode.Hybrid
                    ? "混合输出 · dGPU ↝ iGPU"
                    : integrated ? "iGPU 扫描输出" : "扫描输出适配器未识别",
            FontSize = compact ? 9 : 10, FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(accent),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var resolutionText = new TextBlock
        {
            Text = resolution, FontSize = compact ? 10 : 14, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        details.Children.Add(roleText);
        details.Children.Add(outputText);
        details.Children.Add(resolutionText);
        if (height >= 85) details.Children.Add(new TextBlock
        {
            Text = $"{display.Endpoint.Connector} · {display.Endpoint.TargetName}",
            FontSize = 10, Opacity = 0.72, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (width < 150)
        {
            content.ColumnDefinitions.Clear();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (screenImage is FrameworkElement illustration)
            {
                illustration.HorizontalAlignment = HorizontalAlignment.Center;
                illustration.Height = 29;
            }
            Grid.SetRow(details, 1);
            details.Margin = new Thickness(3, 0, 3, 0);
        }
        else Grid.SetColumn(details, 1);
        content.Children.Add(details);
        card.Child = content;
        string pictureSource = index == 0 && wallpaperPath is not null ? "主屏系统壁纸" : "内置示意图片";
        string description = $"{role}，{kind}，{display.Endpoint.TargetName}，{resolution}，{display.Endpoint.Connector}，{display.Endpoint.AdapterName}，{pictureSource}";
        if (integrated && display.Endpoint.IsInternal && effectiveMode == MuxMode.Hybrid)
            description += "。内屏由 iGPU 输出；dGPU 可参与混合渲染，当前是否正在转送未读回。";
        ToolTipService.SetToolTip(card, description);
        AutomationProperties.SetName(card, description);
        return card;
    }

    private static double displaysImageWidth(double width) => Math.Min(118, Math.Max(64, width * 0.36));

    private void RebuildTopology(DisplayView[] displays)
    {
        TopologyHost.Children.Clear();
        if (displays.Length == 0)
        {
            AddAt(TopologyHost, new TextBlock { Text = "当前输出路径未读回", FontSize = 12, Opacity = 0.65 }, 8, 38);
            return;
        }

        var ordered = displays.Select((display, index) => (display, index))
            .OrderBy(item => IsDiscreteAdapter(item.display.Endpoint.AdapterName) ? 0 : 1)
            .ThenBy(item => item.index).Take(4).ToArray();
        double[] targetYs = EndpointPositions(ordered.Length);
        bool singleHybrid = ordered.Length == 1 && effectiveMode == MuxMode.Hybrid &&
            IsIntegratedAdapter(ordered[0].display.Endpoint.AdapterName);
        bool hasDiscrete = displays.Any(display => IsDiscreteAdapter(display.Endpoint.AdapterName));
        bool hasIntegrated = displays.Any(display => IsIntegratedAdapter(display.Endpoint.AdapterName));
        for (int i = 0; i < ordered.Length; i++)
        {
            var (display, index) = ordered[i];
            string key = DisplayKey(displays, index);
            bool discrete = IsDiscreteAdapter(display.Endpoint.AdapterName);
            bool integrated = IsIntegratedAdapter(display.Endpoint.AdapterName);
            Color color = discrete ? BrandColor(gpuBrands.Discrete) : integrated ? BrandColor(gpuBrands.Integrated) : Muted;
            double sourceY = discrete ? 36.5 : 94.5;
            double targetY = targetYs[i];
            double endpointX = display.Endpoint.IsInternal ? 409.5 : 408;
            var routeClip = new RectangleGeometry { Rect = new Rect(144, 0, endpointX - 144, 140) };
            TopologyHost.Children.Add(new Path
            {
                Data = RouteGeometry(sourceY, targetY, endpointX, singleHybrid), Stroke = new SolidColorBrush(color),
                Tag = key + ":glow",
                StrokeThickness = 10, Opacity = 0.17, Clip = routeClip,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat
            });
            TopologyHost.Children.Add(new Path
            {
                Data = RouteGeometry(sourceY, targetY, endpointX, singleHybrid), Stroke = new SolidColorBrush(color),
                Tag = key + ":route",
                StrokeThickness = 2.7,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat
            });
            var endpointIcon = (FrameworkElement)EndpointIcon(display.Endpoint.IsInternal, color);
            endpointIcon.Tag = key + ":icon";
            AddAt(TopologyHost, endpointIcon, 408, targetY - 9);
            string role = index == 0 ? "主屏" : index == 1 ? "副屏" : $"屏幕 {index + 1}";
            string name = display.Endpoint.IsInternal ? "内屏" : "外接";
            string resolution = display.Monitor.Width > 0 ? $"{display.Monitor.Width}×{display.Monitor.Height}" : "分辨率未读回";
            var label = new StackPanel { Width = 102, Spacing = 0, Tag = key + ":label" };
            label.Children.Add(new TextBlock
            {
                Text = $"{role} · {name}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            label.Children.Add(new TextBlock { Text = $"{display.Endpoint.Connector}  {resolution}", FontSize = 10,
                Opacity = 0.74, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTipService.SetToolTip(label, $"{role} · {display.Endpoint.TargetName} · {display.Endpoint.Connector} · {resolution}");
            AddAt(TopologyHost, label, 431, Math.Max(0, targetY - 13));
        }

        // Windows reports scanout ownership, not which GPU rendered each frame.
        // Keep the curved transfer as a non-animated capability hint, not live telemetry.
        if (hasIntegrated && !string.IsNullOrWhiteSpace(gpuBrands.Discrete) && effectiveMode == MuxMode.Hybrid)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gradient.GradientStops.Add(new GradientStop { Color = BrandColor(gpuBrands.Discrete), Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = BrandColor(gpuBrands.Integrated), Offset = 1 });
            var transfer = new Path
            {
                Data = TransferGeometry(singleHybrid ? targetYs[0] : 94.5), Stroke = gradient, StrokeThickness = 2.7, Tag = "transfer",
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            ToolTipService.SetToolTip(transfer, "混合输出路径提示：独显画面可能经核显转送；系统仅确认扫描输出适配器，实际渲染与转送活动未读回");
            TopologyHost.Children.Add(transfer);
        }
        string discreteName = gpuBrands.Discrete == "NVIDIA" && !string.IsNullOrWhiteSpace(gpuName)
            ? CompactGpuName(gpuName) : gpuBrands.Discrete;
        TopologyHost.Children.Add(SourceNode("dGPU", discreteName, gpuBrands.Discrete,
            BrandColor(gpuBrands.Discrete), hasDiscrete, 15.5));
        TopologyHost.Children.Add(SourceNode("iGPU", gpuBrands.Integrated, gpuBrands.Integrated,
            BrandColor(gpuBrands.Integrated), hasIntegrated, 73.5));
    }

    private static double[] EndpointPositions(int count) => count switch
    {
        1 => [65.5], 2 => [36.5, 94.5], 3 => [22, 65.5, 109], _ => [17.5, 49.5, 81.5, 113.5]
    };

    private static PathGeometry RouteGeometry(double sourceY, double targetY, double endpointX, bool singleHybrid)
    {
        var figure = new PathFigure { StartPoint = new Point(144, sourceY) };
        figure.Segments.Add(new LineSegment { Point = new Point(190, sourceY) });
        double trunkY = singleHybrid ? targetY : sourceY;
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(226, sourceY), Point2 = new Point(254, trunkY), Point3 = new Point(286, trunkY)
        });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(320, trunkY), Point2 = new Point(344, targetY), Point3 = new Point(378, targetY)
        });
        figure.Segments.Add(new LineSegment { Point = new Point(endpointX, targetY) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry TransferGeometry(double mergeY)
    {
        var figure = new PathFigure { StartPoint = new Point(144, 36.5) };
        figure.Segments.Add(new LineSegment { Point = new Point(190, 36.5) });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(226, 36.5), Point2 = new Point(254, mergeY), Point3 = new Point(286, mergeY)
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Border SourceNode(string label, string detail, string brand, Color color, bool active, double top)
    {
        var glyph = BrandGlyph(brand, color);
        if (glyph is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromArgb(active ? (byte)56 : (byte)42, color.R, color.G, color.B)),
            Child = glyph
        });
        panel.Children.Add(new Border
        {
            Width = 1, Height = 27, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb((byte)140, color.R, color.G, color.B))
        });
        var text = new StackPanel { Spacing = 0, Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 11.5,
            FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(detail) ? "未识别" : detail,
            FontSize = 10, Opacity = 0.82, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 70 });
        panel.Children.Add(text);
        var border = new Border
        {
            Tag = label, Width = 126, Height = 42, CornerRadius = new CornerRadius(9), Padding = new Thickness(1),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(active ? (byte)170 : (byte)100, color.R, color.G, color.B)),
            Background = new SolidColorBrush(Color.FromArgb(180, 14, 20, 24)), Child = panel,
            Opacity = string.IsNullOrEmpty(brand) ? 0.45 : 1
        };
        Canvas.SetLeft(border, 18);
        Canvas.SetTop(border, top);
        ToolTipService.SetToolTip(border, string.IsNullOrEmpty(brand) ? "物理显卡品牌未识别" :
            active ? $"{detail} · 当前承担扫描输出" : $"{detail} · 当前未承担扫描输出");
        return border;
    }

    private static Color BrandColor(string brand) => brand switch
    {
        "NVIDIA" => Green,
        "AMD" => AmdRed,
        "Intel" => IntelBlue,
        _ => Muted
    };

    private static string CompactGpuName(string name) =>
        name.Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Laptop GPU", "", StringComparison.OrdinalIgnoreCase);

    private static UIElement BrandGlyph(string brand, Color color)
    {
        string? file = brand switch
        {
            "NVIDIA" => "NvidiaEyeWhite.svg",
            "AMD" => "AmdArrow.svg",
            "Intel" => "IntelLogo.svg",
            _ => null
        };
        if (file is null) return SourceGlyph(false, color);
        var uri = new Uri($"ms-appx:///Assets/GpuRoute/{file}");
        return new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform,
            Source = file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(uri) : new BitmapImage(uri) };
    }

    private static UIElement SourceGlyph(bool discrete, Color color)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var brush = new SolidColorBrush(color);
        if (discrete)
        {
            var outline = new PathFigure { StartPoint = new Point(2, 12) };
            outline.Segments.Add(new BezierSegment { Point1 = new Point(6, 5), Point2 = new Point(18, 5), Point3 = new Point(22, 12) });
            outline.Segments.Add(new BezierSegment { Point1 = new Point(18, 19), Point2 = new Point(6, 19), Point3 = new Point(2, 12) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(outline);
            canvas.Children.Add(new Path { Data = geometry, Stroke = brush, StrokeThickness = 1.9 });
            AddAt(canvas, new Ellipse { Width = 9, Height = 9, Stroke = brush, StrokeThickness = 1.7 }, 7.5, 7.5);
            AddAt(canvas, new Ellipse { Width = 3.5, Height = 3.5, Fill = brush }, 10.3, 10.3);
            AddAt(canvas, new Rectangle { Width = 4, Height = 2, Fill = brush }, 17, 7);
        }
        else
        {
            AddAt(canvas, new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(2),
                BorderBrush = brush, BorderThickness = new Thickness(1.8) }, 4, 4);
            AddAt(canvas, new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(1),
                BorderBrush = brush, BorderThickness = new Thickness(1.3) }, 8.5, 8.5);
            foreach (double pin in new[] { 7d, 12d, 17d })
            {
                canvas.Children.Add(VectorLine(pin, 1, pin, 4, color, 1.4));
                canvas.Children.Add(VectorLine(pin, 20, pin, 23, color, 1.4));
                canvas.Children.Add(VectorLine(1, pin, 4, pin, color, 1.4));
                canvas.Children.Add(VectorLine(20, pin, 23, pin, color, 1.4));
            }
        }
        return canvas;
    }

    private static UIElement EndpointIcon(bool internalDisplay, Color routeColor)
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        var white = new SolidColorBrush(Color.FromArgb(255, 245, 247, 249));
        double left = internalDisplay ? 1.5 : 0;
        double top = internalDisplay ? 1 : 0;
        double width = internalDisplay ? 15 : 18;
        double height = internalDisplay ? 11 : 12;
        // Faint outer falloff leaves both the screen cutout and its white frame intact.
        for (int spread = 3; spread >= 1; spread--)
        {
            var glow = Color.FromArgb((byte)(40 / (1 << (spread - 1))), routeColor.R, routeColor.G, routeColor.B);
            AddAt(canvas, new Border
            {
                Width = width + spread * 2, Height = height + spread * 2,
                CornerRadius = new CornerRadius(1.5 + spread), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(glow)
            }, left - spread, top - spread);
        }
        if (internalDisplay)
        {
            AddAt(canvas, new Border { Width = 15, Height = 11, CornerRadius = new CornerRadius(1),
                BorderBrush = white, BorderThickness = new Thickness(1.4) }, 1.5, 1);
            canvas.Children.Add(VectorLine(1, 13, 17, 13, Color.FromArgb(255, 245, 247, 249), 1.4));
            canvas.Children.Add(VectorLine(3, 11.5, 1, 13, Color.FromArgb(255, 245, 247, 249), 1.4));
            canvas.Children.Add(VectorLine(15, 11.5, 17, 13, Color.FromArgb(255, 245, 247, 249), 1.4));
            return canvas;
        }
        AddAt(canvas, new Border { Width = 18, Height = 12, CornerRadius = new CornerRadius(1.5),
            BorderBrush = white, BorderThickness = new Thickness(1.4) }, 0, 0);
        AddAt(canvas, new Rectangle { Width = 3, Height = 3, Fill = white }, 7.5, 12);
        AddAt(canvas, new Border { Width = 11, Height = 2, CornerRadius = new CornerRadius(1),
            Background = white }, 3.5, 15);
        return canvas;
    }

    private static Line VectorLine(double x1, double y1, double x2, double y2, Color color, double width) => new()
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        Stroke = new SolidColorBrush(color), StrokeThickness = width,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
    };

    private static readonly string[] BuiltinWallpapers =
        ["WallpaperAlpine.png", "WallpaperCity.png", "WallpaperForest.png", "WallpaperDesert.png"];

    private static UIElement BuildDeviceIllustration(bool internalDisplay, int index, double width, double height,
        string? wallpaperPath)
    {
        var scene = new Canvas { Width = 120, Height = 80 };
        AddAt(scene, new Image
        {
            Width = 120, Height = 80, Stretch = Stretch.Fill,
            Source = Asset(internalDisplay ? "LaptopNeutralFrame.png" : "MonitorNeutralFrame.png")
        }, 0, 0);

        double left = internalDisplay ? 20.2 : 11;
        double top = internalDisplay ? 9.3 : 7.4;
        double screenWidth = internalDisplay ? 79.5 : 98;
        double screenHeight = internalDisplay ? 43.1 : 48.7;
        var wallpaper = new Image
        {
            Width = screenWidth, Height = screenHeight, Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, screenWidth, screenHeight) },
            Source = Asset(BuiltinWallpapers[Math.Max(0, index - 1) % BuiltinWallpapers.Length])
        };
        AddAt(scene, wallpaper, left, top);
        if (index == 0 && wallpaperPath is not null)
            _ = LoadSystemWallpaperAsync(wallpaper, wallpaperPath);
        return new Viewbox { Width = width, Height = height, Stretch = Stretch.Uniform, Child = scene,
            VerticalAlignment = VerticalAlignment.Center };
    }

    private static async Task LoadSystemWallpaperAsync(Image target, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 512 };
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"系统壁纸读取失败：{exception.GetType().Name}");
        }
    }

    private static BitmapImage Asset(string fileName) =>
        new(new Uri($"ms-appx:///Assets/GpuRoute/{fileName}"));

    private static void AddAt(Canvas parent, UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        parent.Children.Add(element);
    }

    private bool IsDiscreteAdapter(string name)
    {
        string vendor = AdapterVendor(name);
        return vendor.Length > 0 && vendor == gpuBrands.Discrete && vendor != gpuBrands.Integrated;
    }

    private bool IsIntegratedAdapter(string name)
    {
        string vendor = AdapterVendor(name);
        return vendor.Length > 0 && vendor == gpuBrands.Integrated && vendor != gpuBrands.Discrete;
    }

    private static string AdapterVendor(string name) => name switch
    {
        "NVIDIA dGPU" => "NVIDIA",
        "AMD GPU" => "AMD",
        "Intel GPU" => "Intel",
        _ => ""
    };

    private static List<MonitorSnapshot> EnumerateMonitors()
    {
        List<MonitorSnapshot> result = [];
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = string.Empty };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
            bool modeRead = EnumDisplaySettings(info.DeviceName, -1, ref mode);
            result.Add(new(info.DeviceName,
                modeRead ? mode.Width : info.Monitor.Right - info.Monitor.Left,
                modeRead ? mode.Height : info.Monitor.Bottom - info.Monitor.Top,
                (info.Flags & 1) != 0, info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right, info.Monitor.Bottom));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private void OnHybridClick(object sender, RoutedEventArgs e) => ModeRequested?.Invoke(this, MuxMode.Hybrid);
    private void OnDiscreteClick(object sender, RoutedEventArgs e) => ModeRequested?.Invoke(this, MuxMode.Discrete);
    private readonly record struct MonitorSnapshot(string DeviceName, int Width, int Height, bool Primary,
        int Left, int Top, int Right, int Bottom);
    private readonly record struct DisplayView(WindowsDisplayRouteReader.ActiveDisplayEndpoint Endpoint, MonitorSnapshot Monitor);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DisplayMode mode);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; }
    [StructLayout(LayoutKind.Explicit, Size = 220)] private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
    }
}
