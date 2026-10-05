using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Branding;

public sealed partial class WaveLogoControl : UserControl
{
    private static readonly string[] GradientIds = ["jlw-upper-gradient", "jlw-lower-gradient",
        "upper-sheen", "lower-sheen", "jlw-rear-left-gradient", "jlw-rear-right-gradient", "jlw-front-gradient"];
    private static readonly string[] ModeNames = ["Office", "Gaming", "Turbo", "Custom1", "Custom2", "Custom3"];
    private static readonly Lazy<MaterialSource[]> Materials = new(() => ModeNames
        .Select(mode => ReadMaterial(Path.Combine("CModeIcons", $"JiaolongC{mode}-Home.svg")))
        .Append(ReadMaterial("JiaolongWaveApp.svg")).ToArray());
    private readonly LogoColorTransition transition = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly MotionSettingsService motionSettings = new();
    private readonly UISettings systemColors = new();
    private CanvasControl? canvas;
    private CanvasSvgDocument? document;
    private CanvasCommandList? shadowMask;
    private ShadowEffect? edgeShadow;
    private CanvasSvgNamedElement[] gradientStops = [];
    private XamlRoot? observedRoot;
    private ControlModeId? confirmedMode;
    private bool subscribed;
    private bool hasPresented;
    private bool documentUsesModeMaterial;
    private bool reduceMotion;
    private bool highContrast;
    private string contrastForeground = "";
    private string contrastBackground = "";
    public bool SoftBackground { get; set; }

    private sealed record MaterialSource(string Svg, string[] StopIds, Windows.UI.Color[] Colors, float[] Opacities);

    private static MaterialSource ReadMaterial(string asset)
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", asset));
        var ids = new List<string>();
        var colors = new List<Windows.UI.Color>();
        var opacities = new List<float>();
        foreach (string gradientId in GradientIds)
        {
            var gradient = xml.Descendants().Single(node => (string?)node.Attribute("id") == gradientId);
            int index = 0;
            foreach (var stop in gradient.Elements())
            {
                string id = $"{gradientId}-stop-{index++}";
                ids.Add(id);
                stop.SetAttributeValue("id", id);
                uint rgb = uint.Parse(((string)stop.Attribute("stop-color")!)[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                colors.Add(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                opacities.Add(float.Parse((string?)stop.Attribute("stop-opacity") ?? "1", CultureInfo.InvariantCulture));
            }
        }
        return new(xml.ToString(SaveOptions.DisableFormatting), ids.ToArray(), colors.ToArray(), opacities.ToArray());
    }

    public WaveLogoControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => canvas?.Invalidate();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => RefreshVisibility());
    }

    public bool ReduceMotion
    {
        get => reduceMotion;
        set
        {
            reduceMotion = value;
            if (value) SnapToConfirmedState();
        }
    }

    public void SetState(ControlModeId? mode, bool animate = true)
    {
        bool hadConfirmedMode = confirmedMode is not null;
        confirmedMode = mode is { } value && Enum.IsDefined(value) ? value : null;
        // Fixed fallback and C runtime materials have different gradient directions and glow positions.
        if (document is not null && canvas is not null && documentUsesModeMaterial != (confirmedMode is not null))
        {
            LoadPresentationDocument(canvas);
            return;
        }
        motionSettings.Refresh();
        double seconds = animate && hadConfirmedMode && hasPresented && IsPresentationVisible() &&
            !ReduceMotion && !highContrast && !motionSettings.IsReducedMotionEnabled ? .42 : 0;
        if (confirmedMode is { } known)
            // Draw advances these weights; interruption starts at the last presented frame.
            transition.Select(ModeIndex(known), clock.Elapsed.TotalSeconds, seconds, advance: false);
        else
        {
            transition.Select(0, clock.Elapsed.TotalSeconds, 0);
            seconds = 0;
        }
        if (seconds == 0 || !transition.IsActive || document is null) StopFrames();
        else if (!subscribed)
        {
            CompositionTarget.Rendering += OnFrame;
            subscribed = true;
        }
        canvas?.Invalidate();
    }

    public void SetHighContrast(bool enabled)
    {
        string foreground = enabled ? ToHex(systemColors.GetColorValue(UIColorType.Foreground)) : "";
        string background = enabled ? ToHex(systemColors.GetColorValue(UIColorType.Background)) : "";
        if (highContrast == enabled && foreground == contrastForeground && background == contrastBackground) return;
        highContrast = enabled;
        contrastForeground = foreground;
        contrastBackground = background;
        hasPresented = false;
        SnapToConfirmedState();
        if (canvas is { } loaded && document is not null) LoadPresentationDocument(loaded);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (canvas is not null) return;
        observedRoot = XamlRoot;
        if (observedRoot is not null) observedRoot.Changed += OnRootChanged;
        canvas = new CanvasControl { ClearColor = Colors.Transparent, IsHitTestVisible = false };
        canvas.CreateResources += CreateResources;
        canvas.Draw += Draw;
        WaveCanvasHost.Children.Add(canvas);
        SetState(confirmedMode, animate: false);
    }

    private void CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
        => LoadPresentationDocument(sender);

    private void LoadPresentationDocument(CanvasControl sender)
    {
        DisposeDocument();
        documentUsesModeMaterial = confirmedMode is not null;
        document = CanvasSvgDocument.LoadFromXml(sender, PresentationSvg());
        gradientStops = Materials.Value[documentUsesModeMaterial ? 0 : 6].StopIds.Select(document.FindElementById).ToArray();
        hasPresented = false;
        SetState(confirmedMode, animate: false);
    }

    private string PresentationSvg()
    {
        string source = Materials.Value[documentUsesModeMaterial ? 0 : 6].Svg;
        if (!highContrast) return source;
        var xml = XDocument.Parse(source);
        foreach (var stop in xml.Descendants().Where(node => node.Name.LocalName == "stop"))
        {
            string gradient = (string?)stop.Parent?.Attribute("id") ?? "";
            bool wave = gradient.Contains("rear-left-gradient", StringComparison.Ordinal) ||
                gradient.Contains("rear-right-gradient", StringComparison.Ordinal) ||
                gradient.Contains("front-gradient", StringComparison.Ordinal);
            stop.SetAttributeValue("stop-color", wave ? contrastForeground : contrastBackground);
            if (gradient is "upper-sheen" or "lower-sheen") stop.SetAttributeValue("stop-opacity", 0);
        }
        return xml.ToString(SaveOptions.DisableFormatting);
    }

    private static string ToHex(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (document is null || sender.ActualWidth <= 0 || sender.ActualHeight <= 0) return;
        if (transition.IsActive) transition.Advance(clock.Elapsed.TotalSeconds);
        if (!highContrast)
        {
            for (int stop = 0; stop < gradientStops.Length; stop++)
            {
                var color = Materials.Value[6].Colors[stop];
                double opacity = Materials.Value[6].Opacities[stop];
                if (confirmedMode is not null)
                {
                    double red = 0, green = 0, blue = 0;
                    opacity = 0;
                    for (int mode = 0; mode < 6; mode++)
                    {
                        var sample = Materials.Value[mode].Colors[stop];
                        red += sample.R * transition.Weights[mode];
                        green += sample.G * transition.Weights[mode];
                        blue += sample.B * transition.Weights[mode];
                        opacity += Materials.Value[mode].Opacities[stop] * transition.Weights[mode];
                    }
                    color = Windows.UI.Color.FromArgb(255, (byte)Math.Clamp(Math.Round(red), 0, 255),
                        (byte)Math.Clamp(Math.Round(green), 0, 255), (byte)Math.Clamp(Math.Round(blue), 0, 255));
                }
                gradientStops[stop].SetColorAttribute("stop-color", color);
                gradientStops[stop].SetFloatAttribute("stop-opacity", (float)opacity);
            }
        }
        double side = Math.Min(sender.ActualWidth, sender.ActualHeight);
        // SVG's explicit 1024-unit root size wins over a smaller DrawSvg viewport.
        args.DrawingSession.Transform = Matrix3x2.CreateScale((float)(side / 1024)) *
            Matrix3x2.CreateTranslation((float)((sender.ActualWidth - side) / 2), (float)((sender.ActualHeight - side) / 2));
        if (SoftBackground && !highContrast)
        {
            if (edgeShadow is null)
            {
                shadowMask = new CanvasCommandList(sender);
                using (var maskSession = shadowMask.CreateDrawingSession())
                    maskSession.DrawSvg(document, new Size(1024, 1024));
                edgeShadow = new ShadowEffect { Source = shadowMask, BlurAmount = 36,
                    ShadowColor = Windows.UI.Color.FromArgb(224, 0, 0, 0) };
            }
            args.DrawingSession.DrawImage(edgeShadow);
        }
        args.DrawingSession.DrawSvg(document, new Size(1024, 1024));
        hasPresented = IsPresentationVisible();
        if (!transition.IsActive) StopFrames();
    }

    private void OnFrame(object? sender, object args)
    {
        motionSettings.Refresh();
        if (!IsPresentationVisible() || ReduceMotion || motionSettings.IsReducedMotionEnabled)
        {
            SnapToConfirmedState();
            if (!IsPresentationVisible()) hasPresented = false;
            return;
        }
        canvas?.Invalidate();
        if (!transition.IsActive) StopFrames();
    }

    private bool IsPresentationVisible()
    {
        if (!IsLoaded || XamlRoot?.IsHostVisible != true) return false;
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: <= 0 }) return false;
        return true;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshVisibility();

    private void RefreshVisibility()
    {
        if (!IsPresentationVisible()) hasPresented = false;
        if (!hasPresented) SnapToConfirmedState();
    }

    private void SnapToConfirmedState()
    {
        if (confirmedMode is { } known) transition.Select(ModeIndex(known), clock.Elapsed.TotalSeconds, 0);
        StopFrames();
        canvas?.Invalidate();
    }

    private void StopFrames()
    {
        if (!subscribed) return;
        CompositionTarget.Rendering -= OnFrame;
        subscribed = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        StopFrames();
        hasPresented = false;
        if (observedRoot is not null) observedRoot.Changed -= OnRootChanged;
        observedRoot = null;
        if (canvas is { } previous)
        {
            canvas = null;
            previous.CreateResources -= CreateResources;
            previous.Draw -= Draw;
            WaveCanvasHost.Children.Clear();
            previous.RemoveFromVisualTree();
        }
        DisposeDocument();
    }

    private void DisposeDocument()
    {
        edgeShadow?.Dispose();
        edgeShadow = null;
        shadowMask?.Dispose();
        shadowMask = null;
        foreach (var stop in gradientStops) stop.Dispose();
        gradientStops = [];
        document?.Dispose();
        document = null;
    }

    private static int ModeIndex(ControlModeId mode) => mode switch
    {
        ControlModeId.Office => 0,
        ControlModeId.Gaming => 1,
        ControlModeId.Turbo => 2,
        ControlModeId.Custom1 => 3,
        ControlModeId.Custom2 => 4,
        ControlModeId.Custom3 => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
