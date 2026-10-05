using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype.QuickMenu;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class HomeSidebar : UserControl
{
    private readonly UserPreferencesStore preferenceStore = new();
    private readonly Dictionary<QuickSettingKind, PrototypeToggleButton> quickToggles = [];
    private readonly Dictionary<QuickSettingKind, FrameworkElement> quickElements = [];
    private readonly Dictionary<QuickSettingKind, bool> availability = [];
    private readonly Dictionary<QuickSettingKind, bool?> observedQuickStates = [];
    private readonly SemaphoreSlim preferenceSaveGate = new(1, 1);
    private readonly QuickMenuEditor quickMenuEditor = new();
    private Storyboard? navIndicatorStoryboard;
    private readonly Dictionary<ScaleTransform, Storyboard> iconMotionStoryboards = [];
    private QuickMenuLayout layout = QuickMenuLayout.Default;
    private string selectedNavigation = "Home";
    private bool suppressQuickSettingChanged;
    private bool quickMenuLayoutApplyQueued;
    private Popup? quickMenuEditorPopup;

    public event EventHandler<string>? NavigationRequested;
    public event Action<QuickSettingKind, bool>? QuickSettingChanged;
    public event Action<QuickSettingKind>? QuickActionRequested;

    public bool ReducedMotion { get; set; }

    public TimeSpan NavigationDuration { get; set; } = TimeSpan.FromMilliseconds(190);

    public HomeSidebar()
    {
        InitializeComponent();
        AttachIconMotion(NavHome, NavHomeIcon);
        AttachIconMotion(NavPerformance, NavPerformanceIcon);
        AttachIconMotion(NavGpu, NavGpuIcon);
        AttachIconMotion(NavFan, NavFanIcon);
        AttachIconMotion(NavLighting, NavLightingIcon);
        AttachIconMotion(NavAutomation, NavAutomationIcon);
        AttachIconMotion(NavSettings, NavSettingsIcon);
        if (EditQuickMenuButton.Content is FrameworkElement editIcon)
            AttachIconMotion(EditQuickMenuButton, editIcon);
        availability[QuickSettingKind.WinKey] = true;
        quickMenuEditor.LayoutChanged += OnQuickMenuLayoutChanged;
        ApplyQuickMenuLayout(layout);
    }

    public async Task InitializeQuickMenuAsync(CancellationToken cancellationToken)
    {
        var preferences = await preferenceStore.LoadAsync(cancellationToken);
        layout = QuickMenuLayout.FromPersisted(preferences.QuickMenuEnabled, preferences.QuickMenuOrder);
        ApplyQuickMenuLayout(layout);
    }

    public void SelectNavigation(string destination)
    {
        if (string.Equals(selectedNavigation, destination, StringComparison.Ordinal)) return;

        var buttons = NavigationButtons().ToArray();
        var selectedIndex = Array.FindIndex(buttons, button => string.Equals(button.Tag?.ToString(), destination, StringComparison.Ordinal));
        if (selectedIndex < 0) return;

        selectedNavigation = destination;
        foreach (var button in buttons)
        {
            button.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.BorderThickness = new Thickness(0);
        }

        AnimateNavigationIndicator(selectedIndex, animate: true);
    }


    private void AnimateNavigationIndicator(int selectedIndex, bool animate)
    {
        var transform = (CompositeTransform)NavSelectionIndicator.RenderTransform;
        var target = selectedIndex * 74d;
        navIndicatorStoryboard?.Stop();
        if (ReducedMotion || !animate)
        {
            transform.TranslateY = target;
            return;
        }

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = NavigationDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, NavSelectionIndicator);
        Storyboard.SetTargetProperty(animation, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        storyboard.Children.Add(animation);
        navIndicatorStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(navIndicatorStoryboard, storyboard)) navIndicatorStoryboard = null;
        };
        storyboard.Begin();
    }

    public void ApplyQuickSettingState(QuickSettingKind setting, bool? enabled, bool available)
    {
        observedQuickStates[setting] = enabled;
        this.availability[setting] = available;
        quickMenuEditor.SetAvailability(setting, available);
        if (!quickToggles.TryGetValue(setting, out var toggle)) return;

        var uiEnabled = enabled.HasValue
            ? HomeQuickSettingSemantics.ToUiEnabled(setting, enabled.Value)
            : toggle.IsChecked == true;
        var previousUiEnabled = toggle.IsChecked == true;
        suppressQuickSettingChanged = true;
        try
        {
            // Keep the real state visible even when a known control is not currently writable.
            // Availability only gates interaction; it must not turn an actually-on tile into a
            // pale, unchecked tile.
            // Interaction is gated independently below so unavailable controls still use one of
            // the two real visual states instead of a third dimmed state.
            toggle.IsEnabled = true;
            toggle.IsHitTestVisible = available && enabled.HasValue;
            toggle.IsTabStop = available && enabled.HasValue;
            toggle.IsChecked = uiEnabled;
            Synchronize(toggle, useTransitions: enabled.HasValue && previousUiEnabled != uiEnabled);
            var label = QuickMenuCatalog.CreateDefault().FirstOrDefault(item => item.Kind == setting)?.Label ?? setting.ToString();
            AutomationProperties.SetName(toggle, enabled.HasValue
                ? $"{label}已{(uiEnabled ? "开启" : "关闭")}{(available ? string.Empty : "（当前不可操作）")}"
                : $"{label}不可用");
        }
        finally
        {
            suppressQuickSettingChanged = false;
        }
    }

    private void ApplyQuickMenuLayout(QuickMenuLayout value, bool refreshEditor = true)
    {
        layout = value;
        var catalog = QuickMenuCatalog.CreateDefault();
        var nextElements = new Dictionary<QuickSettingKind, FrameworkElement>();
        var nextToggles = new Dictionary<QuickSettingKind, PrototypeToggleButton>();
        for (var index = 0; index < layout.EnabledOrder.Count; index++)
        {
            var kind = layout.EnabledOrder[index];
            var item = catalog.FirstOrDefault(candidate => candidate.Kind == kind);
            if (item is null) continue;

            if (!quickElements.TryGetValue(kind, out var element) || (item.IsAction && element is not PrototypeButton) || (!item.IsAction && element is not PrototypeToggleButton))
            {
                element = item.IsAction ? CreateAction(item) : CreateToggle(item);
            }

            Grid.SetRow(element, index / 3);
            Grid.SetColumn(element, index % 3);
            if (!QuickSettingsGrid.Children.Contains(element)) QuickSettingsGrid.Children.Add(element);
            nextElements[kind] = element;
            if (element is PrototypeToggleButton toggle) nextToggles[kind] = toggle;
        }

        foreach (var obsolete in quickElements.Values.Where(element => !nextElements.Values.Contains(element)).ToArray())
            QuickSettingsGrid.Children.Remove(obsolete);

        quickElements.Clear();
        foreach (var pair in nextElements) quickElements[pair.Key] = pair.Value;
        quickToggles.Clear();
        foreach (var pair in nextToggles) quickToggles[pair.Key] = pair.Value;
        if (refreshEditor) quickMenuEditor.ApplyLayout(layout, availability);
        // Newly selected shortcuts must inherit the latest readback without waiting for a change.
        foreach (var pair in observedQuickStates.ToArray())
            ApplyQuickSettingState(pair.Key, pair.Value, availability.GetValueOrDefault(pair.Key));
    }

    private PrototypeButton CreateAction(QuickMenuItem item)
    {
        var icon = CreateIcon(item);
        var action = new PrototypeButton
        {
            Width = 61,
            Height = 61,
            Padding = new Thickness(0),
            Tag = item.Kind,
            IsEnabled = true,
            Content = icon,
            Style = FindStyle("PrototypeQuickActionStyle")
        };
        AttachIconMotion(action, icon);
        action.Click += OnQuickActionClick;
        return action;
    }

    private PrototypeToggleButton CreateToggle(QuickMenuItem item)
    {
        var icon = CreateIcon(item);
        var toggle = new PrototypeToggleButton
        {
            Width = 61,
            Height = 61,
            Padding = new Thickness(0),
            Tag = item.Kind,
            IsEnabled = true,
            IsChecked = false,
            IsHitTestVisible = false,
            IsTabStop = false,
            Content = icon,
            Style = FindStyle("PrototypeQuickToggleStyle")
        };
        AttachIconMotion(toggle, icon);
        toggle.Checked += OnQuickSettingChanged;
        toggle.Unchecked += OnQuickSettingChanged;
        return toggle;
    }

    private static Image CreateIcon(QuickMenuItem item) =>
        new()
        {
            Source = new BitmapImage(new Uri(item.IconSource)),
            Width = 28,
            Height = 28,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        };

    private void AttachIconMotion(UIElement host, FrameworkElement icon)
    {
        icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var transform = icon.RenderTransform as ScaleTransform ?? new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        icon.RenderTransform = transform;
        host.PointerEntered += (_, _) => AnimateIcon(transform, 1.04);
        host.PointerExited += (_, _) => AnimateIcon(transform, 1.0);
        host.PointerPressed += (_, _) => AnimateIcon(transform, 0.94);
        host.PointerReleased += (_, _) => AnimateIcon(transform, 1.0);
    }

    private void AnimateIcon(ScaleTransform targetTransform, double target)
    {
        if (ReducedMotion)
        {
            targetTransform.ScaleX = target;
            targetTransform.ScaleY = target;
            return;
        }

        if (iconMotionStoryboards.Remove(targetTransform, out var previous))
            previous.Stop();

        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(125));
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var animation = new DoubleAnimation
            {
                To = target,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, targetTransform);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        iconMotionStoryboards[targetTransform] = storyboard;
        storyboard.Completed += (_, _) => iconMotionStoryboards.Remove(targetTransform);
        storyboard.Begin();
    }

    private static Style? FindStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private void OnNavigationClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string destination })
            NavigationRequested?.Invoke(this, destination);
    }

    private IEnumerable<Button> NavigationButtons()
    {
        if (VisualTreeHelper.GetParent(NavHome) is Panel panel)
            return panel.Children.OfType<Button>();

        return [];
    }

    private void OnEditQuickMenuClick(object sender, RoutedEventArgs e)
    {
        if (quickMenuEditorPopup is { IsOpen: true })
        {
            quickMenuEditorPopup.IsOpen = false;
            return;
        }

        if (XamlRoot is null) return;
        quickMenuEditor.ApplyLayout(layout, availability);
        quickMenuEditorPopup = new Popup
        {
            XamlRoot = XamlRoot,
            IsLightDismissEnabled = true,
            ShouldConstrainToRootBounds = true,
            Child = quickMenuEditor
        };
        quickMenuEditorPopup.Closed += OnQuickMenuEditorPopupClosed;
        quickMenuEditorPopup.IsOpen = true;
        PositionQuickMenuEditor();
    }

    public void OpenQuickMenuEditor() => OnEditQuickMenuClick(this, new RoutedEventArgs());

    private void PositionQuickMenuEditor()
    {
        if (quickMenuEditorPopup is null || XamlRoot is null) return;
        var size = XamlRoot.Size;
        quickMenuEditorPopup.HorizontalOffset = Math.Max(0, (size.Width - 600) / 2);
        quickMenuEditorPopup.VerticalOffset = Math.Max(0, (size.Height - 380) / 2);
    }

    private void OnQuickMenuEditorPopupClosed(object? sender, object args)
    {
        if (quickMenuEditorPopup is null) return;
        var popup = quickMenuEditorPopup;
        popup.Closed -= OnQuickMenuEditorPopupClosed;
        popup.Child = null;
        quickMenuEditorPopup = null;
    }

    private void OnQuickSettingChanged(object sender, RoutedEventArgs e)
    {
        if (suppressQuickSettingChanged || sender is not PrototypeToggleButton toggle || toggle.Tag is not QuickSettingKind setting) return;
        Synchronize(toggle, useTransitions: true);
        QuickSettingChanged?.Invoke(setting, toggle.IsChecked == true);
    }

    private void OnQuickActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is PrototypeButton { Tag: QuickSettingKind setting })
            QuickActionRequested?.Invoke(setting);
    }

    private void Synchronize(ToggleButton toggle, bool useTransitions)
    {
        var checkedState = toggle.IsChecked == true;
        VisualStateManager.GoToState(toggle, checkedState ? "Checked" : "Unchecked", useTransitions);
        var setting = toggle.Tag is QuickSettingKind kind ? kind : default;
        var label = QuickMenuCatalog.CreateDefault().FirstOrDefault(item => item.Kind == setting)?.Label ?? setting.ToString();
        AutomationProperties.SetName(toggle, $"{label}已{(checkedState ? "开启" : "关闭")}");
    }

    private void OnQuickMenuLayoutChanged(QuickMenuLayout value)
    {
        layout = value;
        if (quickMenuLayoutApplyQueued) return;

        quickMenuLayoutApplyQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                quickMenuLayoutApplyQueued = false;
                ApplyQuickMenuLayout(layout, refreshEditor: false);
                _ = PersistQuickMenuAsync(layout);
            }))
        {
            quickMenuLayoutApplyQueued = false;
        }
    }

    private async Task PersistQuickMenuAsync(QuickMenuLayout value)
    {
        await preferenceSaveGate.WaitAsync();
        try
        {
            await Task.Run(() => preferenceStore.Update(current => current with
            {
                QuickMenuEnabled = value.ToPersistedEnabled(),
                QuickMenuOrder = value.ToPersistedOrder()
            }));
        }
        catch (IOException) { }
        finally
        {
            preferenceSaveGate.Release();
        }
    }
}
