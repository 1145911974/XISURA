using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class ModeAmbientBackdrop : UserControl
{
    private readonly Dictionary<string, CachedPlate> plates = [];
    private Storyboard? backgroundStoryboard;
    private Storyboard? tierStoryboard;
    private ModeTransitionPlan? pendingPlan;
    private TurboBackgroundIntensity turboIntensity = TurboBackgroundIntensity.Resolve("Normal");
    private long latestVersion;
    private long tierTransitionVersion;
    private bool motionPaused;
    private bool decorationsVisible = true;

    public ModeAmbientBackdrop()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public void ApplyTransition(ModeTransitionPlan plan)
    {
        latestVersion = plan.Version;
        pendingPlan = plan;

        if (CurrentPlate.Source is null)
            CurrentPlate.Source = Plate(plan.Current ?? plan.Target).Image;

        var target = Plate(plan.Target);
        PreserveVisiblePlate();
        TargetPlate.Source = target.Image;
        TargetPlate.Opacity = 0;
        TargetPlate.Visibility = Visibility.Visible;
        TrimPlateCache();
        if (!target.IsReady)
            return;

        if (plan.Snap || motionPaused || !decorationsVisible)
        {
            PromoteTarget(plan.Target);
            return;
        }

        StartTransition(plan, target);
    }

    private void StartTransition(ModeTransitionPlan plan, CachedPlate target)
    {
        PreserveVisiblePlate();
        TargetPlate.Source = target.Image;
        TargetPlate.Opacity = 0;
        TargetPlate.Visibility = Visibility.Visible;

        var duration = plan.Duration;
        backgroundStoryboard = new Storyboard();
        // Keep full coverage while the new plate is composited over the old one.
        CurrentPlate.Opacity = 1;
        AddOpacity(backgroundStoryboard, TargetPlate, 0, 1, duration);
        var version = plan.Version;
        var storyboard = backgroundStoryboard;
        storyboard.Completed += (_, _) =>
        {
            if (version != latestVersion || !ReferenceEquals(backgroundStoryboard, storyboard)) return;
            PromoteTarget(plan.Target);
        };
        storyboard.Begin();
    }

    private void PreserveVisiblePlate()
    {
        var visible = TargetPlate.Source is not null && TargetPlate.Opacity >= .5
            ? TargetPlate.Source
            : CurrentPlate.Source;
        backgroundStoryboard?.Stop();
        backgroundStoryboard = null;
        if (visible is not null) CurrentPlate.Source = visible;
        CurrentPlate.Opacity = 1;
        CurrentPlate.Visibility = Visibility.Visible;
        TargetPlate.Source = null;
        TargetPlate.Opacity = 0;
    }

    public void SetMotionPaused(bool paused)
    {
        if (motionPaused == paused) return;
        motionPaused = paused;
        if (paused && pendingPlan is { } plan && Plate(plan.Target).IsReady)
            PromoteTarget(plan.Target);
    }

    public void SetDecorationsVisible(bool visible)
    {
        decorationsVisible = visible;
        DecorativeRoot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && pendingPlan is { } plan && Plate(plan.Target).IsReady)
            PromoteTarget(plan.Target);
    }

    public void SetTurboTier(bool active, string tier, TimeSpan duration)
    {
        ++tierTransitionVersion;
        turboIntensity = TurboBackgroundIntensity.Resolve(tier);
        var shade = active ? turboIntensity.ShadeOpacity : 0;
        var glow = active ? turboIntensity.GlowOpacity : 0;
        var currentShade = TurboTierShade.Opacity;
        var currentGlow = TurboTierGlow.Opacity;
        tierStoryboard?.Stop();

        if (duration <= TimeSpan.Zero)
        {
            TurboTierShade.Opacity = shade;
            TurboTierGlow.Opacity = glow;
            return;
        }

        var version = tierTransitionVersion;
        tierStoryboard = new Storyboard();
        AddOpacity(tierStoryboard, TurboTierShade, currentShade, shade, duration);
        AddOpacity(tierStoryboard, TurboTierGlow, currentGlow, glow, duration);
        tierStoryboard.Completed += (_, _) =>
        {
            if (version != tierTransitionVersion) return;
            TurboTierShade.Opacity = shade;
            TurboTierGlow.Opacity = glow;
        };
        tierStoryboard.Begin();
    }

    private void PromoteTarget(ModeVisualScene target)
    {
        backgroundStoryboard?.Stop();
        backgroundStoryboard = null;
        pendingPlan = null;
        CurrentPlate.Source = Plate(target).Image;
        CurrentPlate.Opacity = 1;
        CurrentPlate.Visibility = Visibility.Visible;
        TargetPlate.Source = null;
        TargetPlate.Opacity = 0;
        TargetPlate.Visibility = Visibility.Visible;
        TrimPlateCache();
    }

    private static void AddOpacity(Storyboard storyboard, DependencyObject target, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EnableDependentAnimation = false,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private CachedPlate Plate(ModeVisualScene scene)
    {
        if (plates.TryGetValue(scene.Key, out var cached)) return cached;
        var image = new BitmapImage();
        var plate = new CachedPlate(scene, image);
        plates.Add(scene.Key, plate);
        image.ImageOpened += OnPlateOpened;
        image.ImageFailed += OnPlateFailed;
        image.UriSource = new Uri(scene.PlateAsset);
        return plate;
    }

    private void TrimPlateCache()
    {
        // Keep displayed sources intact; a rapid retarget briefly admits a third plate.
        foreach (var pair in plates.Where(pair =>
                     !ReferenceEquals(pair.Value.Image, CurrentPlate.Source) &&
                     !ReferenceEquals(pair.Value.Image, TargetPlate.Source)).ToArray())
        {
            plates.Remove(pair.Key);
            pair.Value.Image.ImageOpened -= OnPlateOpened;
            pair.Value.Image.ImageFailed -= OnPlateFailed;
        }
    }

    private void OnPlateFailed(object sender, ExceptionRoutedEventArgs error)
    {
        var plate = plates.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Image, sender));
        if (plate is null) return;
        plate.Failed = true;
        Debug.WriteLine($"Mode background failed to decode '{plate.Scene.Key}': {error.ErrorMessage}");
    }

    private void OnPlateOpened(object sender, RoutedEventArgs args)
    {
        var plate = plates.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Image, sender));
        if (plate is null) return;
        plate.IsReady = true;
        if (pendingPlan is not { } plan ||
            plan.Version != latestVersion ||
            plan.Target.Key != plate.Scene.Key)
            return;

        if (plan.Snap || motionPaused || !decorationsVisible)
            PromoteTarget(plan.Target);
        else
            StartTransition(plan, plate);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        backgroundStoryboard?.Stop();
        tierStoryboard?.Stop();
        TrimPlateCache();
    }

    private sealed class CachedPlate(ModeVisualScene scene, BitmapImage image)
    {
        public ModeVisualScene Scene { get; } = scene;
        public BitmapImage Image { get; } = image;
        public bool IsReady { get; set; }
        public bool Failed { get; set; }
    }
}
