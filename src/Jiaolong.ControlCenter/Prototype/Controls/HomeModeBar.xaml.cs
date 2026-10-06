using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class HomeModeBar : UserControl
{
    private const double ExpandedIconY = 16;
    private const double ExpandedLabelY = -27;
    private const double CustomExpandedIconX = -36;
    private const double CustomExpandedLabelX = 24;
    private const double CustomExpandedLabelY = -27;
    private readonly Dictionary<PrototypePerformanceMode, Border> cards;
    private readonly Dictionary<PrototypePerformanceMode, Border> indicators;
    private readonly Dictionary<string, Border> childIndicators;
    private readonly Dictionary<string, Button> childButtons;
    private readonly Dictionary<Button, (ScaleTransform Scale, Canvas Glow)> modeIcons;
    private readonly Dictionary<Button, Storyboard> modeIconStoryboards = [];
    private Storyboard? indicatorStoryboard;
    private Storyboard? childIndicatorStoryboard;
    private Storyboard? turboIndicatorStoryboard;
    private Storyboard? expansionStoryboard;
    private Storyboard? cardSurfaceStoryboard;
    private long expansionTransitionVersion;
    private long cardSurfaceTransitionVersion;
    public bool IsCommandPending { get; set; }
    private PrototypePerformanceMode selectedMode = PrototypePerformanceMode.Office;
    private readonly ModeIndicatorTransitionCoordinator indicatorTransitions = new();
    private PrototypeMotionTokens currentMotion = PrototypeMotionProfile.Resolve(reducedMotion: false);

    public event Action<PrototypePerformanceMode>? ModeRequested;
    public event Action<string>? CustomProfileRequested;
    public event Action<string>? TurboTierRequested;

    public void SetHardwareAvailability(bool available)
    {
        ModeOffice.IsEnabled = available;
        ModeGaming.IsEnabled = available;
        ModeTurbo.IsEnabled = available;
        ModeCustom.IsEnabled = available;
        foreach (var button in childButtons.Values) button.IsEnabled = available;
    }

    public HomeModeBar()
    {
        InitializeComponent();
        cards = new()
        {
            [PrototypePerformanceMode.Office] = OfficeSelectionSurface,
            [PrototypePerformanceMode.Gaming] = GamingSelectionSurface,
            [PrototypePerformanceMode.Turbo] = TurboSelectionSurface,
            [PrototypePerformanceMode.Custom] = CustomSelectionSurface,
        };
        indicators = new()
        {
            [PrototypePerformanceMode.Office] = OfficeIndicator,
            [PrototypePerformanceMode.Gaming] = GamingIndicator,
        };
        childIndicators = new()
        {
            ["Normal"] = TurboNormalIndicator,
            ["Quiet"] = TurboQuietIndicator,
            ["Extreme"] = TurboExtremeIndicator,
            ["Profile1"] = CustomProfile1Indicator,
            ["Profile2"] = CustomProfile2Indicator,
            ["Profile3"] = CustomProfile3Indicator,
        };
        childButtons = new()
        {
            ["Normal"] = TurboHeaderButton,
            ["Quiet"] = TurboQuietButton,
            ["Extreme"] = TurboExtremeButton,
        };
        modeIcons = new()
        {
            [ModeOffice] = (ModeIconScale, OfficeIconGlowHost),
            [ModeGaming] = (GamingIconScale, GamingIconGlowHost),
            [ModeTurbo] = (TurboIconScale, TurboIconGlowHost),
            [ModeCustom] = (CustomIconScale, CustomIconGlowHost),
        };
        InitializeIconGlow(OfficeIconGlowHost, OfficeModeIcon, Color.FromArgb(255, 0x16, 0x77, 0xFF));
        InitializeIconGlow(GamingIconGlowHost, GamingModeIcon, Color.FromArgb(255, 0xFF, 0x8A, 0x1F));
        InitializeIconGlow(TurboIconGlowHost, TurboMotionIcon, Color.FromArgb(255, 0xFF, 0x31, 0x41));
        InitializeIconGlow(CustomIconGlowHost, CustomMotionIcon, Color.FromArgb(255, 0x9A, 0x5C, 0xFF));
        foreach (var button in modeIcons.Keys)
        {
            button.PointerEntered += OnModePointerEntered;
            button.PointerExited += OnModePointerExited;
            button.PointerPressed += OnModePointerPressed;
            button.PointerReleased += OnModePointerReleased;
            button.PointerCanceled += OnModePointerExited;
        }
    }

    public void ApplyMode(
        PrototypeModeTheme theme,
        bool animate,
        PrototypeMotionTokens motion,
        TimeSpan cardDuration)
    {
        currentMotion = motion;
        var mode = theme.Mode;
        foreach (var button in modeIcons.Keys)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button,
                button.Tag?.ToString() == mode.ToString() ? "Selected" : "Unselected");
        var plan = indicatorTransitions.BeginMode(mode, motion, animate);
        TransitionCardSurfaces(theme, !plan.Snap, cardDuration);
        ApplyPersistedChildSelections(plan);
        TransitionTurboIndicator(mode, !plan.Snap);
        TransitionExpandedCards(mode, !plan.Snap, cardDuration);

        if (plan.Snap)
        {
            SetMainIndicators(mode);
            return;
        }

        StartMainExit(plan);
    }

    private void StartMainExit(ModeIndicatorTransitionPlan plan)
    {
        var mode = plan.TargetMode!.Value;
        var current = indicators.ToDictionary(pair => pair.Key, pair => Capture(pair.Value));
        indicatorStoryboard?.Stop();
        foreach (var pair in indicators) SetVisual(pair.Value, current[pair.Key]);

        var exit = new Storyboard();
        foreach (var pair in indicators.Where(pair => pair.Key != mode)) AddScaleAndOpacity(exit, pair.Value, current[pair.Key], 0, 0, plan.PhaseDuration);
        exit.Completed += (_, _) =>
        {
            var enterPlan = indicatorTransitions.CompleteMainExit(plan.Version);
            if (enterPlan is null) return;
            foreach (var pair in indicators.Where(pair => pair.Key != mode)) SetVisual(pair.Value, (0, 0));
            if (!indicators.TryGetValue(mode, out var target))
            {
                indicatorTransitions.CompleteMainEnter(enterPlan.Version);
                return;
            }
            var targetCurrent = Capture(target);
            var enter = new Storyboard();
            AddScaleAndOpacity(enter, target, targetCurrent, 1, 1, enterPlan.PhaseDuration);
            enter.Completed += (_, _) =>
            {
                if (indicatorTransitions.CompleteMainEnter(enterPlan.Version)) SetVisual(target, (1, 1));
            };
            indicatorStoryboard = enter;
            enter.Begin();
        };
        indicatorStoryboard = exit;
        exit.Begin();
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out PrototypePerformanceMode mode)) return;
        if (!IsCommandPending && mode == selectedMode && mode == PrototypePerformanceMode.Turbo)
        {
            RequestChild("Normal");
            return;
        }
        ModeRequested?.Invoke(mode);
    }

    private static void InitializeIconGlow(Canvas host, Image icon, Color color)
    {
        var hostVisual = ElementCompositionPreview.GetElementVisual(host);
        var compositor = hostVisual.Compositor;
        var dropShadow = compositor.CreateDropShadow();
        dropShadow.Mask = icon.GetAlphaMask();
        dropShadow.Color = color;
        dropShadow.BlurRadius = 16f;
        dropShadow.Offset = Vector3.Zero;

        var shadowVisual = compositor.CreateSpriteVisual();
        shadowVisual.Shadow = dropShadow;
        var bindSize = compositor.CreateExpressionAnimation("hostVisual.Size");
        bindSize.SetReferenceParameter("hostVisual", hostVisual);
        shadowVisual.StartAnimation("Size", bindSize);
        ElementCompositionPreview.SetElementChildVisual(host, shadowVisual);
    }

    private void OnModePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button) AnimateModeIcon(button, 1, 0.7, 120);
    }

    private void OnModePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button) AnimateModeIcon(button, 1, 0, 120);
    }

    private void OnModePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button) AnimateModeIcon(button, 0.94, 0.82, 70);
    }

    private void OnModePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button) AnimateModeIcon(button, 1, 0.7, 110);
    }

    private void AnimateModeIcon(Button button, double scale, double glow, int milliseconds)
    {
        if (!modeIcons.TryGetValue(button, out var visual)) return;
        var currentScale = visual.Scale.ScaleX;
        var currentGlow = visual.Glow.Opacity;
        if (modeIconStoryboards.Remove(button, out var running)) running.Stop();
        visual.Scale.ScaleX = visual.Scale.ScaleY = currentScale;
        visual.Glow.Opacity = currentGlow;
        if (ReducedMotion())
        {
            visual.Scale.ScaleX = visual.Scale.ScaleY = scale;
            visual.Glow.Opacity = glow;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var storyboard = new Storyboard();
        AddDouble(storyboard, visual.Scale, "ScaleX", currentScale, scale, duration);
        AddDouble(storyboard, visual.Scale, "ScaleY", currentScale, scale, duration);
        AddDouble(storyboard, visual.Glow, "Opacity", currentGlow, glow, duration);
        modeIconStoryboards[button] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (modeIconStoryboards.TryGetValue(button, out var current) && ReferenceEquals(current, storyboard))
            {
                visual.Scale.ScaleX = visual.Scale.ScaleY = scale;
                visual.Glow.Opacity = glow;
                storyboard.Stop();
                modeIconStoryboards.Remove(button);
            }
        };
        storyboard.Begin();
    }

    private bool ReducedMotion() => currentMotion.IndicatorExit <= TimeSpan.Zero;

    private void OnChildOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string option } || !childIndicators.ContainsKey(option)) return;
        RequestChild(option);
    }

    public void SetConfirmedTurboTier(string tier)
    {
        childIndicatorStoryboard?.Stop();
        indicatorTransitions.BeginChild(tier, currentMotion, animate: false);
        SetChildStatuses(ChildGroup(tier), tier);
        SetChildIndicators(ChildGroup(tier), tier);
    }

    private void RequestChild(string option)
    {
        if (IsCommandPending)
        {
            if (!option.StartsWith("Profile", StringComparison.Ordinal)) TurboTierRequested?.Invoke(option);
            return;
        }
        SetChildStatuses(ChildGroup(option), option);
        var plan = indicatorTransitions.BeginChild(option, currentMotion, animate: HasMotion(currentMotion));
        if (plan.Snap) SetChildIndicators(ChildGroup(option), option);
        else StartChildExit(plan);
        if (option.StartsWith("Profile", StringComparison.Ordinal)) CustomProfileRequested?.Invoke(option);
        else TurboTierRequested?.Invoke(option);
    }

    private void TransitionCardSurfaces(PrototypeModeTheme theme, bool animate, TimeSpan duration)
    {
        var version = ++cardSurfaceTransitionVersion;
        var plan = ModeCardSurfacePlan.Begin(selectedMode, theme.Mode, animate, duration);
        var current = cards.ToDictionary(pair => pair.Key, pair => pair.Value.Opacity);
        cardSurfaceStoryboard?.Stop();
        foreach (var pair in cards) pair.Value.Opacity = current[pair.Key];

        selectedMode = theme.Mode;
        if (plan.Snap)
        {
            foreach (var pair in cards) pair.Value.Opacity = plan.TargetOpacity(pair.Key);
            return;
        }

        var storyboard = new Storyboard();
        foreach (var pair in cards)
        {
            var target = plan.TargetOpacity(pair.Key);
            AddDouble(storyboard, pair.Value, "Opacity", current[pair.Key], target, plan.Duration);
        }
        storyboard.Completed += (_, _) =>
        {
            if (version != cardSurfaceTransitionVersion) return;
            foreach (var pair in cards) pair.Value.Opacity = plan.TargetOpacity(pair.Key);
        };
        cardSurfaceStoryboard = storyboard;
        storyboard.Begin();
    }

    private void TransitionTurboIndicator(PrototypePerformanceMode mode, bool animate)
    {
        var targetVisible = mode == PrototypePerformanceMode.Turbo && TurboNormalIndicator.Opacity > 0;
        var targetOpacity = targetVisible ? 1d : 0d;
        var targetScale = targetVisible ? 1d : 0d;
        var currentOpacity = TurboNormalIndicator.Opacity;
        var currentScale = ((ScaleTransform)TurboNormalIndicator.RenderTransform).ScaleX;
        var wasVisible = TurboNormalIndicator.Visibility == Visibility.Visible;
        turboIndicatorStoryboard?.Stop();

        if (!targetVisible && !wasVisible)
        {
            TurboNormalIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        TurboNormalIndicator.Visibility = Visibility.Visible;
        if (targetVisible && !wasVisible)
        {
            currentOpacity = 0;
            currentScale = 0;
            SetVisual(TurboNormalIndicator, (0, 0));
        }

        if (!animate || currentMotion.IndicatorEnter <= TimeSpan.Zero || currentMotion.IndicatorExit <= TimeSpan.Zero)
        {
            SetVisual(TurboNormalIndicator, (targetScale, targetOpacity));
            if (!targetVisible) TurboNormalIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var duration = targetVisible ? currentMotion.IndicatorEnter : currentMotion.IndicatorExit;
        var storyboard = new Storyboard();
        AddDouble(storyboard, (ScaleTransform)TurboNormalIndicator.RenderTransform, "ScaleX", currentScale, targetScale, duration);
        AddDouble(storyboard, TurboNormalIndicator, "Opacity", currentOpacity, targetOpacity, duration);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(turboIndicatorStoryboard, storyboard)) return;
            SetVisual(TurboNormalIndicator, (targetScale, targetOpacity));
            if (!targetVisible) TurboNormalIndicator.Visibility = Visibility.Collapsed;
            turboIndicatorStoryboard = null;
        };
        turboIndicatorStoryboard = storyboard;
        storyboard.Begin();
    }

    private void TransitionExpandedCards(PrototypePerformanceMode mode, bool animate, TimeSpan duration)
    {
        var version = ++expansionTransitionVersion;
        var targets = new[]
        {
            (Parts: new CardMotionParts(TurboIconTransform, TurboLabelTransform, TurboOptions, TurboOptionsTransform, TurboHeaderButton, TurboNormalIndicator, -32, 18, ExpandedLabelY), Expanded: mode == PrototypePerformanceMode.Turbo),
            (Parts: new CardMotionParts(CustomIconTransform, CustomLabelTransform, CustomOptions, CustomOptionsTransform, null, null, CustomExpandedIconX, CustomExpandedLabelX, CustomExpandedLabelY), Expanded: mode == PrototypePerformanceMode.Custom),
        };
        var current = targets.Select(target => CardMotionState.Capture(target.Parts)).ToArray();
        expansionStoryboard?.Stop();
        for (var index = 0; index < targets.Length; index++) current[index].Restore(targets[index].Parts);
        foreach (var target in targets) PrepareCardHitTargets(target.Parts, target.Expanded);

        if (!animate)
        {
            foreach (var target in targets) SetCardLayout(target.Parts, target.Expanded);
            return;
        }

        var storyboard = new Storyboard();
        foreach (var target in targets)
        {
            AddDouble(storyboard, target.Parts.Icon, "X", target.Parts.Icon.X, target.Expanded ? target.Parts.ExpandedIconX : 0, duration);
            AddDouble(storyboard, target.Parts.Icon, "Y", target.Parts.Icon.Y, target.Expanded ? ExpandedIconY : 0, duration);
            AddDouble(storyboard, target.Parts.Label, "X", target.Parts.Label.X, target.Expanded ? target.Parts.ExpandedLabelX : 0, duration);
            AddDouble(storyboard, target.Parts.Label, "Y", target.Parts.Label.Y, target.Expanded ? target.Parts.ExpandedLabelY : 0, duration);
            AddDouble(storyboard, target.Parts.OptionsTransform, "Y", target.Parts.OptionsTransform.Y, target.Expanded ? 0 : 12, duration);
            AddOptionsOpacity(storyboard, target.Parts.Options, target.Expanded, duration);
        }
        storyboard.Completed += (_, _) =>
        {
            if (version != expansionTransitionVersion) return;
            foreach (var target in targets) SetCardLayout(target.Parts, target.Expanded);
        };
        expansionStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void PrepareCardHitTargets(CardMotionParts parts, bool expanded)
    {
        parts.Options.IsHitTestVisible = expanded;
        if (parts.Header is not null) parts.Header.IsHitTestVisible = expanded;
        if (expanded && parts.ExpandedIndicator is not null) parts.ExpandedIndicator.Visibility = Visibility.Visible;
    }

    private static void SetCardLayout(CardMotionParts parts, bool expanded)
    {
        parts.Icon.X = expanded ? parts.ExpandedIconX : 0;
        parts.Icon.Y = expanded ? ExpandedIconY : 0;
        parts.Label.X = expanded ? parts.ExpandedLabelX : 0;
        parts.Label.Y = expanded ? parts.ExpandedLabelY : 0;
        parts.OptionsTransform.Y = expanded ? 0 : 12;
        parts.Options.Opacity = expanded ? 1 : 0;
        parts.Options.IsHitTestVisible = expanded;
        if (parts.Header is not null) parts.Header.IsHitTestVisible = expanded;
        if (parts.ExpandedIndicator is not null) parts.ExpandedIndicator.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AddOptionsOpacity(Storyboard storyboard, FrameworkElement options, bool expanded, TimeSpan duration)
    {
        if (!expanded)
        {
            AddDouble(storyboard, options, "Opacity", options.Opacity, 0, duration);
            return;
        }

        AddDouble(storyboard, options, "Opacity", options.Opacity, 1, duration);
    }

    private sealed record CardMotionParts(
        TranslateTransform Icon,
        TranslateTransform Label,
        FrameworkElement Options,
        TranslateTransform OptionsTransform,
        FrameworkElement? Header,
        FrameworkElement? ExpandedIndicator,
        double ExpandedIconX,
        double ExpandedLabelX,
        double ExpandedLabelY);

    private readonly record struct CardMotionState(double IconX, double IconY, double LabelX, double LabelY, double OptionsY, double OptionsOpacity)
    {
        public static CardMotionState Capture(CardMotionParts parts) =>
            new(parts.Icon.X, parts.Icon.Y, parts.Label.X, parts.Label.Y, parts.OptionsTransform.Y, parts.Options.Opacity);

        public void Restore(CardMotionParts parts)
        {
            parts.Icon.X = IconX;
            parts.Icon.Y = IconY;
            parts.Label.X = LabelX;
            parts.Label.Y = LabelY;
            parts.OptionsTransform.Y = OptionsY;
            parts.Options.Opacity = OptionsOpacity;
        }
    }

    private void StartChildExit(ModeIndicatorTransitionPlan plan)
    {
        var selected = plan.SelectedChild!;
        var group = ChildGroup(selected);
        var current = group.ToDictionary(pair => pair.Key, pair => Capture(pair.Value));
        childIndicatorStoryboard?.Stop();
        foreach (var pair in group) SetVisual(pair.Value, current[pair.Key]);
        var exit = new Storyboard();
        foreach (var pair in group.Where(pair => pair.Key != selected)) AddScaleAndOpacity(exit, pair.Value, current[pair.Key], 0, 0, plan.PhaseDuration);
        exit.Completed += (_, _) =>
        {
            var enterPlan = indicatorTransitions.CompleteChildExit(plan.Version);
            if (enterPlan is null) return;
            foreach (var pair in group.Where(pair => pair.Key != selected)) SetVisual(pair.Value, (0, 0));
            var target = childIndicators[selected];
            var enter = new Storyboard();
            AddScaleAndOpacity(enter, target, Capture(target), 1, 1, enterPlan.PhaseDuration);
            enter.Completed += (_, _) => { if (indicatorTransitions.CompleteChildEnter(enterPlan.Version)) SetVisual(target, (1, 1)); };
            childIndicatorStoryboard = enter;
            enter.Begin();
        };
        childIndicatorStoryboard = exit;
        exit.Begin();
    }

    private void SetMainIndicators(PrototypePerformanceMode mode)
    {
        indicatorStoryboard?.Stop();
        foreach (var pair in indicators) SetVisual(pair.Value, pair.Key == mode ? (1, 1) : (0, 0));
    }

    private void SetChildIndicators(IEnumerable<KeyValuePair<string, Border>> group, string selected)
    {
        childIndicatorStoryboard?.Stop();
        foreach (var pair in group)
        {
            SetVisual(pair.Value, pair.Key == selected ? (1, 1) : (0, 0));
        }
        SetChildStatuses(group, selected);
    }

    private void SetChildStatuses(IEnumerable<KeyValuePair<string, Border>> group, string selected)
    {
        foreach (var pair in group)
            if (childButtons.TryGetValue(pair.Key, out var button))
                AutomationProperties.SetItemStatus(button, pair.Key == selected ? "Selected" : string.Empty);
    }

    private void ApplyPersistedChildSelections(ModeIndicatorTransitionPlan plan)
    {
        SetChildIndicators(ChildGroup(plan.TurboTier), plan.TurboTier);
        SetChildIndicators(ChildGroup(plan.CustomProfile), plan.CustomProfile);
    }

    private IEnumerable<KeyValuePair<string, Border>> ChildGroup(string selected) =>
        childIndicators.Where(pair => pair.Key.StartsWith("Profile", StringComparison.Ordinal) == selected.StartsWith("Profile", StringComparison.Ordinal));

    private static bool HasMotion(PrototypeMotionTokens motion) => motion.IndicatorExit > TimeSpan.Zero && motion.IndicatorEnter > TimeSpan.Zero;
    private static (double Scale, double Opacity) Capture(Border indicator) => (((ScaleTransform)indicator.RenderTransform).ScaleX, indicator.Opacity);
    private static void SetVisual(Border indicator, (double Scale, double Opacity) value)
    {
        ((ScaleTransform)indicator.RenderTransform).ScaleX = value.Scale;
        indicator.Opacity = value.Opacity;
    }

    private static void AddScaleAndOpacity(Storyboard storyboard, Border indicator, (double Scale, double Opacity) from, double scale, double opacity, TimeSpan duration)
    {
        AddDouble(storyboard, (ScaleTransform)indicator.RenderTransform, "ScaleX", from.Scale, scale, duration);
        AddDouble(storyboard, indicator, "Opacity", from.Opacity, opacity, duration);
    }

    private static void AddDouble(Storyboard storyboard, DependencyObject target, string property, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = new Duration(duration), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

}
