using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class ModePresetPicker : UserControl
{
    private static event EventHandler? NamesChanged;
    private readonly UserPreferencesStore preferences = new();
    private Dictionary<string, string> names = [];
    private readonly List<Button> resetButtons = [];
    private readonly List<Button> renameButtons = [];
    private event EventHandler<PresetKey>? ResetRequested;
    private bool closingWithAnimation;
    private bool allowClose;
    private PresetKey? confirmedActiveKey;
    private static readonly IReadOnlyDictionary<ControlModeId, string> ModeLabels = new Dictionary<ControlModeId, string>
    {
        [ControlModeId.Office] = "办公模式", [ControlModeId.Gaming] = "游戏模式", [ControlModeId.Turbo] = "狂飙模式",
        [ControlModeId.Custom1] = "自定义 1", [ControlModeId.Custom2] = "自定义 2", [ControlModeId.Custom3] = "自定义 3"
    };

    public ModePresetPicker()
    {
        InitializeComponent();
        foreach (var button in ModeList.Children.OfType<Button>())
        {
            AutomationProperties.SetName(button, ModeLabels[Enum.Parse<ControlModeId>((string)button.Tag)]);
            AutomationProperties.SetAutomationId(button, $"Preset.Mode.{button.Tag}");
        }
        PrepareRenameIcons();
        Loaded += (_, _) => { NamesChanged += OnNamesChanged; RefreshNames(); };
        Unloaded += (_, _) => NamesChanged -= OnNamesChanged;
        RefreshNames();
        UpdateSelection(SelectedKey);
    }

    public PresetKey SelectedKey { get => (PresetKey)GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }
    public string SelectedDisplayName => $"{ModeLabels[SelectedKey.Mode]} · {PresetNameCatalog.GetName(names, SelectedKey)}";
    public bool IsDropDownOpen { get; private set; }
    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(PresetKey), typeof(ModePresetPicker), new PropertyMetadata(PresetKey.Create(ControlModeId.Office, 1), OnSelectedKeyChanged));
    public event EventHandler<PresetKey>? SelectedKeyChanged;
    public event EventHandler<PresetKey>? PresetInvoked;
    private bool editingPreset;
    public string DisplayNameFor(PresetKey key) => $"{ModeLabels[key.Mode]} · {PresetNameCatalog.GetName(names, key)}";
    public void SetManagementMode(bool editing)
    {
        editingPreset = editing;
        UpdateSelection(SelectedKey);
        foreach (var button in renameButtons) button.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in resetButtons) button.Visibility = editing && ResetRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSlotStates();
    }
    public void SetSlotSummary(string summary)
    {
        for (int slot = 1; slot <= 3; slot++) SetSlotSummary(slot, summary);
    }
    public void EnableReset(EventHandler<PresetKey> handler)
    {
        ResetRequested += handler;
        foreach (var button in resetButtons) button.Visibility = editingPreset ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SetResetEnabled(bool enabled)
    {
        foreach (var button in resetButtons) button.IsEnabled = enabled;
    }
    public void SetSlotSummary(int slot, string summary)
    {
        var text = slot switch { 1 => Slot1Summary, 2 => Slot2Summary, 3 => Slot3Summary, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
        if (text.Text != summary) text.Text = summary;
        ToolTipService.SetToolTip(text, summary);
    }
    public void ShowPicker() { if (IsEnabled) PresetFlyout.ShowAt(PickerButton); }
    public void HidePicker() => PresetFlyout.Hide();
    public void SetAllowedModes(IEnumerable<ControlModeId>? modes, string? reason = null)
    {
        var allowed = modes?.ToHashSet();
        foreach (var button in ModeList.Children.OfType<Button>())
        {
            button.IsEnabled = allowed is null || button.Tag is string tag &&
                Enum.TryParse<ControlModeId>(tag, out var mode) && allowed.Contains(mode);
            ToolTipService.SetToolTip(button, button.IsEnabled ? null : reason);
        }
    }
    public void SetEditingState(PresetKey key, bool dirty, bool saved) => UpdateSlotStates();
    public void SetActivePreset(PresetKey key) => SetConfirmedActivePreset(key);
    public void SetConfirmedActivePreset(PresetKey? key)
    {
        confirmedActiveKey = key;
        UpdateSlotStates();
    }

    private static void OnSelectedKeyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var picker = (ModePresetPicker)dependencyObject;
        var key = (PresetKey)args.NewValue;
        picker.UpdateSelection(key);
        picker.SelectedKeyChanged?.Invoke(picker, key);
    }

    private void OnModeClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse(tag, out ControlModeId mode))
            SelectFromUser(PresetKey.Create(mode, SelectedKey.Slot));
    }

    private void OnSlotClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int slot))
        {
            SelectFromUser(PresetKey.Create(SelectedKey.Mode, slot));
            PresetFlyout.Hide();
            PresetInvoked?.Invoke(this, SelectedKey);
        }
    }

    private void SelectFromUser(PresetKey key)
    {
        if (key == SelectedKey) return;
        SelectedKey = key;
    }

    private void UpdateSelection(PresetKey key)
    {
        SelectionText.Text = editingPreset ? $"编辑 · {SelectedDisplayName}" : "使用预设";
        SelectionIcon.Glyph = key.Mode switch
        {
            ControlModeId.Office => "\uE7F4",
            ControlModeId.Gaming => "\uE7FC",
            ControlModeId.Turbo => "\uE945",
            _ => "\uE8A5"
        };
        if (!editingPreset) SelectionIcon.Glyph = "\uE8A5";
        ToolTipService.SetToolTip(PickerButton, editingPreset ? $"正在编辑：{SelectedDisplayName}" : "选择已保存的预设并直接应用；管理预设可单独编辑");
        AutomationProperties.SetName(PickerButton, editingPreset ? $"正在编辑{SelectedDisplayName}，展开选择模式与预设" : "使用预设，展开选择模式与预设");
        UpdateSlotLabels();
        UpdateButtons(ModeList, key.Mode.ToString());
        UpdateButtons(SlotList, key.Slot.ToString());
        UpdateSlotStates();
    }

    private void RefreshNames()
    {
        names = preferences.Load().PresetNames ?? [];
        UpdateSelection(SelectedKey);
    }

    private void OnNamesChanged(object? sender, EventArgs args) => RefreshNames();

    private void UpdateSlotLabels()
    {
        foreach (var wrapper in SlotList.Children.OfType<Grid>())
        {
            var button = wrapper.Children.OfType<Button>().FirstOrDefault();
            if (button is null) continue;
            if (button.Tag is not string tag || !int.TryParse(tag, out var slot) || button.Content is not Grid row)
                continue;
            var label = wrapper.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is not null)
            {
                label.Text = PresetNameCatalog.GetName(names, PresetKey.Create(SelectedKey.Mode, slot));
                AutomationProperties.SetName(button, $"选择{label.Text}预设");
                ToolTipService.SetToolTip(label, label.Text);
            }
        }
    }

    private void PrepareRenameIcons()
    {
        var buttons = SlotList.Children.OfType<Button>().ToArray();
        SlotList.Children.Clear();
        foreach (var button in buttons)
        {
            AutomationProperties.SetAutomationId(button, $"Preset.Slot.{button.Tag}");
            var row = (Grid)button.Content;
            var content = row.Children.OfType<StackPanel>().First(child => Grid.GetColumn(child) == 1);
            var originalTitle = content.Children.OfType<TextBlock>().First();
            originalTitle.Visibility = Visibility.Collapsed;
            content.Margin = new Thickness(0, 20, 0, 0);

            var title = new TextBlock
            {
                Text = originalTitle.Text,
                FontSize = 17,
                FontWeight = originalTitle.FontWeight,
                MaxWidth = 160,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            var icon = new Button
            {
                Style = (Style)Resources["PresetIconButtonStyle"],
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) },
                Tag = button.Tag,
                Width = 24,
                Height = 24,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            AutomationProperties.SetName(icon, $"重命名预设 {button.Tag}");
            icon.Click += OnRenameIconClick;
            renameButtons.Add(icon);
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(46, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            titleRow.Children.Add(title);
            titleRow.Children.Add(icon);
            var reset = new Button
            {
                Style = (Style)Resources["PresetIconButtonStyle"],
                Content = new FontIcon { Glyph = "\uE777", FontSize = 14 },
                Tag = button.Tag, Width = 24, Height = 24, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetName(reset, $"恢复预设 {button.Tag} 的默认推荐值");
            AutomationProperties.SetAutomationId(reset, $"Preset.Reset.{button.Tag}");
            ToolTipService.SetToolTip(reset, "恢复此预设的默认推荐值");
            reset.Click += (_, _) =>
            {
                if (int.TryParse(reset.Tag?.ToString(), out var slot))
                    ResetRequested?.Invoke(this, PresetKey.Create(SelectedKey.Mode, slot));
            };
            resetButtons.Add(reset);
            titleRow.Children.Add(reset);
            var wrapper = new Grid { Height = 72, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            wrapper.Children.Add(button);
            wrapper.Children.Add(titleRow);
            SlotList.Children.Add(wrapper);
        }
    }

    private async void OnRenameIconClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var slot)) return;
        var key = PresetKey.Create(SelectedKey.Mode, slot);
        var root = XamlRoot;
        var closed = new TaskCompletionSource();
        void OnClosed(object? flyoutSender, object eventArgs)
        {
            PresetFlyout.Closed -= OnClosed;
            closed.TrySetResult();
        }
        PresetFlyout.Closed += OnClosed;
        PresetFlyout.Hide();
        await closed.Task;

        var input = new TextBox { Text = PresetNameCatalog.GetName(names, key), MaxLength = 16 };
        AutomationProperties.SetName(input, "预设名称");
        input.Loaded += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"重命名{ModeLabels[key.Mode]}预设 {key.Slot}",
            Content = input,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "恢复默认名称",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        try
        {
            preferences.Update(current =>
            {
                var renamed = new Dictionary<string, string>(current.PresetNames ?? [], StringComparer.Ordinal);
                PresetNameCatalog.SetName(renamed, key, result == ContentDialogResult.Secondary ? "" : input.Text);
                return current with { PresetNames = renamed };
            });
            NamesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await new ContentDialog { XamlRoot = root, Title = "保存失败", Content = "预设名称未更改，请重试。", CloseButtonText = "关闭" }.ShowAsync();
        }
    }

    private void UpdateSlotStates()
    {
        UpdateSlotState(1, Slot1EditingMarker, Slot1EditingBadge, Slot1UsingBadge);
        UpdateSlotState(2, Slot2EditingMarker, Slot2EditingBadge, Slot2UsingBadge);
        UpdateSlotState(3, Slot3EditingMarker, Slot3EditingBadge, Slot3UsingBadge);
    }

    private void UpdateSlotState(int slot, FrameworkElement marker, FrameworkElement editingBadge, FrameworkElement usingBadge)
    {
        bool editing = editingPreset && SelectedKey.Slot == slot;
        bool active = confirmedActiveKey == PresetKey.Create(SelectedKey.Mode, slot);
        marker.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        editingBadge.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        usingBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void UpdateButtons(Panel panel, string selectedTag)
    {
        var accent = (Brush)Application.Current.Resources["ModeSelectionBrush"];
        var normal = (Brush)Application.Current.Resources["PrototypeControlAcrylicBrush"];
        foreach (var button in panel.Children.Select(child => child is Button direct
            ? direct
            : (child as Grid)?.Children.OfType<Button>().FirstOrDefault()).OfType<Button>())
            button.Background = string.Equals(button.Tag as string, selectedTag, StringComparison.Ordinal) ? accent : normal;
    }

    private async void OnFlyoutOpened(object sender, object args)
    {
        IsDropDownOpen = true;
        if (new UISettings().AnimationsEnabled)
            await AnimatePresetEnterAsync();
    }

    private void OnFlyoutClosed(object sender, object args)
    {
        IsDropDownOpen = false;
        PresetSurface.Opacity = 1d;
        PresetSurface.RenderTransform = null;
    }

    private async void OnFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (allowClose || !new UISettings().AnimationsEnabled)
            return;

        args.Cancel = true;
        if (closingWithAnimation)
            return;

        closingWithAnimation = true;
        await AnimatePresetExitAsync();
        allowClose = true;
        PresetFlyout.Hide();
        allowClose = false;
        closingWithAnimation = false;
    }

    private Task AnimatePresetExitAsync()
    {
        var completion = new TaskCompletionSource();
        var transform = new TranslateTransform();
        PresetSurface.RenderTransform = transform;
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { From = 1d, To = 0d, Duration = TimeSpan.FromMilliseconds(120), EasingFunction = easing };
        var translate = new DoubleAnimation { From = 0d, To = -4d, Duration = TimeSpan.FromMilliseconds(120), EasingFunction = easing };
        Storyboard.SetTarget(opacity, PresetSurface);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translate, transform);
        Storyboard.SetTargetProperty(translate, "Y");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translate);
        storyboard.Completed += (_, _) => completion.SetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private Task AnimatePresetEnterAsync()
    {
        var completion = new TaskCompletionSource();
        var transform = PresetSurface.RenderTransform as TranslateTransform ?? new TranslateTransform { Y = 8d };
        PresetSurface.RenderTransform = transform;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { From = 0d, To = 1d, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = easing };
        var translate = new DoubleAnimation { From = 8d, To = 0d, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = easing };
        Storyboard.SetTarget(opacity, PresetSurface);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translate, transform);
        Storyboard.SetTargetProperty(translate, "Y");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translate);
        storyboard.Completed += (_, _) => completion.SetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private void OnFlyoutOpening(object sender, object args)
    {
        RefreshNames();
        double width = PickerButton.XamlRoot?.Size.Width ?? 1280d;
        double height = PickerButton.XamlRoot?.Size.Height ?? 800d;
        PresetSurface.Width = Math.Clamp(width * 0.36d, 470d, 510d);
        PresetSurface.Height = Math.Clamp(height * 0.32d, 330d, 350d);
        double listHeight = PresetSurface.Height - 104d; // padding, border, header and row gap
        double modeHeight = (listHeight - 5d * ModeList.Spacing) / 6d;
        foreach (var button in ModeList.Children.OfType<Button>())
            button.Height = modeHeight;
        double slotHeight = (listHeight - 2d * SlotList.Spacing) / 3d;
        double slotWidth = (PresetSurface.Width - 77d) * 0.65d;
        foreach (var wrapper in SlotList.Children.OfType<Grid>())
        {
            wrapper.Height = slotHeight;
            wrapper.Children.OfType<Button>().First().Height = slotHeight;
            var titleRow = wrapper.Children.OfType<StackPanel>().First();
            titleRow.Margin = new Thickness(46, (slotHeight - 72d) / 2d + 9d, 0, 0);
            titleRow.Children.OfType<TextBlock>().First().MaxWidth = Math.Max(32d, slotWidth - 180d);
        }
        if (new UISettings().AnimationsEnabled)
        {
            PresetSurface.Opacity = 0d;
            PresetSurface.RenderTransform = new TranslateTransform { Y = 8d };
        }
    }
}
