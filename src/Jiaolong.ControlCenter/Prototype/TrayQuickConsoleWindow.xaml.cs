using Jiaolong.Contracts.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.Prototype.QuickMenu;
using Jiaolong_ControlCenter.Prototype.Controls;
using Windows.UI.ViewManagement;
using Windows.System;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class TrayQuickConsoleWindow : Window
{
    private const int ConsoleWidth = 360;
    private const int ConsoleHeight = 480;
    private readonly Action<PrototypePerformanceMode> modeRequested;
    private readonly Action editQuickMenuRequested;
    private readonly Action<bool> adaptiveRequested;
    private readonly Action strongCoolingRequested;
    private readonly Action displayOffRequested;
    private readonly Action openMainWindowRequested;
    private readonly Action<QuickSettingKind, bool> quickSettingRequested;
    private readonly Dictionary<QuickSettingKind, bool?> quickSettings = new();
    private readonly Dictionary<QuickSettingKind, Button> quickButtons = new();
    private readonly Dictionary<QuickSettingKind, (Image Icon, ProgressRing Pending)> quickVisuals = new();
    private readonly Dictionary<string, BitmapImage> quickIconCache = new(StringComparer.Ordinal);
    private readonly HashSet<QuickSettingKind> pendingQuickSettings = new();
    private QuickMenuLayout? trayQuickMenuLayout;
    private readonly Stopwatch entranceClock = new();
    private readonly DispatcherTimer freshnessTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlySet<QuickSettingKind>? availableQuickSettings;
    private HardwareSnapshot? latestTelemetry;
    private bool serviceConnected;
    private bool suppressAdaptiveToggle;
    private bool displayedAdaptiveEnabled;
    private bool adaptiveRequestPending;
    private PointInt32 dockPosition;
    private RectInt32 dockWorkArea;
    private int entranceDistance;
    private bool configured;
    private bool isVisible;
    private bool isClosing;
    private int warmupFrames;
    private double visibilityProgress;
    private double transitionFrom;
    private bool reducedMotion;

    public TrayQuickConsoleWindow(Action<PrototypePerformanceMode> modeRequested, Action<string> customProfileRequested, Action editQuickMenuRequested, Action<bool> adaptiveRequested, Action strongCoolingRequested, Action displayOffRequested, Action openMainWindowRequested, Action<QuickSettingKind, bool> quickSettingRequested)
    {
        this.modeRequested = modeRequested ?? throw new ArgumentNullException(nameof(modeRequested));
        ArgumentNullException.ThrowIfNull(customProfileRequested);
        this.editQuickMenuRequested = editQuickMenuRequested ?? throw new ArgumentNullException(nameof(editQuickMenuRequested));
        this.adaptiveRequested = adaptiveRequested ?? throw new ArgumentNullException(nameof(adaptiveRequested));
        this.strongCoolingRequested = strongCoolingRequested ?? throw new ArgumentNullException(nameof(strongCoolingRequested));
        this.displayOffRequested = displayOffRequested ?? throw new ArgumentNullException(nameof(displayOffRequested));
        this.openMainWindowRequested = openMainWindowRequested ?? throw new ArgumentNullException(nameof(openMainWindowRequested));
        this.quickSettingRequested = quickSettingRequested ?? throw new ArgumentNullException(nameof(quickSettingRequested));
        InitializeComponent();
        PopulateQuickMenu();
        ConfigureWindow();
        freshnessTimer.Tick += (_, _) => RefreshTelemetry();
        Closed += (_, _) => { freshnessTimer.Stop(); StopEntrance(); themeTransition?.Stop(); themeTransition = null; StopSelectorTransition(); StopPaletteTransition(); themeIconTransition?.Stop(); themeIconTransition = null; };
        ConsoleSurface.SizeChanged += (_, _) =>
        {
            SelectorPanel.MaxHeight = Math.Max(40, ConsoleSurface.ActualHeight - 145);
            foreach (var child in QuickMenuGrid.Children.OfType<Button>()) child.MaxWidth = Math.Max(24, (ConsoleSurface.ActualWidth - 50) / 3);
        };
        ConsoleSurface.ActualThemeChanged += (_, _) => { if (!changingTrayTheme && ConsoleRoot.RequestedTheme == ElementTheme.Default) { RefreshThemePalette(false); RefreshVisualState(false); } };
        Activated += (_, args) =>
        {
            if (isVisible && !IsModalActionPending && args.WindowActivationState == WindowActivationState.Deactivated) HideAnimated();
        };
        ConsoleSurface.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            if (SelectorLayer.Visibility == Visibility.Visible && !selectorClosing) CloseSelector(animate: false);
            else HideAnimated();
            args.Handled = true;
        };
    }

    private Brush TrayBrush(string key) => (Brush)ConsoleRoot.Resources[key];

    public void ApplyQuickMenuLayout(QuickMenuLayout layout)
    {
        trayQuickMenuLayout = layout ?? throw new ArgumentNullException(nameof(layout));
        PopulateQuickMenu();
    }

    private void PopulateQuickMenu()
    {
        QuickMenuGrid.Children.Clear();
        quickButtons.Clear();
        quickVisuals.Clear();
        var preferences = new UserPreferencesStore().Load();
        var layout = trayQuickMenuLayout ?? QuickMenuLayout.FromPersisted(preferences.QuickMenuEnabled, preferences.QuickMenuOrder);
        var catalog = QuickMenuCatalog.CreateDefault().ToDictionary(item => item.Kind);
        var items = layout.EnabledOrder.Where(catalog.ContainsKey).Take(6).ToArray();
        for (var index = 0; index < 6; index++)
        {
            var button = new PrototypeButton { Width = 86, Height = new UISettings().TextScaleFactor > 1 ? double.NaN : 56, MinHeight = 56, Padding = new Thickness(4), CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Style = (Style)Application.Current.Resources["PrototypeQuickActionStyle"] };
            Grid.SetRow(button, index / 3);
            Grid.SetColumn(button, index % 3);
            QuickMenuGrid.Children.Add(button);
            if (index >= items.Length)
            {
                button.Content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { new FontIcon { Glyph = "\uE710", FontSize = 18 }, new TextBlock { Text = "添加快捷项", FontSize = 10 } } };
                button.Click += (_, _) => editQuickMenuRequested();
                AutomationProperties.SetName(button, "添加托盘快捷项");
                button.Background = TrayBrush("TrayControlBrush"); button.BorderBrush = TrayBrush("TrayStrokeBrush"); button.Foreground = TrayBrush("TraySecondaryBrush");
                continue;
            }
            var kind = items[index];
            button.Tag = kind;
            button.MinHeight = 60;
            button.Click += OnQuickSettingClick;
            quickButtons[kind] = button;
            var icon = new Image { Source = TrayQuickIcon(kind), Width = 22, Height = 22, Stretch = Stretch.Uniform };
            var pending = new ProgressRing { IsActive = false, Visibility = Visibility.Collapsed, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var content = new Grid();
            content.Children.Add(new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { icon, new TextBlock { Text = QuickSettingLabel(kind), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Foreground = TrayBrush("TrayTextBrush"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center } } });
            content.Children.Add(pending);
            button.Content = content;
            quickVisuals[kind] = (icon, pending);
            RefreshQuickButton(kind);
        }
    }

    private static bool IsLocal(QuickSettingKind kind) => kind is QuickSettingKind.WinKey or QuickSettingKind.NumLock or QuickSettingKind.CapsLock or QuickSettingKind.DisplayOff;

    private void RefreshQuickButton(QuickSettingKind kind)
    {
        if (!quickButtons.TryGetValue(kind, out var button)) return;
        var enabled = quickSettings.GetValueOrDefault(kind);
        var pending = pendingQuickSettings.Contains(kind);
        bool action = kind == QuickSettingKind.DisplayOff;
        bool supported = IsLocal(kind) || serviceConnected && (availableQuickSettings?.Contains(kind) ?? true);
        button.IsEnabled = !pending && supported && (action || enabled.HasValue);
        var label = QuickSettingLabel(kind);
        var selected = enabled.HasValue && (kind == QuickSettingKind.FnLock
            ? enabled.Value : HomeQuickSettingSemantics.ToUiEnabled(kind, enabled.Value));
        var foreground = selected ? AccentForeground() : TrayBrush("TraySecondaryBrush");
        button.Background = selected ? AccentFill() : TrayBrush("TrayControlBrush");
        button.BorderBrush = selected ? AccentStroke() : TrayBrush("TrayStrokeBrush");
        button.Foreground = foreground;
        var visual = quickVisuals[kind];
        var source = TrayQuickIcon(kind);
        if (!ReferenceEquals(visual.Icon.Source, source)) visual.Icon.Source = source;
        visual.Icon.Opacity = pending ? .5 : 1;
        visual.Pending.IsActive = pending;
        visual.Pending.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        var state = pending ? "正在执行" : action ? "执行一次" : !enabled.HasValue ? "状态未读取" : kind is QuickSettingKind.FnLock or QuickSettingKind.Fn or QuickSettingKind.WinKey ? selected ? "已锁定" : "未锁定" : selected ? "已启用" : "已停用";
        AutomationProperties.SetName(button, $"{label}，{state}");
        ToolTipService.SetToolTip(button, !supported ? "当前连接或设备不支持此功能" : $"{label} · {state}");
    }

    public void SetSelectedMode(PrototypePerformanceMode mode, string? customProfile = null)
    {
        ControlModeId? id = mode switch { PrototypePerformanceMode.Office => ControlModeId.Office, PrototypePerformanceMode.Gaming => ControlModeId.Gaming, PrototypePerformanceMode.Turbo => ControlModeId.Turbo, PrototypePerformanceMode.Custom => customProfile switch { "Profile1" => ControlModeId.Custom1, "Profile2" => ControlModeId.Custom2, "Profile3" => ControlModeId.Custom3, _ => null }, _ => null };
        SetConfirmedSelection(id, confirmedPreset is { } key && key.Mode == id ? key : null);
    }

    private BitmapImage TrayQuickIcon(QuickSettingKind kind)
    {
        string source = QuickMenuCatalog.CreateDefault().First(item => item.Kind == kind).IconSource;
        if (trayTheme == "light") source = source.Replace(".png", "Light.png", StringComparison.Ordinal);
        if (!quickIconCache.TryGetValue(source, out var image)) quickIconCache[source] = image = new BitmapImage(new Uri(source));
        return image;
    }

    public void ApplyTelemetry(HardwareSnapshot snapshot) { latestTelemetry = snapshot; if (isVisible) RefreshTelemetry(); }

    private void RefreshTelemetry()
    {
        var snapshot = latestTelemetry;
        var now = DateTimeOffset.UtcNow;
        bool fresh = snapshot is not null && snapshot.CapturedAtUtc <= now.AddSeconds(2) && now - snapshot.CapturedAtUtc <= TimeSpan.FromSeconds(10);
        string Number(double? value, string unit, string format = "0") => fresh && value is { } v && double.IsFinite(v) ? $"{v.ToString(format)}{unit}" : $"--{unit}";
        CpuTemperatureText.Text = Number(snapshot?.CpuTemperatureC, " °C"); GpuTemperatureText.Text = Number(snapshot?.GpuTemperatureC, " °C");
        CpuUsageText.Text = Number(snapshot?.CpuUsagePercent, "% 负载"); GpuUsageText.Text = Number(snapshot?.GpuUsagePercent, "% 负载");
        CpuPowerText.Text = Number(snapshot?.CpuPowerWatts, " W"); GpuPowerText.Text = Number(snapshot?.GpuPowerWatts, " W");
        CpuFanText.Text = "风扇 " + Number(snapshot?.CpuFanRpm, " RPM"); GpuFanText.Text = "风扇 " + Number(snapshot?.GpuFanRpm, " RPM");
        MemoryText.Text = $"内存 {Number(snapshot?.MemoryUsedGb, "", "0.#")} / {Number(snapshot?.MemoryTotalGb, " GB", "0.#")}";
        PowerSourceText.Text = !fresh || snapshot?.AcPowerConnected is null ? "电源未读取" : $"{(snapshot.AcPowerConnected.Value ? "接电" : "电池")} · {Number(snapshot.BatteryPercent, "%")}";
    }

    public void ApplyState(HomeStateSnapshot snapshot)
    {
        var catalog = QuickMenuCatalog.CreateDefault();
        availableQuickSettings = catalog.Where(item => snapshot.Capabilities.Items.Any(capability => capability.Key == item.CapabilityKey && capability.State == CapabilityState.Available)).Select(item => item.Kind).ToHashSet();
        SetQuickSettingState(QuickSettingKind.StrongCooling, snapshot.Controls.StrongCooling);
        foreach (var setting in snapshot.Controls.QuickSettings) SetQuickSettingState(setting.Setting, setting.Enabled);
        if (snapshot.Telemetry is { } telemetry) ApplyTelemetry(telemetry);
    }

    public void ApplyQuickSettingState(QuickSettingKind setting, bool? enabled) => SetQuickSettingState(setting, enabled);
    public void ApplyAvailability(bool connected, IReadOnlySet<QuickSettingKind>? available = null)
    {
        bool connectionChanged = serviceConnected != connected;
        serviceConnected = connected;
        if (!connected) { fanConnected = false; fanStrongCooling = null; }
        if (!connected) foreach (var kind in quickSettings.Keys.Where(kind => !IsLocal(kind)).ToArray()) quickSettings[kind] = null;
        if (available is not null) availableQuickSettings = available;
        foreach (var kind in quickButtons.Keys) RefreshQuickButton(kind);
        RefreshTelemetry();
        RefreshFanValues();
        RefreshFanAvailability();
        if (connectionChanged && SelectorLayer.Visibility == Visibility.Visible && selectorView != "fan") RenderSelector();
    }
    public void SetQuickSettingBusy(QuickSettingKind setting, bool busy)
    {
        if (busy) pendingQuickSettings.Add(setting); else pendingQuickSettings.Remove(setting);
        RefreshQuickButton(setting);
        RefreshFanAvailability();
    }
    private void SetQuickSettingState(QuickSettingKind setting, bool? enabled)
    {
        quickSettings[setting] = enabled;
        if (setting == QuickSettingKind.StrongCooling) { fanStrongCooling = enabled; RefreshFanValues(); RefreshFanAvailability(); }
        RefreshQuickButton(setting);
    }

    public void ApplyAdaptiveState(bool enabled, bool available, bool pending = false)
    {
        displayedAdaptiveEnabled = enabled;
        adaptiveRequestPending = pending;
        suppressAdaptiveToggle = true;
        AdaptiveModeToggle.IsOn = enabled;
        AdaptiveModeToggle.IsEnabled = available;
        AdaptiveModeToggle.IsHitTestVisible = !pending;
        ToolTipService.SetToolTip(AdaptiveModeToggle, available ? "自适应调度独立于策略选择" : "当前连接或设备不可用");
        suppressAdaptiveToggle = false;
    }

    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            ConsoleWaveLogo.ReduceMotion = value;
            if (value) { RefreshThemePalette(false); RefreshVisualState(false); AnimateThemeIcon(false); SnapSelectorTransition(); }
            if (!value || !entranceClock.IsRunning) return;
            if (isClosing) HideImmediately(); else { StopEntrance(); visibilityProgress = 1; ApplyMotionFrame(); }
        }
    }
    public XamlRoot? PopupXamlRoot => ConsoleSurface.XamlRoot;
    public bool IsModalActionPending { get; set; }
    public void CloseForExit()
    {
        isVisible = false;
        freshnessTimer.Stop();
        StopEntrance();
        themeTransition?.Stop(); themeTransition = null;
        StopSelectorTransition();
        StopPaletteTransition();
        themeIconTransition?.Stop(); themeIconTransition = null;
        Close();
    }

    public void ShowStatus(string message)
    {
        // Keep operational failures accessible without adding transient footer copy.
        bool error = !string.IsNullOrWhiteSpace(message) && new[] { "失败", "未完成", "未应用", "未提交", "未确认", "不可用", "不支持", "无效", "过期", "拒绝", "未返回" }.Any(marker => message.Contains(marker, StringComparison.Ordinal));
        ToolTipService.SetToolTip(ModeSelectorButton, error ? message : "展开六个模式及各三个预设");
        if (error) AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Tray operation: {message}\n");
    }
    public void SetModeBusy(bool busy)
    {
        modeBusy = busy;
        if (!busy && pendingFocusRestore is { IsEnabled: true } restore) { restore.Focus(FocusState.Programmatic); pendingFocusRestore = null; }
        if (SelectorLayer.Visibility == Visibility.Visible && selectorView != "fan") RenderSelector();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => Navigate("settings");
    private void OnFanClick(object sender, RoutedEventArgs e) => OpenSelector("fan", FanOverlayButton);
    private void Navigate(string page) { if (navigateRequested is not null) navigateRequested(page); else openMainWindowRequested(); }
    private void OnDisplayOffClick(object sender, RoutedEventArgs e) { HideImmediately(); displayOffRequested(); }
    private void OnOpenMainClick(object sender, RoutedEventArgs e) => openMainWindowRequested();
    private void OnEditQuickMenuClick(object sender, RoutedEventArgs e) => editQuickMenuRequested();
    private void OnAdaptiveModeToggled(object sender, RoutedEventArgs e)
    {
        if (suppressAdaptiveToggle) return;
        if (adaptiveRequestPending)
        {
            ApplyAdaptiveState(displayedAdaptiveEnabled, AdaptiveModeToggle.IsEnabled, pending: true);
            return;
        }
        bool requested = AdaptiveModeToggle.IsOn;
        ShowStatus("正在确认自适应调度…");
        adaptiveRequested(requested);
    }
    private void OnQuickSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickSettingKind setting }) return;
        if (setting == QuickSettingKind.DisplayOff) { HideImmediately(); displayOffRequested(); return; }
        if (!quickSettings.TryGetValue(setting, out var enabled) || enabled is null) { ShowStatus("当前服务未返回此开关状态"); return; }
        SetQuickSettingBusy(setting, true);
        ShowStatus($"正在确认{QuickSettingLabel(setting)}…");
        if (setting == QuickSettingKind.StrongCooling) strongCoolingRequested();
        else quickSettingRequested(setting, HomeQuickSettingSemantics.ToUiEnabled(setting, !enabled.Value));
    }

    private static string QuickSettingLabel(QuickSettingKind kind) => kind switch { QuickSettingKind.Wifi => "Wi-Fi", QuickSettingKind.Bluetooth => "蓝牙", QuickSettingKind.Touchpad => "触控板", QuickSettingKind.WinKey => "Win 键锁", QuickSettingKind.Fn or QuickSettingKind.FnLock => "Fn 锁", QuickSettingKind.NumLock => "NumLock", QuickSettingKind.CapsLock => "CapsLock", QuickSettingKind.LidLogo => "A 面标志", QuickSettingKind.StrongCooling => "一键强冷", QuickSettingKind.DisplayOff => "息屏", _ => kind.ToString() };


    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private (int Width, int Height) GetScaledSize()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = hwnd != IntPtr.Zero ? Math.Max(96u, GetDpiForWindow(hwnd)) : 96u;
        var scale = dpi / 96.0;
        return ((int)Math.Ceiling(ConsoleWidth * scale), (int)Math.Ceiling(ConsoleHeight * scale));
    }

    public void ShowDocked(PointInt32? anchor = null)
    {
        GetCursorPos(out var cursor);
        var displayArea = DisplayArea.GetFromPoint(anchor ?? new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var workArea = displayArea?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
        bool changingDisplay = !dockWorkArea.Equals(workArea);
        // Shell can deliver both button-up and selection for the same gesture.
        if (isVisible && !isClosing && !changingDisplay) return;
        PopulateQuickMenu();
        CloseSelector(false);
        RefreshVisualState(false);
        freshnessTimer.Start();
        if (isClosing && !changingDisplay)
        {
            BeginTransition(false);
            Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(this));
            return;
        }
        SetWindowOpacity(0);
        dockWorkArea = workArea;
        // Move onto the target monitor before reading its per-window DPI.
        AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
        var (scaledWidth, scaledHeight) = GetScaledSize();
        scaledWidth = Math.Min(scaledWidth, workArea.Width);
        scaledHeight = Math.Min(scaledHeight, workArea.Height);
        AppWindow.ResizeClient(new SizeInt32(scaledWidth, scaledHeight));
        int gap = (int)Math.Round(12 * GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d);
        dockPosition = WindowPlacement.DockBottomRight(new WindowWorkArea(workArea.X, workArea.Y, workArea.Width, workArea.Height), AppWindow.Size, gap);
        StopEntrance();
        bool animate = !ReducedMotion && new UISettings().AnimationsEnabled;
        entranceDistance = Math.Max(6, (int)Math.Round(10 * GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d));
        AppWindow.Move(dockPosition);
        ConsoleSurface.Opacity = 1;
        isVisible = true;
        visibilityProgress = 0;
        ConsoleSurface.IsHitTestVisible = true;
        ApplyChrome();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
        if (animate && isVisible)
        {
            BeginTransition(false);
            // Let XAML commit its first content frame while the entire HWND is invisible.
            warmupFrames = 2;
        }
        else if (isVisible) { visibilityProgress = 1; ApplyMotionFrame(); }
    }

    public void HideImmediately()
    {
        StopEntrance();
        freshnessTimer.Stop();
        CloseSelector(false);
        RefreshThemePalette(false);
        themeTransition?.Stop(); themeTransition = null; accentTargets = null;
        RefreshVisualState(false);
        AnimateThemeIcon(false);
        isVisible = false;
        isClosing = false;
        AppWindow.Hide();
        visibilityProgress = 0;
        SetWindowOpacity(0);
        ConsoleSurface.Opacity = 1;
    }

    private void HideAnimated()
    {
        if (!isVisible || isClosing) return;
        if (ReducedMotion || !new UISettings().AnimationsEnabled) { HideImmediately(); return; }
        BeginTransition(true);
    }

    private void BeginTransition(bool closing)
    {
        StopEntrance();
        isClosing = closing;
        ConsoleSurface.IsHitTestVisible = !closing;
        transitionFrom = visibilityProgress;
        warmupFrames = 0;
        entranceClock.Restart();
        CompositionTarget.Rendering += OnEntranceFrame;
    }

    private void ConfigureWindow()
    {
        if (configured) return;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ApplyTheme("dark");
        AppWindow.IsShownInSwitchers = false;
        var (scaledWidth, scaledHeight) = GetScaledSize();
        AppWindow.ResizeClient(new SizeInt32(scaledWidth, scaledHeight));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true; }
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == 0) return;
        var style = GetWindowLongPtr(hwnd, ExtendedStyleIndex).ToInt64();
        SetWindowLongPtr(hwnd, ExtendedStyleIndex, new nint(style | ToolWindowStyle | 0x00080000L));
        SetWindowOpacity(0);
        configured = true;
        ApplyChrome();
    }

    private void ApplyChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        // Remove caption/dialog and resize frames; WS_DLGFRAME paints the white rim.
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        if ((style & 0x00C40000L) != 0)
        {
            SetWindowLongPtr(hwnd, -16, new nint(style & ~0x00C40000L));
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37);
        }
        uint border = 0xFFFFFFFE;
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(uint));
        uint corners = 1;
        DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(uint));
        // One radius owns both the native transparency edge and the XAML surface.
        var bounds = AppWindow.Size;
        int diameter = (int)Math.Round(36 * GetDpiForWindow(hwnd) / 96d);
        nint region = CreateRoundRectRgn(0, 0, bounds.Width + 1, bounds.Height + 1, diameter, diameter);
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        uint dark = ConsoleSurface.ActualTheme == ElementTheme.Light ? 0u : 1u;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(uint));
        // Only a changed frame style requires SWP_FRAMECHANGED. Rebuilding the
        // native input site during a theme click reenters WinUI input teardown.
    }

    private void OnEntranceFrame(object? sender, object args)
    {
        if (ReducedMotion || !new UISettings().AnimationsEnabled)
        {
            if (isClosing) HideImmediately(); else { StopEntrance(); visibilityProgress = 1; ApplyMotionFrame(); }
            return;
        }
        if (warmupFrames > 0) { warmupFrames--; entranceClock.Restart(); return; }
        double progress = Math.Clamp(entranceClock.Elapsed.TotalMilliseconds / (isClosing ? 340 : 420), 0, 1);
        double eased = isClosing ? progress * progress * progress : 1 - Math.Pow(1 - progress, 3);
        visibilityProgress = transitionFrom + ((isClosing ? 0 : 1) - transitionFrom) * eased;
        ApplyMotionFrame();
        if (progress < 1) return;
        if (isClosing) HideImmediately();
        else StopEntrance();
    }

    private void ApplyMotionFrame()
    {
        // Fade the HWND as one surface; child-only motion separates text from its panel.
        SetWindowOpacity(visibilityProgress);
    }

    private void SetWindowOpacity(double value) => SetLayeredWindowAttributes(
        WindowNative.GetWindowHandle(this), 0, (byte)Math.Round(Math.Clamp(value, 0, 1) * 255), 2);

    private void StopEntrance()
    {
        CompositionTarget.Rendering -= OnEntranceFrame;
        entranceClock.Reset();
    }

    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    private const int ExtendedStyleIndex = -20;
    private const long ToolWindowStyle = 0x00000080L;
    [StructLayout(LayoutKind.Sequential)] private readonly struct POINT { public readonly int X; public readonly int Y; }
}
