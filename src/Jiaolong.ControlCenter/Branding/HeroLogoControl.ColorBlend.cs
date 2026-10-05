using System.Diagnostics;
using Jiaolong_ControlCenter.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Jiaolong_ControlCenter.Branding;

public sealed partial class HeroLogoControl
{
    private readonly LogoColorTransition colorBlend = new();
    private readonly Stopwatch blendClock = Stopwatch.StartNew();
    private readonly MotionSettingsService motionSettings = new();
    private CanvasControl? blendCanvas;
    private CanvasBitmap[] blendBitmaps = [];
    private bool blendSubscribed;

    private void InitializeColorBlend()
    {
        Loaded += (_, _) =>
        {
            if (blendCanvas is not null) return;
            blendCanvas = new CanvasControl { ClearColor = Colors.Transparent, IsHitTestVisible = false };
            blendCanvas.CreateResources += CreateBlendResources;
            blendCanvas.Draw += DrawBlend;
            ColorBlendHost.Children.Add(blendCanvas);
            ApplyVisualState();
        };
        Unloaded += (_, _) =>
        {
            StopBlendFrames();
            adaptiveStoryboard?.Stop();
            if (blendCanvas is { } canvas)
            {
                blendCanvas = null;
                canvas.CreateResources -= CreateBlendResources;
                canvas.Draw -= DrawBlend;
                ColorBlendHost.Children.Clear();
                canvas.RemoveFromVisualTree();
            }
            foreach (var bitmap in blendBitmaps) bitmap.Dispose();
            blendBitmaps = [];
        };
    }

    private void CreateBlendResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) =>
        args.TrackAsyncAction(LoadBlendResources(sender).AsAsyncAction());

    private async Task LoadBlendResources(CanvasControl sender)
    {
        var loaded = new List<CanvasBitmap>();
        try
        {
            foreach (var mode in new[] { "Office", "Gaming", "Turbo", "CustomProfile1", "CustomProfile2", "CustomProfile3" })
                loaded.Add(await CanvasBitmap.LoadAsync(sender,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", $"HeroLogoFull{mode}.png"), 96));
            if (!ReferenceEquals(sender, blendCanvas)) return;
            foreach (var bitmap in blendBitmaps) bitmap.Dispose();
            blendBitmaps = loaded.ToArray();
            loaded.Clear();
            ApplyVisualState();
        }
        finally
        {
            foreach (var bitmap in loaded) bitmap.Dispose();
        }
    }

    private void ApplyColorBlend()
    {
        motionSettings.Refresh();
        double seconds = ReducedMotion || motionSettings.IsReducedMotionEnabled || !IsLoaded
            ? 0 : Math.Max(0, TransitionDuration.TotalSeconds);
        colorBlend.Select(SelectedCoreIndex(), blendClock.Elapsed.TotalSeconds, seconds);
        bool ready = blendBitmaps.Length == 6;
        ColorBlendHost.Visibility = Visibility.Visible;
        var fallback = FullLogoLayers();
        for (int i = 0; i < fallback.Length; i++)
            fallback[i].Opacity = !ready && i == SelectedCoreIndex() ? 1 : 0;
        blendCanvas?.Invalidate();
        if (colorBlend.IsActive && ready && !blendSubscribed)
        {
            CompositionTarget.Rendering += OnBlendFrame;
            blendSubscribed = true;
        }
        else if (!colorBlend.IsActive) StopBlendFrames();
    }

    private void OnBlendFrame(object? sender, object args)
    {
        bool visible = IsLoaded && XamlRoot?.IsHostVisible == true;
        for (DependencyObject? parent = this; visible && parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement { Visibility: Visibility.Collapsed }) visible = false;
        if (!visible || AdaptiveCorePreview)
        {
            colorBlend.Select(SelectedCoreIndex(), blendClock.Elapsed.TotalSeconds, 0);
            StopBlendFrames();
        }
        else colorBlend.Advance(blendClock.Elapsed.TotalSeconds);
        blendCanvas?.Invalidate();
        if (!colorBlend.IsActive) StopBlendFrames();
    }

    private void StopBlendFrames()
    {
        if (!blendSubscribed) return;
        CompositionTarget.Rendering -= OnBlendFrame;
        blendSubscribed = false;
    }

    private void DrawBlend(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (blendBitmaps.Length != 6 || AdaptiveCorePreview) return;
        // Premultiplied weighted sum, NOT SourceOver: shared silver pixels retain full coverage.
        args.DrawingSession.Blend = CanvasBlend.Add;
        var destination = new Rect(0, 0, sender.ActualWidth, sender.ActualHeight);
        for (int i = 0; i < blendBitmaps.Length; i++)
            if (colorBlend.Weights[i] > 0)
                args.DrawingSession.DrawImage(blendBitmaps[i], destination,
                    new Rect(0, 0, 1254, 1254), (float)colorBlend.Weights[i], CanvasImageInterpolation.Linear);
    }
}
