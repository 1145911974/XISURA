using Jiaolong_ControlCenter.Prototype;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Jiaolong_ControlCenter.Branding;

public sealed partial class HeroLogoControl : UserControl
{
    private static readonly TimeSpan AdaptiveCycleDuration = TimeSpan.FromMilliseconds(1800);

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(PrototypePerformanceMode),
        typeof(HeroLogoControl),
        new PropertyMetadata(PrototypePerformanceMode.Office, OnVisualPropertyChanged));

    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile),
        typeof(HeroLogoProfile),
        typeof(HeroLogoControl),
        new PropertyMetadata(HeroLogoProfile.Default, OnVisualPropertyChanged));

    public static readonly DependencyProperty CustomProfileProperty = DependencyProperty.Register(
        nameof(CustomProfile),
        typeof(string),
        typeof(HeroLogoControl),
        new PropertyMetadata("Profile1", OnVisualPropertyChanged));

    public static readonly DependencyProperty TransitionDurationProperty = DependencyProperty.Register(
        nameof(TransitionDuration),
        typeof(TimeSpan),
        typeof(HeroLogoControl),
        new PropertyMetadata(TimeSpan.FromMilliseconds(420), OnVisualPropertyChanged));

    public static readonly DependencyProperty AdaptiveCorePreviewProperty = DependencyProperty.Register(
        nameof(AdaptiveCorePreview),
        typeof(bool),
        typeof(HeroLogoControl),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion),
        typeof(bool),
        typeof(HeroLogoControl),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    private Storyboard? adaptiveStoryboard;
    private bool initialized;

    public HeroLogoControl()
    {
        InitializeComponent();
        InitializeColorBlend();
        initialized = true;
        ApplyVisualState();
    }

    public PrototypePerformanceMode Mode
    {
        get => (PrototypePerformanceMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public HeroLogoProfile Profile
    {
        get => (HeroLogoProfile?)GetValue(ProfileProperty) ?? HeroLogoProfile.Default;
        set => SetValue(ProfileProperty, value);
    }

    public string CustomProfile
    {
        get => (string?)GetValue(CustomProfileProperty) ?? "Profile1";
        set => SetValue(CustomProfileProperty, value);
    }

    public TimeSpan TransitionDuration
    {
        get => (TimeSpan)GetValue(TransitionDurationProperty);
        set => SetValue(TransitionDurationProperty, value);
    }

    public bool AdaptiveCorePreview
    {
        get => (bool)GetValue(AdaptiveCorePreviewProperty);
        set => SetValue(AdaptiveCorePreviewProperty, value);
    }

    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs _) =>
        ((HeroLogoControl)sender).ApplyVisualState();

    private void ApplyVisualState()
    {
        if (!initialized) return;

        adaptiveStoryboard?.Stop();
        adaptiveStoryboard = null;
        var logos = FullLogoLayers();

        if (AdaptiveCorePreview)
        {
            StopBlendFrames();
            ColorBlendHost.Visibility = Visibility.Collapsed;
            for (var index = 0; index < logos.Length; index++)
                logos[index].Opacity = 0;
            AdaptiveLogoLayers.Opacity = 1;
            ApplyAdaptiveCoreState();
            return;
        }

        AdaptiveLogoLayers.Opacity = 0;

        ApplyColorBlend();
    }

    private void ApplyAdaptiveCoreState()
    {
        var cores = AdaptiveCoreLayers();
        for (var index = 0; index < cores.Length; index++)
            cores[index].Opacity = 0.18;

        var duration = TransitionDuration < TimeSpan.Zero ? TimeSpan.Zero : TransitionDuration;
        if (ReducedMotion || duration == TimeSpan.Zero) return;

        adaptiveStoryboard = new Storyboard
        {
            Duration = new Duration(AdaptiveCycleDuration),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        AddAdaptiveOpacityAnimation(cores[0],
            (0, .18), (.24, .24), (.52, .16), (.78, .20), (1, .18));
        AddAdaptiveOpacityAnimation(cores[1],
            (0, .18), (.18, .15), (.48, .23), (.76, .18), (1, .18));
        AddAdaptiveOpacityAnimation(cores[2],
            (0, .18), (.28, .16), (.56, .23), (.84, .17), (1, .18));
        AddAdaptiveOpacityAnimation(cores[3],
            (0, .18), (.22, .22), (.52, .15), (.78, .21), (1, .18));
        AddAdaptiveOpacityAnimation(cores[4],
            (0, .18), (.20, .16), (.50, .24), (.82, .18), (1, .18));
        AddAdaptiveOpacityAnimation(cores[5],
            (0, .18), (.30, .23), (.58, .16), (.86, .22), (1, .18));
        adaptiveStoryboard.Begin();
    }

    private void AddAdaptiveOpacityAnimation(Image layer, params (double Progress, double Value)[] points)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var point in points)
        {
            animation.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(AdaptiveCycleDuration.TotalMilliseconds * point.Progress)),
                Value = point.Value,
            });
        }

        Storyboard.SetTarget(animation, layer);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        adaptiveStoryboard!.Children.Add(animation);
    }

    private int SelectedCoreIndex() => Mode switch
    {
        PrototypePerformanceMode.Gaming => 1,
        PrototypePerformanceMode.Turbo => 2,
        PrototypePerformanceMode.Custom => CustomProfile switch
        {
            "Profile2" => 4,
            "Profile3" => 5,
            _ => 3,
        },
        _ => 0,
    };

    private Image[] FullLogoLayers() =>
        [FullLogoOffice, FullLogoGaming, FullLogoTurbo, FullLogoCustomProfile1, FullLogoCustomProfile2, FullLogoCustomProfile3];

    private Image[] AdaptiveCoreLayers() =>
        [AdaptiveCoreOffice, AdaptiveCoreGaming, AdaptiveCoreTurbo, AdaptiveCoreCustomProfile1, AdaptiveCoreCustomProfile2, AdaptiveCoreCustomProfile3];
}
