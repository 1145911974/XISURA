using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class TrayQuickConsoleWindow
{
    private Action<PresetKey>? applyPresetRequested;
    private Func<ControlModeId, PresetKey?>? rememberedPreset;
    private Func<PresetKey, (string Name, bool Available, string? Reason)>? presetInfo;
    private Action<AdaptiveStrategyId>? strategyRequested;
    private Action<string>? navigateRequested;
    private Action<ControlModeId>? managePresetsRequested;
    private ControlModeId? confirmedMode;
    private ControlModeId? previewMode;
    private PresetKey? confirmedPreset;
    private AdaptiveStrategyId? confirmedStrategy;
    private ControlModeId? browsingMode;
    private Button? selectorTrigger;
    private Button? pendingFocusRestore;
    private string selectorView = "modes";
    private string logoStyle = "explore";
    private bool modeBusy;
    private bool strategyBusy;
    public void SetStrategyBusy(bool busy)
    {
        strategyBusy = busy;
        if (SelectorLayer.Visibility == Visibility.Visible && selectorView == "strategies") RenderStrategies();
    }
    private readonly SolidColorBrush accentForeground = new(Color.FromArgb(255, 51, 75, 121));
    private readonly SolidColorBrush accentFill = new(Color.FromArgb(255, 32, 41, 56));
    private readonly SolidColorBrush accentStroke = new(Color.FromArgb(255, 52, 64, 83));
    private Storyboard? themeTransition;
    private Storyboard? selectorTransition;
    private bool selectorClosing;

    public void ConfigureSelection(Action<PresetKey> applyPreset, Func<ControlModeId, PresetKey?> rememberedPreset,
        Func<PresetKey, (string Name, bool Available, string? Reason)> presetInfo,
        Action<AdaptiveStrategyId> strategyRequested, Action<string> navigateRequested, Action<ControlModeId> managePresetsRequested)
    {
        applyPresetRequested = applyPreset ?? throw new ArgumentNullException(nameof(applyPreset));
        this.rememberedPreset = rememberedPreset ?? throw new ArgumentNullException(nameof(rememberedPreset));
        this.presetInfo = presetInfo ?? throw new ArgumentNullException(nameof(presetInfo));
        this.strategyRequested = strategyRequested ?? throw new ArgumentNullException(nameof(strategyRequested));
        this.navigateRequested = navigateRequested ?? throw new ArgumentNullException(nameof(navigateRequested));
        this.managePresetsRequested = managePresetsRequested ?? throw new ArgumentNullException(nameof(managePresetsRequested));
    }

    public void PreviewSelection(ControlModeId? mode)
    {
        if (previewMode == mode) return;
        previewMode = mode;
        SelectedModeText.Text = (previewMode ?? confirmedMode) is { } target ? ModeLabel(target) : "模式未确认";
        RefreshVisualState(isVisible);
        if (SelectorLayer.Visibility == Visibility.Visible && selectorView != "fan") RenderSelector();
    }

    public void SetConfirmedSelection(ControlModeId? mode, PresetKey? preset, bool animate = true)
    {
        bool preservePreview = modeBusy && previewMode is not null && mode != previewMode;
        if (mode is { } id && !Enum.IsDefined(id)) mode = null;
        if (preset is { } key && (key.Mode != mode || key.Slot is < 1 or > 3)) preset = null;
        bool changed = confirmedMode != mode;
        bool appliedPendingPreset = modeBusy && preset is not null && confirmedPreset != preset;
        if (!preservePreview) previewMode = null;
        confirmedMode = mode;
        confirmedPreset = preset;
        SelectedModeText.Text = (previewMode ?? mode) is { } current ? ModeLabel(current) : "模式未确认";
        ToolTipService.SetToolTip(ModeSelectorButton, preset is { } p ? $"{ModeLabel(p.Mode)} · {presetInfo?.Invoke(p).Name ?? $"预设 {p.Slot}"}" : "展开六个模式及各三个预设");
        RefreshVisualState(changed && isVisible && animate);
        if (appliedPendingPreset && !preservePreview) CloseSelector();
        if (SelectorLayer.Visibility == Visibility.Visible && selectorView != "fan") RenderSelector();
    }

    public void ApplyAdaptiveStrategy(AdaptiveStrategyId? strategy)
    {
        confirmedStrategy = strategy is { } id && Enum.IsDefined(id) ? id : null;
        string label = confirmedStrategy is { } current ? StrategyLabel(current) : "策略未读取";
        ToolTipService.SetToolTip(AdaptiveStrategyButton, $"当前策略：{label}；点击切换");
        AutomationProperties.SetHelpText(AdaptiveStrategyButton, $"当前策略：{label}");
        if (SelectorLayer.Visibility == Visibility.Visible && selectorView == "strategies") RenderStrategies();
    }

    public void ApplyLogoStyle(string style)
    {
        logoStyle = string.Equals(style, "classic", StringComparison.OrdinalIgnoreCase) ? "classic" : "explore";
        ConsoleWaveLogo.Visibility = logoStyle == "explore" ? Visibility.Visible : Visibility.Collapsed;
        ConsoleLogoImage.Visibility = logoStyle == "classic" ? Visibility.Visible : Visibility.Collapsed;
        RefreshVisualState(false);
    }

    public event Action<string>? ThemeChanged;
    private string trayTheme = "dark";
    private bool changingTrayTheme;
    private Storyboard? paletteTransition;
    private Storyboard? themeIconTransition;
    private (Color Foreground, Color Fill, Color Stroke, Color TintStart, Color TintMiddle)? accentTargets;
    private static readonly string[] PaletteKeys = ["TrayPanelBrush", "TrayControlBrush", "TrayTextBrush", "TraySecondaryBrush", "TrayStrokeBrush"];
    public void ApplyTheme(string theme, bool animate = false)
    {
        string normalized = theme?.ToLowerInvariant() == "light" ? "light" : "dark";
        bool changed = normalized != trayTheme;
        trayTheme = normalized;
        changingTrayTheme = true;
        ConsoleRoot.RequestedTheme = normalized == "light" ? ElementTheme.Light : ElementTheme.Dark;
        changingTrayTheme = false;
        AnimateThemeIcon(changed && animate);
        string name = normalized == "light" ? "切换快捷控制台深色主题" : "切换快捷控制台浅色主题";
        AutomationProperties.SetName(ThemeToggleButton, name);
        ToolTipService.SetToolTip(ThemeToggleButton, name);
        RefreshThemePalette(changed && animate);
        RefreshVisualState(changed && animate);
        if (configured) ApplyChrome();
    }
    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        string next = trayTheme == "dark" ? "light" : "dark";
        ApplyTheme(next, animate: true);
        ThemeChanged?.Invoke(next);
    }
    private Color PaletteColor(string key)
    {
        if (new AccessibilitySettings().HighContrast)
        {
            var settings = new UISettings();
            return settings.GetColorValue(key is "TrayPanelBrush" or "TrayControlBrush" ? UIColorType.Background : UIColorType.Foreground);
        }
        return (trayTheme, key) switch
        {
            ("light", "TrayPanelBrush") => Color.FromArgb(255, 244, 247, 252),
            ("light", "TrayControlBrush") => Color.FromArgb(255, 231, 237, 246),
            ("light", "TrayTextBrush") => Color.FromArgb(255, 27, 41, 62),
            ("light", "TraySecondaryBrush") => Color.FromArgb(255, 89, 107, 132),
            ("light", "TrayStrokeBrush") => Color.FromArgb(255, 206, 216, 230),
            (_, "TrayPanelBrush") => Color.FromArgb(255, 21, 28, 40),
            (_, "TrayControlBrush") => Color.FromArgb(255, 32, 41, 56),
            (_, "TrayTextBrush") => Color.FromArgb(255, 240, 243, 249),
            (_, "TraySecondaryBrush") => Color.FromArgb(255, 152, 166, 188),
            _ => Color.FromArgb(255, 52, 64, 83)
        };
    }
    private void StopPaletteTransition()
    {
        var sampled = PaletteKeys.Select(key => ((SolidColorBrush)TrayBrush(key)).Color).ToArray();
        paletteTransition?.Stop(); paletteTransition = null;
        for (int i = 0; i < PaletteKeys.Length; i++) ((SolidColorBrush)TrayBrush(PaletteKeys[i])).Color = sampled[i];
    }
    private void RefreshThemePalette(bool animate)
    {
        if (changingTrayTheme) return;
        StopPaletteTransition();
        if (!animate || !isVisible || ReducedMotion || !new UISettings().AnimationsEnabled || new AccessibilitySettings().HighContrast)
        {
            foreach (string key in PaletteKeys) ((SolidColorBrush)TrayBrush(key)).Color = PaletteColor(key);
            return;
        }
        var transition = new Storyboard();
        foreach (string key in PaletteKeys)
        {
            var brush = (SolidColorBrush)TrayBrush(key);
            var animation = new ColorAnimation { From = brush.Color, To = PaletteColor(key), Duration = TimeSpan.FromMilliseconds(320), EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, brush); Storyboard.SetTargetProperty(animation, "Color"); transition.Children.Add(animation);
        }
        transition.Completed += (_, _) =>
        {
            if (paletteTransition != transition) return;
            transition.Stop(); paletteTransition = null;
            foreach (string key in PaletteKeys) ((SolidColorBrush)TrayBrush(key)).Color = PaletteColor(key);
        };
        paletteTransition = transition; transition.Begin();
    }

    private Brush AccentForeground() => accentForeground;
    private Brush AccentFill() => accentFill;
    private Brush AccentStroke() => accentStroke;

    private void RefreshVisualState(bool animate)
    {
        bool highContrast = new AccessibilitySettings().HighContrast;
        ConsoleWaveLogo.SetHighContrast(highContrast);
        var neutral = PaletteColor("TrayControlBrush");
        var text = PaletteColor("TrayTextBrush");
        var displayMode = previewMode ?? confirmedMode;
        var accent = displayMode switch
        {
            ControlModeId.Office => Color.FromArgb(255, 0x16, 0x77, 0xFF),
            ControlModeId.Gaming => Color.FromArgb(255, 0xFF, 0x8A, 0x1F),
            ControlModeId.Turbo => Color.FromArgb(255, 0xFF, 0x31, 0x41),
            ControlModeId.Custom1 => Color.FromArgb(255, 0x8C, 0x94, 0x9F),
            ControlModeId.Custom2 => Color.FromArgb(255, 0x27, 0xD9, 0x80),
            ControlModeId.Custom3 => Color.FromArgb(255, 0x9A, 0x5C, 0xFF),
            _ => Color.FromArgb(255, 0x33, 0x4B, 0x79)
        };
        if (logoStyle == "classic" && Application.Current.Resources.TryGetValue("ModeAccentBrush", out var classic) && classic is SolidColorBrush classicBrush) accent = classicBrush.Color;

        ModeAccentGlow.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        var foreground = highContrast ? text : ConsoleSurface.ActualTheme == ElementTheme.Light ? Blend(text, accent, .46) : accent;
        SetAccentColors(foreground, highContrast ? neutral : Blend(neutral, accent, .2), highContrast ? text : Blend(neutral, accent, .62), Color.FromArgb(23, accent.R, accent.G, accent.B), Color.FromArgb(8, accent.R, accent.G, accent.B), animate);

        ConsoleWaveLogo.ReduceMotion = ReducedMotion;
        ConsoleWaveLogo.SetState(displayMode, animate && logoStyle == "explore");
        var asset = displayMode switch { ControlModeId.Office => "JiaolongTrayIconOffice-tight.png", ControlModeId.Gaming => "JiaolongTrayIconGaming-tight.png", ControlModeId.Turbo => "JiaolongTrayIconTurbo-tight.png", _ => "JiaolongAppIcon-tight.png" };
        ConsoleLogoImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Brand/{asset}"));
        foreach (var kind in quickButtons.Keys) RefreshQuickButton(kind);
        FanFooterIcon.Source = TrayQuickIcon(QuickSettingKind.StrongCooling);
        DisplayFooterIcon.Source = TrayQuickIcon(QuickSettingKind.DisplayOff);
    }

    private void SetAccentColors(Color foreground, Color fill, Color stroke, Color start, Color middle, bool animate)
    {
        var target = (foreground, fill, stroke, start, middle);
        if (accentTargets == target && !ReducedMotion) return;
        accentTargets = target;
        var sampled = new[] { accentForeground.Color, accentFill.Color, accentStroke.Color, TintStart.Color, TintMiddle.Color };
        themeTransition?.Stop(); themeTransition = null;
        void Commit(Color[] colors)
        {
            accentForeground.Color = colors[0]; accentFill.Color = colors[1]; accentStroke.Color = colors[2];
            TintStart.Color = colors[3]; TintMiddle.Color = colors[4];
        }
        var targets = new[] { foreground, fill, stroke, start, middle };
        Commit(sampled);
        if (!animate || ReducedMotion || !new UISettings().AnimationsEnabled)
        {
            Commit(targets);
            return;
        }
        var transition = new Storyboard();
        DependencyObject[] brushes = [accentForeground, accentFill, accentStroke, TintStart, TintMiddle];
        for (int i = 0; i < brushes.Length; i++)
        {
            var animation = new ColorAnimation { From = sampled[i], To = targets[i], Duration = TimeSpan.FromMilliseconds(320), EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, brushes[i]); Storyboard.SetTargetProperty(animation, "Color"); transition.Children.Add(animation);
        }
        transition.Completed += (_, _) =>
        {
            if (themeTransition != transition) return;
            transition.Stop(); themeTransition = null; Commit(targets);
        };
        themeTransition = transition; transition.Begin();
    }

    private void AnimateThemeIcon(bool animate)
    {
        double[] sampled = [ThemeSunIcon.Opacity, ThemeMoonIcon.Opacity, SunTransform.Rotation, MoonTransform.Rotation, SunTransform.ScaleX, MoonTransform.ScaleX];
        themeIconTransition?.Stop(); themeIconTransition = null;
        bool light = trayTheme == "light";
        double[] targets = [light ? 0 : 1, light ? 1 : 0, light ? 70 : 0, light ? 0 : -70, light ? .7 : 1, light ? 1 : .7];
        void Commit(double[] values)
        {
            ThemeSunIcon.Opacity = values[0]; ThemeMoonIcon.Opacity = values[1];
            SunTransform.Rotation = values[2]; MoonTransform.Rotation = values[3];
            SunTransform.ScaleX = SunTransform.ScaleY = values[4]; MoonTransform.ScaleX = MoonTransform.ScaleY = values[5];
        }
        Commit(sampled);
        if (!animate || !isVisible || ReducedMotion || !new UISettings().AnimationsEnabled) { Commit(targets); return; }
        var transition = new Storyboard();
        DependencyObject[] elements = [ThemeSunIcon, ThemeMoonIcon, SunTransform, MoonTransform, SunTransform, MoonTransform];
        string[] properties = ["Opacity", "Opacity", "Rotation", "Rotation", "ScaleX", "ScaleX"];
        for (int i = 0; i < elements.Length; i++)
        {
            void Add(string property)
            {
                var animation = new DoubleAnimation { From = sampled[i], To = targets[i], Duration = TimeSpan.FromMilliseconds(320), EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(animation, elements[i]); Storyboard.SetTargetProperty(animation, property); transition.Children.Add(animation);
            }
            Add(properties[i]);
            if (i >= 4) Add("ScaleY");
        }
        transition.Completed += (_, _) =>
        {
            if (themeIconTransition != transition) return;
            transition.Stop(); themeIconTransition = null; Commit(targets);
        };
        themeIconTransition = transition; transition.Begin();
    }
    private static Color Blend(Color source, Color target, double amount) => Color.FromArgb(255,
        (byte)Math.Round(source.R + (target.R - source.R) * amount), (byte)Math.Round(source.G + (target.G - source.G) * amount), (byte)Math.Round(source.B + (target.B - source.B) * amount));

    private void OnOpenModesClick(object sender, RoutedEventArgs e) => OpenSelector("modes", ModeSelectorButton);
    private void OnOpenStrategiesClick(object sender, RoutedEventArgs e) => OpenSelector("strategies", AdaptiveStrategyButton);
    private void OpenSelector(string view, Button trigger)
    {
        bool wasVisible = SelectorLayer.Visibility == Visibility.Visible;
        bool keyboard = trigger.FocusState == FocusState.Keyboard;
        StopSelectorTransition();
        selectorClosing = false;
        selectorView = view; selectorTrigger = trigger;
        SelectorPanel.Opacity = wasVisible ? SelectorPanel.Opacity : 0;
        SelectorLayer.Visibility = Visibility.Visible;
        SelectorLayer.IsHitTestVisible = true;
        RenderSelector();
        TransitionSelector(1, !keyboard);
        if (SelectorBackButton.Visibility == Visibility.Visible) SelectorBackButton.Focus(FocusState.Programmatic);
        else if (selectorView == "modes") ModeChoicesGrid.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Programmatic);
        else if (selectorView == "strategies") StrategyChoicesPanel.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Programmatic);
        else if (selectorView == "fan") automaticFanButton?.Focus(FocusState.Programmatic);
    }
    private void CloseSelector(bool restoreFocus = true, bool animate = true)
    {
        selectorClosing = true;
        SelectorLayer.IsHitTestVisible = false;
        if (restoreFocus && selectorTrigger is { } trigger)
        {
            if (trigger.IsEnabled) trigger.Focus(FocusState.Programmatic); else pendingFocusRestore = trigger;
        }
        if (!restoreFocus) pendingFocusRestore = null;
        selectorTrigger = null;
        TransitionSelector(0, animate && restoreFocus);
    }
    private void StopSelectorTransition()
    {
        double opacity = SelectorPanel.Opacity;
        selectorTransition?.Stop(); selectorTransition = null;
        SelectorPanel.Opacity = opacity;
    }
    private void SnapSelectorTransition()
    {
        StopSelectorTransition();
        SelectorPanel.Opacity = selectorClosing ? 0 : 1;
        if (selectorClosing) SelectorLayer.Visibility = Visibility.Collapsed;
    }
    private void TransitionSelector(double target, bool animate)
    {
        StopSelectorTransition();
        if (!animate || ReducedMotion || !new UISettings().AnimationsEnabled || SelectorLayer.Visibility != Visibility.Visible || Math.Abs(SelectorPanel.Opacity - target) < .001)
        {
            SnapSelectorTransition();
            return;
        }
        var transition = new Storyboard();
        var opacity = new DoubleAnimation
        {
            From = SelectorPanel.Opacity, To = target, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, SelectorPanel);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        transition.Children.Add(opacity);
        transition.Completed += (_, _) =>
        {
            if (selectorTransition != transition) return;
            SnapSelectorTransition();
        };
        selectorTransition = transition;
        transition.Begin();
    }
    private void OnSelectorBackdropPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SelectorLayer)) return;
        CloseSelector(); e.Handled = true;
    }
    private void OnSelectorBackClick(object sender, RoutedEventArgs e)
    {
        if (selectorView == "presets") { selectorView = "modes"; RenderSelector(); }
        else CloseSelector();
    }
    private void RenderSelector()
    {
        ModeChoicesGrid.Visibility = selectorView == "modes" ? Visibility.Visible : Visibility.Collapsed;
        PresetChoicesPanel.Visibility = selectorView == "presets" ? Visibility.Visible : Visibility.Collapsed;
        StrategyChoicesPanel.Visibility = selectorView == "strategies" ? Visibility.Visible : Visibility.Collapsed;
        ManagePresetsButton.Visibility = selectorView == "presets" ? Visibility.Visible : Visibility.Collapsed;
        FanChoicesPanel.Visibility = selectorView == "fan" ? Visibility.Visible : Visibility.Collapsed;
        SelectorBackButton.Visibility = selectorView == "presets" ? Visibility.Visible : Visibility.Collapsed;
        if (selectorView == "modes") RenderModes();
        else if (selectorView == "presets") RenderPresets();
        else if (selectorView == "strategies") RenderStrategies();
        else RenderFanControls();
    }

    private Button SelectionButton(object content, string name) => new()
    {
        Style = (Style)ConsoleRoot.Resources["TrayLinkStyle"], Content = content, CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Background = TrayBrush("TrayControlBrush"), BorderBrush = TrayBrush("TrayStrokeBrush"), Foreground = TrayBrush("TrayTextBrush"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = name
    };

    private void RenderModes()
    {
        SelectorTitleText.Text = "选择性能模式";
        ModeChoicesGrid.Children.Clear();
        var modes = Enum.GetValues<ControlModeId>();
        for (int i = 0; i < modes.Length; i++)
        {
            var mode = modes[i];
            var tile = new Grid { Height = 56, VerticalAlignment = VerticalAlignment.Center };
            var body = SelectionButton(new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { new FontIcon { Glyph = mode switch { ControlModeId.Office => "\uE7F4", ControlModeId.Gaming => "\uE7FC", ControlModeId.Turbo => "\uE945", _ => "\uE9E9" }, FontSize = 17 }, new TextBlock { Text = ModeLabel(mode), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center } } }, ModeLabel(mode));
            body.Padding = new Thickness(4); body.BorderThickness = new Thickness(1); body.IsEnabled = serviceConnected;
            body.Height = body.MinHeight = body.MaxHeight = 56;
            body.MinWidth = 0;
            body.VerticalAlignment = VerticalAlignment.Stretch;
            if ((previewMode ?? confirmedMode) == mode) { body.Background = accentFill; body.BorderBrush = accentStroke; }
            AutomationProperties.SetName(body, $"选择{ModeLabel(mode)}模式{(confirmedMode == mode ? "，当前正在使用" : "")}");
            body.Click += (_, _) => SelectMode(mode);
            var arrow = new Button { Style = (Style)ConsoleRoot.Resources["TrayLinkStyle"], Content = new FontIcon { Glyph = "\uE70D", FontSize = 8 }, Width = 23, Height = 56, MinHeight = 56, MaxHeight = 56, MinWidth = 0, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0, 8, 8, 0), Foreground = body.Foreground };
            AutomationProperties.SetName(arrow, $"{ModeLabel(mode)}，展开三个预设");
            arrow.Click += (_, _) => { browsingMode = mode; selectorView = "presets"; RenderSelector(); };
            tile.Children.Add(body);
            tile.Children.Add(arrow);
            Grid.SetRow(tile, i / 3); Grid.SetColumn(tile, i % 3); ModeChoicesGrid.Children.Add(tile);
        }
    }
    private void SelectMode(ControlModeId mode)
    {
        if (!serviceConnected) return;
        if (mode is ControlModeId.Office or ControlModeId.Gaming or ControlModeId.Turbo)
        {
            modeRequested(mode switch
            {
                ControlModeId.Office => PrototypePerformanceMode.Office,
                ControlModeId.Gaming => PrototypePerformanceMode.Gaming,
                _ => PrototypePerformanceMode.Turbo
            });
            return;
        }
        var remembered = rememberedPreset?.Invoke(mode);
        if (remembered is { } key && key.Mode == mode && key.Slot is >= 1 and <= 3 && presetInfo?.Invoke(key).Available == true) { RequestPreset(key); return; }
        browsingMode = mode; selectorView = "presets"; RenderSelector();
    }
    private void RenderPresets()
    {
        PresetChoicesPanel.Children.Clear();
        if (browsingMode is not { } mode) return;
        SelectorTitleText.Text = $"{ModeLabel(mode)} · 预设";
        for (int slot = 1; slot <= 3; slot++)
        {
            var key = PresetKey.Create(mode, slot);
            var info = presetInfo?.Invoke(key) ?? ($"预设 {slot}", false, "预设尚未读取");
            bool active = confirmedMode == mode && confirmedPreset == key;
            bool remembered = rememberedPreset?.Invoke(mode) == key;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var number = new TextBlock { Text = slot.ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = TrayBrush("TraySecondaryBrush") };
            var name = new TextBlock { Text = string.IsNullOrWhiteSpace(info.Item1) ? $"预设 {slot}" : info.Item1, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { Text = active ? "正在使用" : !info.Item2 ? "不可用" : remembered ? "已记忆" : "可用", FontSize = 9, Foreground = TrayBrush("TraySecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); Grid.SetColumn(status, 2); row.Children.Add(number); row.Children.Add(name); row.Children.Add(status);
            var button = SelectionButton(row, $"{ModeLabel(mode)}预设{slot}"); button.IsEnabled = serviceConnected && info.Item2;
            if (active) { button.BorderBrush = accentStroke; button.Background = accentFill; }
            AutomationProperties.SetName(button, $"{ModeLabel(mode)}，预设 {slot}，{name.Text}，{status.Text}");
            ToolTipService.SetToolTip(button, info.Item2 ? active ? $"{name.Text}：已确认应用" : remembered ? $"{name.Text}：模式记忆的预设，尚未确认当前应用" : name.Text : info.Item3 ?? "请先在主界面配置此预设");
            button.Click += (_, _) => RequestPreset(key);
            PresetChoicesPanel.Children.Add(button);
        }
    }
    private void RequestPreset(PresetKey key)
    {
        if (modeBusy || !serviceConnected || applyPresetRequested is null || presetInfo?.Invoke(key).Available != true) { ShowStatus("此预设尚未保存或未通过校验"); return; }
        SetModeBusy(true); ShowStatus($"正在确认{ModeLabel(key.Mode)} · 预设 {key.Slot}…");
        applyPresetRequested(key);
    }
    private void RenderStrategies()
    {
        SelectorTitleText.Text = "自适应调度策略";
        StrategyChoicesPanel.Children.Clear();
        foreach (var strategy in Enum.GetValues<AdaptiveStrategyId>())
        {
            bool active = confirmedStrategy == strategy;
            var button = SelectionButton(new TextBlock { Text = $"{StrategyLabel(strategy)}{(active ? "  · 当前策略" : "")}", FontSize = 11, TextWrapping = TextWrapping.Wrap }, StrategyLabel(strategy));
            button.IsEnabled = !strategyBusy && serviceConnected && strategyRequested is not null;
            if (active) { button.BorderBrush = accentStroke; button.Background = accentFill; }
            AutomationProperties.SetName(button, $"选择{StrategyLabel(strategy)}，不改变自适应启用状态");
            button.Click += (_, _) => { ShowStatus("正在保存调度策略…"); strategyRequested?.Invoke(strategy); };
            StrategyChoicesPanel.Children.Add(button);
        }
    }
    private void OnManagePresetsClick(object sender, RoutedEventArgs e)
    {
        if (browsingMode is not { } mode) return;
        HideImmediately(); managePresetsRequested?.Invoke(mode);
    }
    private static string ModeLabel(ControlModeId mode) => mode switch { ControlModeId.Office => "办公", ControlModeId.Gaming => "游戏", ControlModeId.Turbo => "狂飙", ControlModeId.Custom1 => "自定义 1", ControlModeId.Custom2 => "自定义 2", ControlModeId.Custom3 => "自定义 3", _ => "模式未确认" };
    private static string StrategyLabel(AdaptiveStrategyId strategy) => strategy switch { AdaptiveStrategyId.QuietFirst => "安静优先", AdaptiveStrategyId.BalancedAdaptive => "均衡自适应", AdaptiveStrategyId.ResponseFirst => "响应优先", _ => "策略未读取" };
}
