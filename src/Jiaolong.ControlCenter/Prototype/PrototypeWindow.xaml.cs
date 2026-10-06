using Jiaolong_ControlCenter.Services;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System.Power;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class PrototypeWindow : Window
{
    private readonly PrototypeState state = new();
    private readonly PrototypeMotionTokens motion;
    private readonly ModeMotionProfile motionProfile = ModeMotionProfile.Final;
    private readonly ModeTransitionController transitions;
    private readonly ModeBackgroundSelector backgrounds = new();
    private readonly WindowChromeSubscription chromeSubscription = new();
    private readonly AccessibilitySettings accessibilitySettings = new();
    private readonly HomeControlSession homeSession = new();
    private readonly KeyboardIndicatorService keyboardIndicators = new();
    private readonly WindowsKeyLockController windowsKeyLock = new();
    private readonly IDisplayPowerController displayPowerController = new WindowsDisplayPowerController();
    private readonly PrototypeModeCommandCoordinator modeCommands;
    private readonly PrototypeAppliedModeState appliedMode = new();
    private readonly PrototypeModeReconciliation modeReconciliation = new();
    private readonly PrototypeStrongCoolingCommandCoordinator strongCoolingCommands;
    private readonly PendingQuickSettingStates pendingQuickSettings = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly TrayIconService tray;
    private readonly UserPreferencesStore preferences = new();
    private UserPreferences userPreferences = new(false, "system", false);
    private readonly string? acceptancePage;
    private readonly bool acceptanceSecondaryDisplay;
    private bool adaptiveModeEnabled;
    private bool adaptiveActivationPending;
    private readonly ModeColorTransitionController modeColorTransition = new();
    private readonly PrototypePageTransitionController pageTransitions = new();
    private readonly PrototypePageNavigationState pageNavigation = new("Home");
    private readonly Stopwatch modeColorClock = new();
    private string[] modeColorKeys = [];
    private TimeSpan modeColorDuration;
    private bool modeColorRenderingSubscribed;
    private ModeVisualScene? activeScene;
    private string turboTier = "Normal";
    private string customProfile = "Profile1";
    private bool isDeactivated;
    private bool isModeCommandPending;
    private PrototypePerformanceMode? queuedModeRequest;
    private string? queuedTurboTier;
    private bool customActivationPending;
    private Task? customActivationTask;
    private bool serviceConnectionStarted;
    private bool fittedToWorkArea;
    private string activePage = "Home";
    private HardwareSnapshot? latestTelemetry;
    private Storyboard? pageTransitionStoryboard;
    private SizeInt32 restoredClientSize;
    private PointInt32 restoredPosition;
    private WindowProcedure? sizingWindowProcedure;
    private IntPtr sizingWindowHandle;
    private Task? modeCommandTask;
    private Task? homeSessionTask;
    private Task? strongCoolingCommandTask;
    private DispatcherQueueTimer? keyboardStateTimer;
    private DispatcherQueueTimer? connectionAvailabilityTimer;
    private DispatcherQueueTimer? restoreWarningTimer;
    private TrayQuickConsoleWindow? trayQuickConsole;
    private TrayContextMenuWindow? trayContextMenu;
    private bool allowClose;
    private Task? shutdownTask;

    public PrototypeWindow(string? acceptancePage = null, bool acceptanceSecondaryDisplay = false)
    {
        this.acceptancePage = acceptancePage;
        this.acceptanceSecondaryDisplay = acceptanceSecondaryDisplay;
        transitions = new ModeTransitionController(motionProfile);
        modeCommands = new((command, cancellationToken) =>
            homeSession.ExecuteAsync(command, cancellationToken));
        strongCoolingCommands = new(ApplyStrongCoolingAsync);
        InitializeComponent();
        tray = new TrayIconService(HandleTrayCommand, action => DispatcherQueue.TryEnqueue(() => action()));
        tray.ContextMenuRequested += ShowTrayContextMenu;
        tray.QuickConsoleRequested += point => ShowTrayQuickConsole(point);
        userPreferences = preferences.Load();
        var reduceMotion = userPreferences.ReduceMotion ||
            new MotionSettingsService().IsReducedMotionEnabled || !new UISettings().AnimationsEnabled;
        Application.Current.Resources["ControlStateTransitionDuration"] =
            new Duration(reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(180));
        motion = PrototypeMotionProfile.Resolve(reduceMotion);
        Sidebar.ReducedMotion = motion.Translation <= 0;
        Sidebar.NavigationDuration = motion.Page;
        Hero.ReducedMotion = motion.Translation <= 0;
        HomeMonitorGrid.ReducedMotion = motion.Translation <= 0;
        PerformanceWorkspace.AttachSession(homeSession);
        PerformanceWorkspaceV2Preview.AttachSession(homeSession);
        PerformanceWorkspaceV2Preview.SetPresetActivator(async key =>
        {
            if (customActivationPending || isModeCommandPending || allowClose) return false;
            return await ApplyPerformancePresetAsync(key);
        });
        GpuWorkspaceV2.AttachSession(homeSession);
        PageWorkspace.AttachSession(homeSession, this);
        PageWorkspace.TrayQuickMenuChanged += () =>
        {
            userPreferences = preferences.Load();
            trayQuickConsole?.ApplyQuickMenuLayout(userPreferences.ResolveTrayQuickMenuLayout());
        };
        PerformanceWorkspaceV2Preview.PrepareManualControlAsync = async () =>
        {
            if (turboBranch?.ActiveTier is not null)
            {
                _ = PerformanceWorkspaceV2Preview.ShowPresetStatusAsync("内置狂飙策略正在控制 CPU；切回普通狂飙后可实时调节");
                return false;
            }
            return await PauseAdaptiveForManualControlAsync();
        };
        PerformanceWorkspaceV2Preview.PresetSaved += InvalidateSavedPerformancePreset;
        PerformanceWorkspaceV2Preview.SetConfirmedActivePreset(null);
        PerformanceWorkspaceV2Preview.PresetApplied += key =>
        {
            PageWorkspace.SetLightingPresetTarget(key);
            ConfirmPerformancePreset(key, animate: true);
        };
        PageWorkspace.LogoStyleChanged += ApplyLogoStyle;
        PageWorkspace.SetAutomaticPresetReader(PerformanceWorkspace.ReadAutomaticServicePresetsAsync);
        PageWorkspace.StrongCoolingRequested += OnStrongCoolingChanged;
        PageWorkspace.LidLogoRequested += enabled => OnQuickSettingChanged(QuickSettingKind.LidLogo, enabled);
        PageWorkspace.RememberWindowSizeChanged += OnRememberWindowSizeChanged;
        PageWorkspace.QuickMenuEditorRequested += Sidebar.OpenQuickMenuEditor;
        PerformanceWorkspaceV2Preview.ReducedMotion = reduceMotion;
        GpuWorkspaceV2.ReducedMotion = reduceMotion;
        PageWorkspace.SetReducedMotion(reduceMotion);
        HomeModeBar.SetHardwareAvailability(false);
        Hero.ApplyAdaptiveModeState(false, available: false);
        PageWorkspace.SetAutomationEnabled(userPreferences.AdaptiveModeEnabled);

        ConfigureWindow();
        Sidebar.NavigationRequested += OnNavigationRequested;
        Sidebar.QuickSettingChanged += OnQuickSettingChanged;
        Sidebar.QuickActionRequested += OnQuickActionRequested;
        HomeModeBar.ModeRequested += OnModeRequested;
        HomeModeBar.CustomProfileRequested += OnCustomProfileRequested;
        HomeModeBar.TurboTierRequested += OnTurboTierRequested;
        Hero.AdaptiveModeChanged += OnAdaptiveModeChanged;
        homeSession.StateChanged += OnHomeStateChanged;
        homeSession.TelemetryUpdated += OnHomeTelemetryUpdated;
        homeSession.RestoreWarning += OnRestoreWarning;
        StartConnectionAvailabilityTimer();
        RequestModeVisuals(PrototypePerformanceMode.Office, animate: false);
        Hero.SetBrandingState(userPreferences.LogoStyle, null, animate: false);
        _ = RefreshTrayPresetCatalogAsync();
    }

    public void EnsureVisible()
    {
        AppWindow.Show();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SwShownormal);
        Activate();
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowTitleBar);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "JiaolongWaveApp.ico");
        if (!File.Exists(iconPath)) iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            titleBar.BackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        }
        // Place before the first Activate so secondary launches cannot flash on primary.
        if (acceptanceSecondaryDisplay) MoveToSecondaryDisplay();
        else ResizeToPrimaryDisplayAtStartup();
        AttachChromeHandlers();
        ReapplyWindowChrome();
    }

    private void AttachChromeHandlers()
    {
        if (!chromeSubscription.TryAttach()) return;

        Activated += OnWindowActivated;
        WindowSurface.Loaded += OnWindowSurfaceLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        InstallSizingHook();
        PowerManager.EnergySaverStatusChanged += OnEnergySaverStatusChanged;
        Closed += OnWindowClosed;
    }

    private void DetachChromeHandlers()
    {
        if (!chromeSubscription.TryDetach()) return;

        Activated -= OnWindowActivated;
        WindowSurface.Loaded -= OnWindowSurfaceLoaded;
        PageWorkspace.RememberWindowSizeChanged -= OnRememberWindowSizeChanged;
        AppWindow.Closing -= OnAppWindowClosing;
        PowerManager.EnergySaverStatusChanged -= OnEnergySaverStatusChanged;
        RemoveSizingHook();
        Closed -= OnWindowClosed;
    }

    private void ReapplyWindowChrome()
    {
        var result = ApplyDwmChrome();
        if (!result.Succeeded) Debug.WriteLine(result.Diagnostic);
    }

    private DwmChromeApplyResult ApplyDwmChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return PrototypeWindowChrome.NoWindowHandle();

        var chrome = PrototypeWindowChrome.RequiredDwmAttributes;
        var borderColor = chrome.BorderColor;
        var borderColorResult = DwmSetWindowAttribute(hwnd, chrome.BorderColorAttribute, ref borderColor, sizeof(uint));

        var cornerPreference = (uint)chrome.CornerPreference;
        var cornerPreferenceResult = DwmSetWindowAttribute(hwnd, chrome.CornerPreferenceAttribute, ref cornerPreference, sizeof(uint));

        return PrototypeWindowChrome.EvaluateDwmResults(borderColorResult, cornerPreferenceResult);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (allowClose) return;
        isDeactivated = args.WindowActivationState == WindowActivationState.Deactivated;
        // Finish native activation before touching the XAML island or window chrome.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (allowClose) return;
            if (!isDeactivated) ProcessInputMethod.UseMicrosoftPinyin();
            ReapplyWindowChrome();
            RefreshBackdropActivity();
        });
    }

    private void InstallSizingHook()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero || sizingWindowHandle != IntPtr.Zero) return;
        sizingWindowProcedure ??= SizingWindowProcedure;
        if (SetWindowSubclass(hwnd, sizingWindowProcedure, 1, 0))
            sizingWindowHandle = hwnd;
    }

    private void RemoveSizingHook()
    {
        if (sizingWindowHandle == IntPtr.Zero || sizingWindowProcedure is null) return;
        if (RemoveWindowSubclass(sizingWindowHandle, sizingWindowProcedure, 1))
            sizingWindowHandle = IntPtr.Zero;
    }

    private IntPtr SizingWindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, nuint referenceData)
    {
        if (message == 0x0082) // WM_NCDESTROY: remove only our callback, preserving WinUI's chain.
            RemoveSizingHook();
        if (allowClose) return DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == 0x0218) // WM_POWERBROADCAST: queue work, never block the native callback.
            homeSession.HandlePowerEvent(wParam.ToInt32());
        // Tray callbacks belong to its message-only window; WM_APP here is owned by WinUI.

        if (message == PrototypeWindowChrome.WmSysCommand &&
            (unchecked((uint)wParam.ToInt64()) & PrototypeWindowChrome.SysCommandMask) == PrototypeWindowChrome.ScMaximize)
        {
            DispatcherQueue.TryEnqueue(() => { if (!allowClose) ToggleFitToWorkArea(); });
            return IntPtr.Zero;
        }

        if (message == PrototypeWindowChrome.WmSizing && lParam != IntPtr.Zero &&
            Enum.IsDefined(typeof(WindowSizingEdge), wParam.ToInt32()) &&
            GetWindowRect(hwnd, out var outer) && GetClientRect(hwnd, out var client))
        {
            var proposed = Marshal.PtrToStructure<NativeRect>(lParam);
            var constrained = PrototypeWindowChrome.ConstrainSizingRect(
                new WindowSizingRect(proposed.Left, proposed.Top, proposed.Right, proposed.Bottom),
                (WindowSizingEdge)wParam.ToInt32(),
                Math.Max(0, outer.Width - client.Width),
                Math.Max(0, outer.Height - client.Height));
            Marshal.StructureToPtr(
                new NativeRect(constrained.Left, constrained.Top, constrained.Right, constrained.Bottom),
                lParam,
                false);
            return new IntPtr(1);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ResizeToWorkArea(double occupancy)
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var displayWorkArea = display.WorkArea;
        ResizeToWorkArea(occupancy, new NativeRect(
            displayWorkArea.X,
            displayWorkArea.Y,
            displayWorkArea.X + displayWorkArea.Width,
            displayWorkArea.Y + displayWorkArea.Height));
    }

    private void ResizeToPrimaryDisplayAtStartup()
    {
        if (TryGetPrimaryWorkArea(out var workArea))
        {
            ResizeToWorkArea(GetStartupClientSize(), workArea);
            return;
        }

        ResizeToWorkArea(PrototypeWindowChrome.DefaultOccupancy);
    }

    private SizeInt32 GetStartupClientSize()
    {
        var reference = WindowSizePersistence.FromPixels(
            PrototypeWindowChrome.DefaultClientSize,
            PrototypeWindowChrome.ReferenceDpi);
        if (userPreferences.RememberWindowSize)
        {
            var saved = new WindowSizePreference(userPreferences.WindowWidthDip, userPreferences.WindowHeightDip);
            if (WindowSizePersistence.IsUsable(saved))
                reference = saved;
        }

        return WindowSizePersistence.ToPixels(reference, GetWindowDpi());
    }

    private void MoveToSecondaryDisplay()
    {
        if (TryGetSecondaryWorkArea(out var workArea))
        {
            // Move before resizing so the window has the target monitor DPI. Resizing
            // while still on the 144-DPI primary display makes Windows preserve the
            // logical size and shrink the physical client area again on the 96-DPI
            // 1920x1080 secondary display.
            AppWindow.Move(new PointInt32(workArea.Left + 1, workArea.Top + 1));
            ResizeToWorkArea(.96, workArea);
        }
    }

    private void MoveToPrimaryDisplay()
    {
        if (TryGetPrimaryWorkArea(out var workArea))
            ResizeToWorkArea(PrototypeWindowChrome.DefaultOccupancy, workArea);
    }

    private static bool TryGetPrimaryWorkArea(out NativeRect workArea)
    {
        var candidate = default(NativeRect);
        var found = false;
        MonitorEnumProcedure callback = (monitor, _, _, _) =>
        {
            var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && (info.Flags & MonitorInfofPrimary) != 0)
            {
                candidate = info.WorkArea;
                found = true;
                return false;
            }

            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        workArea = candidate;
        return found;
    }

    private static bool TryGetSecondaryWorkArea(out NativeRect workArea)
    {
        var candidate = default(NativeRect);
        var found = false;
        MonitorEnumProcedure callback = (monitor, _, _, _) =>
        {
            var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && (info.Flags & MonitorInfofPrimary) == 0)
            {
                candidate = info.WorkArea;
                found = true;
                return false;
            }

            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        workArea = candidate;
        return found;
    }

    private void ResizeToWorkArea(double occupancy, NativeRect workArea)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var frameWidth = 0;
        var frameHeight = 0;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var outer) && GetClientRect(hwnd, out var client))
        {
            frameWidth = Math.Max(0, outer.Width - client.Width);
            frameHeight = Math.Max(0, outer.Height - client.Height);
        }
        var preferred = PrototypeWindowChrome.DefaultClientSize;
        var size = occupancy >= 0.99
            ? PrototypeWindowChrome.FitClientSizeForWorkArea(
                workArea.Width, workArea.Height, frameWidth, frameHeight, 1.0)
            : (preferred.Width + frameWidth <= workArea.Width &&
               preferred.Height + frameHeight <= workArea.Height
                ? preferred
                : PrototypeWindowChrome.FitClientSizeForWorkArea(
                    workArea.Width, workArea.Height, frameWidth, frameHeight, occupancy));
        AppWindow.ResizeClient(size);
        AppWindow.Move(new PointInt32(
            workArea.Left + Math.Max(0, (workArea.Width - size.Width - frameWidth) / 2),
            workArea.Top + Math.Max(0, (workArea.Height - size.Height - frameHeight) / 2)));
        NormalizeClientAspect();
    }

    private void ResizeToWorkArea(SizeInt32 preferred, NativeRect workArea)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var frameWidth = 0;
        var frameHeight = 0;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var outer) && GetClientRect(hwnd, out var client))
        {
            frameWidth = Math.Max(0, outer.Width - client.Width);
            frameHeight = Math.Max(0, outer.Height - client.Height);
        }

        var size = preferred.Width + frameWidth <= workArea.Width &&
                   preferred.Height + frameHeight <= workArea.Height
            ? preferred
            : PrototypeWindowChrome.FitClientSizeForWorkArea(
                workArea.Width, workArea.Height, frameWidth, frameHeight, PrototypeWindowChrome.DefaultOccupancy);

        AppWindow.ResizeClient(size);
        var position = WindowPlacement.CenterClient(
            new WindowWorkArea(workArea.Left, workArea.Top, workArea.Width, workArea.Height),
            size,
            frameWidth,
            frameHeight);
        AppWindow.Move(position);
        NormalizeClientAspect();
    }

    private uint GetWindowDpi()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return hwnd == IntPtr.Zero ? PrototypeWindowChrome.ReferenceDpi : Math.Max(96u, GetDpiForWindow(hwnd));
    }

    private void OnRememberWindowSizeChanged(bool enabled) =>
        userPreferences = userPreferences with { RememberWindowSize = enabled };

    private void SaveWindowSizeIfEnabled()
    {
        if (!userPreferences.RememberWindowSize) return;

        var size = fittedToWorkArea && restoredClientSize.Width > 0 && restoredClientSize.Height > 0
            ? restoredClientSize
            : AppWindow.ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var saved = WindowSizePersistence.FromPixels(size, GetWindowDpi());
        try
        {
            userPreferences = preferences.Update(current => current.RememberWindowSize ? current with
            {
                WindowWidthDip = saved.WidthDip,
                WindowHeightDip = saved.HeightDip
            } : current);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"保存窗口尺寸失败：{exception.Message}");
        }
    }

    private void NormalizeClientAspect()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var outer) || !GetClientRect(hwnd, out var client)) return;

        var corrected = PrototypeWindowChrome.ConstrainClientSize(client.Width, client.Height);
        if (corrected.Width == client.Width && corrected.Height == client.Height) return;

        var widthDelta = corrected.Width - client.Width;
        var heightDelta = corrected.Height - client.Height;
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            outer.Left,
            outer.Top,
            outer.Width + widthDelta,
            outer.Height + heightDelta,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void OnWindowSurfaceLoaded(object sender, RoutedEventArgs args)
    {
        // Re-apply the final 16:10 client size after WinUI has created the native window.
        // This also repairs a stale 16:9 size left by an older capture/build helper.
        InstallSizingHook();
        if (acceptanceSecondaryDisplay)
            MoveToSecondaryDisplay();
        else
            ResizeToPrimaryDisplayAtStartup();
        NormalizeClientAspect();
        ReapplyWindowChrome();
        AppWindow.Show();
        var startupHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (startupHwnd != IntPtr.Zero) ShowWindow(startupHwnd, SwShownormal);
        Activate();
        var trayRegistered = tray.Show(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            TrayIconAssetCatalog.ResolveAbsolute(AppContext.BaseDirectory, userPreferences.LogoStyle, confirmedControlMode));
        if (!trayRegistered)
            Debug.WriteLine("托盘图标注册失败");
        PublishSystemIcons();
        // Apply DWM border/corner attributes after the presenter has settled.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ReapplyWindowChrome);
        // Startup placement is intentionally independent of the last window position:
        // every launch returns to the primary work area center.
        if (acceptancePage is not null)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        RequestModeVisuals(PrototypePerformanceMode.Turbo, animate: false);
                        PerformanceWorkspaceV2Preview.ApplyAcceptancePreview();
                        ShowPage(acceptancePage);
                    }));
        }
        RefreshBackdropActivity();
        if (serviceConnectionStarted) return;
        serviceConnectionStarted = true;
        StartKeyboardStatePolling();
        homeSessionTask = StartHomeSessionAsync();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        await ShutdownSessionAsync();
    }

    private Task ShutdownSessionAsync() => shutdownTask ??= CompleteShutdownAsync();

    private async Task CompleteShutdownAsync()
    {
        allowClose = true;
        PerformanceWorkspace.Shutdown();
        homeSession.StateChanged -= OnHomeStateChanged;
        homeSession.TelemetryUpdated -= OnHomeTelemetryUpdated;
        try
        {
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown started; process={Environment.ProcessId}\n");
        }
        catch { }
        DetachChromeHandlers();
        tray.Dispose();
        runtimeWindowIcon.Dispose();
        StopModeColorTransition();
        keyboardStateTimer?.Stop();
        connectionAvailabilityTimer?.Stop();
        connectionAvailabilityTimer = null;
        restoreWarningTimer?.Stop();
        restoreWarningTimer = null;
        lifetimeCancellation.Cancel();
        homeSession.RestoreWarning -= OnRestoreWarning;
        PageWorkspace.StopAutomationClient();
        var pendingTasks = new[] { modeCommandTask, customActivationTask, homeSessionTask, strongCoolingCommandTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        try { await Task.WhenAll(pendingTasks); } catch { }
        AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown operations settled; process={Environment.ProcessId}\n");
        try { if (turboBranch is not null) await turboBranch.ReleaseAsync(CancellationToken.None); }
        catch (Exception error) { AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Turbo branch shutdown restore: {error}\n"); }
        await homeSession.DisposeAsync();
        AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown session disposed; process={Environment.ProcessId}\n");
        windowsKeyLock.Dispose();
        lifetimeCancellation.Dispose();
    }

    private void OnEnergySaverStatusChanged(object? sender, object args) => QueueBackdropActivityRefresh();

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        try
        {
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] OnAppWindowClosing called (allowClose={allowClose})\n");
        }
        catch { }
        args.Cancel = true;
        // Hide only after the native closing callback has unwound.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (allowClose) return;
            SaveWindowSizeIfEnabled();
            _ = PageWorkspace.RestoreLightingPreviewAsync();
            AppWindow.Hide();
        });
    }

    private void HandleTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                ShowMainWindow();
                break;
            case TrayCommand.ShowQuickConsole:
                ShowTrayQuickConsole();
                break;
            case TrayCommand.Exit:
                ExitFromTray();
                break;
        }
    }

    private void ShowMainWindow() => ShowMainPage("Home");

    public void RestoreExistingWindow()
    {
        if (allowClose) return;
        trayQuickConsole?.HideImmediately();
        trayContextMenu?.HideImmediately();
        RestoreMainWindowIfMinimized();
        AppWindow.Show();
        Activate();
    }

    private void CenterCurrentWindowOnPrimary()
    {
        if (acceptanceSecondaryDisplay && TryGetSecondaryWorkArea(out var secondaryArea))
        {
            ResizeToWorkArea(AppWindow.ClientSize, secondaryArea);
            return;
        }
        if (!TryGetPrimaryWorkArea(out var workArea)) return;
        var size = AppWindow.ClientSize;
        if (size.Width <= 0 || size.Height <= 0) size = GetStartupClientSize();
        ResizeToWorkArea(size, workArea);
    }

    private void ShowTrayContextMenu(PointInt32 cursor)
    {
        trayQuickConsole?.HideImmediately();
        trayContextMenu ??= new TrayContextMenuWindow(ShowMainWindow, ExitFromTray);
        trayContextMenu.ShowNearCursor(cursor);
    }

    private void ShowTrayQuickConsole(PointInt32? anchor = null)
    {
        try
        {
            trayContextMenu?.HideImmediately();
            userPreferences = preferences.Load();
            trayQuickConsole ??= CreateTrayQuickConsole();
            trayQuickConsole.ApplyTheme(userPreferences.TrayTheme);
            trayQuickConsole.ApplyQuickMenuLayout(userPreferences.ResolveTrayQuickMenuLayout());
            trayQuickConsole.ApplyLogoStyle(userPreferences.LogoStyle);
            trayQuickConsole.ApplyAdaptiveStrategy(userPreferences.ActiveAdaptiveStrategy);
            bool reduceMotion = userPreferences.ReduceMotion || new MotionSettingsService().IsReducedMotionEnabled || !new UISettings().AnimationsEnabled;
            Hero.ReducedMotion = reduceMotion;
            trayQuickConsole.ReducedMotion = reduceMotion;
            _ = RefreshTrayPresetCatalogAsync();
            if (homeSession.State is { } snapshot) trayQuickConsole.ApplyState(snapshot);
            RefreshTrayFanState(homeSession.State);
            trayQuickConsole.ShowDocked(anchor);
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Show tray console: {error}\n");
            OnRestoreWarning("快捷控制台打开失败，请重试");
        }
    }

    private TrayQuickConsoleWindow CreateTrayQuickConsole()
    {
        userPreferences = preferences.Load();
        var window = new TrayQuickConsoleWindow(
            mode => OnModeRequested(mode),
            profile => OnCustomProfileRequested(profile),
            () => { ShowMainPage("Settings"); DispatcherQueue.TryEnqueue(() => { if (!allowClose) PageWorkspace.OpenTrayQuickMenuEditor(); }); },
            enabled => OnAdaptiveModeChanged(enabled),
            () => OnStrongCoolingChanged(!state.StrongCooling),
            () => displayPowerController.TryTurnOff(),
            ShowMainWindow,
            (setting, enabled) => OnQuickSettingChanged(setting, enabled));
        window.ReducedMotion = motion.Translation <= 0;
        window.ConfigureSelection(
            key => { if (!customActivationPending && !isModeCommandPending && !allowClose) customActivationTask = ApplyPerformancePresetAsync(key); },
            RememberedPerformancePreset, TrayPresetInfo, OnTrayStrategyRequested, ShowMainPage, ManagePerformancePresets);
        window.ApplyTheme(userPreferences.TrayTheme);
        window.ApplyQuickMenuLayout(userPreferences.ResolveTrayQuickMenuLayout());
        window.ThemeChanged += OnTrayThemeChanged;
        window.ConfigureFanActions(
            ceiling => _ = ApplyTrayFanAsync(ceiling),
            rpm => _ = ApplyTrayFanAsync(null, rpm),
            enabled => OnStrongCoolingChanged(enabled));
        window.ApplyLogoStyle(userPreferences.LogoStyle);
        window.ApplyAdaptiveStrategy(userPreferences.ActiveAdaptiveStrategy);
        window.SetConfirmedSelection(confirmedControlMode, confirmedPerformancePreset, animate: false);
        window.ApplyAvailability(homeSession.Status == HomeSessionStatus.Connected);
        window.ApplyAdaptiveState(adaptiveModeEnabled, available: homeSession.Status == HomeSessionStatus.Connected);
        if (homeSession.State is { } snapshot)
        {
            window.ApplyState(snapshot);
            if (snapshot.Telemetry is { } telemetry) window.ApplyTelemetry(telemetry);
        }
        window.ApplyQuickSettingState(QuickSettingKind.NumLock, keyboardIndicators.Read(QuickSettingKind.NumLock));
        window.ApplyQuickSettingState(QuickSettingKind.CapsLock, keyboardIndicators.Read(QuickSettingKind.CapsLock));
        window.ApplyQuickSettingState(QuickSettingKind.WinKey, windowsKeyLock.IsEnabled);
        window.Closed += OnTrayQuickConsoleClosed;
        return window;
    }

    private void OnTrayQuickConsoleClosed(object sender, WindowEventArgs args)
    {
        if (sender is not TrayQuickConsoleWindow window) return;
        window.Closed -= OnTrayQuickConsoleClosed;
        if (ReferenceEquals(trayQuickConsole, window)) trayQuickConsole = null;
    }

    private async void ExitFromTray()
    {
        if (allowClose) return;
        // Normalize while every island is alive; a minimized window cannot safely
        // regain focus when WinUI later destroys its retained popup windows.
        try
        {
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] ExitFromTray called\n");
        }
        catch { }
        allowClose = true;
        SaveWindowSizeIfEnabled();
        homeSession.StateChanged -= OnHomeStateChanged;
        homeSession.TelemetryUpdated -= OnHomeTelemetryUpdated;
        keyboardStateTimer?.Stop();
        connectionAvailabilityTimer?.Stop();
        restoreWarningTimer?.Stop();
        StopModeColorTransition();
        try { await PageWorkspace.RestoreLightingPreviewAsync(); }
        catch (Exception error) { AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Exit lighting restoration: {error}\n"); }
        await ShutdownSessionAsync();
        // Leave the input callback before closing all retained WinUI windows.
        var queued = DispatcherQueue.TryEnqueue(() =>
        {
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown closing windows; process={Environment.ProcessId}\n");
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown quick begin; hwnd={trayQuickConsole?.AppWindow.Id.Value}\n");
            trayQuickConsole?.CloseForExit();
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown quick end\n");
            trayQuickConsole = null;
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown menu begin; hwnd={trayContextMenu?.AppWindow.Id.Value}\n");
            trayContextMenu?.CloseForExit();
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown menu end\n");
            trayContextMenu = null;
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown main begin; hwnd={AppWindow.Id.Value}\n");
            // Close the final XAML window so its island unloads before the dispatcher exits.
            Close();
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown main end\n");
        });
        AppRuntimeLog.Write($"[{DateTime.Now:O}] Shutdown close queued={queued}; process={Environment.ProcessId}\n");
    }

    private void StartKeyboardStatePolling()
    {
        if (keyboardStateTimer is not null) return;
        keyboardStateTimer = DispatcherQueue.CreateTimer();
        keyboardStateTimer.Interval = TimeSpan.FromMilliseconds(100);
        keyboardStateTimer.Tick += (_, _) => ApplyKeyboardIndicatorStates();
        keyboardStateTimer.Start();
        ApplyKeyboardIndicatorStates();
    }

    private void StartConnectionAvailabilityTimer()
    {
        connectionAvailabilityTimer = DispatcherQueue.CreateTimer();
        connectionAvailabilityTimer.Interval = TimeSpan.FromSeconds(1);
        connectionAvailabilityTimer.Tick += (_, _) => RefreshConnectionAvailability();
        connectionAvailabilityTimer.Start();
    }

    private void RefreshConnectionAvailability()
    {
        if (homeSession.Status != HomeSessionStatus.Connected)
        {
            HomeModeBar.SetHardwareAvailability(false);
            trayQuickConsole?.ApplyAvailability(false);
            trayQuickConsole?.ApplyAdaptiveState(adaptiveModeEnabled, available: false);
        }
    }

    private void ApplyKeyboardIndicatorStates()
    {
        ApplyKeyboardIndicatorState(QuickSettingKind.NumLock);
        ApplyKeyboardIndicatorState(QuickSettingKind.CapsLock);
    }

    private void ApplyKeyboardIndicatorState(QuickSettingKind setting)
    {
        var enabled = keyboardIndicators.Read(setting);
        if (pendingQuickSettings.ShouldPreserve(setting, enabled)) return;
        pendingQuickSettings.TryConfirm(setting, enabled);
        trayQuickConsole?.SetQuickSettingBusy(setting, false);
        Sidebar.ApplyQuickSettingState(setting, enabled, enabled.HasValue);
        trayQuickConsole?.ApplyQuickSettingState(setting, enabled);
    }

    private async Task StartHomeSessionAsync()
    {
        try
        {
            await Sidebar.InitializeQuickMenuAsync(lifetimeCancellation.Token);
            await homeSession.StartAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error) { AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Home session startup: {error}\n"); }
    }

    private void OnHomeStateChanged(HomeStateSnapshot snapshot) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (allowClose) return;
                ApplyHomeState(snapshot);
            }
            catch (Exception ex)
            {
                try
                {
                    Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] ApplyHomeState exception: {ex}\n");
                }
                catch { }
            }
        });

    private void OnHomeTelemetryUpdated(HardwareSnapshot snapshot) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (allowClose) return;
                latestTelemetry = snapshot;
                if (Content?.XamlRoot?.IsHostVisible == true) ApplyPageTelemetry(snapshot, activePage);
                PageWorkspace.ApplyTelemetry(snapshot);
                trayQuickConsole?.ApplyTelemetry(snapshot);
            }
            catch (Exception ex)
            {
                try
                {
                    Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] ApplyTelemetry exception: {ex}\n");
                }
                catch { }
            }
        });

    private void ApplyPageTelemetry(HardwareSnapshot snapshot, string page)
    {
        switch (page)
        {
            case "Home":
                HomeMonitorGrid.ApplyTelemetry(HomeTelemetrySnapshot.FromHardwareSnapshot(snapshot));
                break;
            case "Performance":
            case "PerformanceV2":
            case "PerformanceV2Preset":
            case "PerformanceV2Advanced":
                PerformanceWorkspaceV2Preview.ApplyTelemetry(snapshot);
                break;
            case "Gpu":
            case "GpuAdvanced":
                GpuWorkspaceV2.ApplyTelemetry(snapshot);
                break;
        }
    }

    private void ApplyHomeState(HomeStateSnapshot snapshot)
    {
        if (homeSession.State is { } current) snapshot = current;
        var identity = snapshot.Capabilities.Identity ?? new HardwareIdentity("未知", "未知", "未知", "未知");
        Hero.ApplyHardwareIdentity(identity);
        var telemetry = snapshot.Telemetry;
        if (telemetry is not null)
        {
            latestTelemetry = telemetry;
            if (Content?.XamlRoot?.IsHostVisible == true) ApplyPageTelemetry(telemetry, activePage);
            PageWorkspace.ApplyTelemetry(telemetry);
            trayQuickConsole?.ApplyTelemetry(telemetry);
        }
        PageWorkspace.SetConfirmedPerformanceTarget(customActivationPending || HasConfirmedPerformanceIdentity(snapshot) ? confirmedPerformancePreset : null);
        PageWorkspace.ApplyState(snapshot);
        PerformanceWorkspace.ApplyState(snapshot);
        PerformanceWorkspaceV2Preview.ApplyState(snapshot);
        GpuWorkspaceV2.ApplyState(snapshot);
        trayQuickConsole?.ApplyState(snapshot);
        RefreshTrayFanState(snapshot);

        if (queuedModeRequest is null && !customActivationPending && !isModeCommandPending && snapshot.Controls.PerformanceMode is PerformanceMode hardwareMode)
        {
            var actualMode = PrototypeModeCommandCoordinator.Map(HasConfirmedPerformanceIdentity(snapshot)
                ? AdaptiveTargetMap.PerformanceModeFor(confirmedPerformancePreset!.Value) : hardwareMode);
            if (modeReconciliation.AcceptObservation(actualMode) &&
                (state.Mode != actualMode || appliedMode.AppliedMode != actualMode))
            {
                var animate = appliedMode.AppliedMode is not null;
                state.Mode = actualMode;
                appliedMode.Confirm(actualMode);
                RequestModeVisuals(actualMode, animate);
            }
        }
        if (!customActivationPending && !isModeCommandPending && turboBranch?.InvalidateIfChanged(snapshot) == true)
        {
            SetTurboTierVisual("Normal");
            modeCommandTask = ReleaseInvalidatedTurboBranchAsync();
        }
        ReconcileConfirmedBranding(snapshot, animate: true);
        if (!adaptiveActivationPending && snapshot.Controls.AdaptiveAutomation is { } automation)
        {
            adaptiveModeEnabled = automation.Enabled;
            bool available = homeSession.Status == HomeSessionStatus.Connected;
            Hero.ApplyAdaptiveModeState(adaptiveModeEnabled, available);
            trayQuickConsole?.ApplyAdaptiveState(adaptiveModeEnabled, available);
        }
        trayQuickConsole?.ApplyAvailability(homeSession.Status == HomeSessionStatus.Connected,
            Enum.GetValues<QuickSettingKind>().Where(setting => HasCapability(snapshot, CapabilityKey(setting))).ToHashSet());
        RefreshTrayPresetAvailability(snapshot);

        var performanceAvailable = HasCapability(snapshot, "performanceMode");
        HomeModeBar.SetHardwareAvailability(performanceAvailable && homeSession.Status == HomeSessionStatus.Connected);
        if (ShouldApplyQuickSettingState(QuickSettingKind.StrongCooling, snapshot.Controls.StrongCooling))
        {
            ApplySharedQuickSettingState(QuickSettingKind.StrongCooling, snapshot.Controls.StrongCooling, HasCapability(snapshot, "strongCooling"));
        }
        Sidebar.ApplyQuickSettingState(QuickSettingKind.WinKey, windowsKeyLock.IsEnabled, true);
        trayQuickConsole?.ApplyQuickSettingState(QuickSettingKind.WinKey, windowsKeyLock.IsEnabled);
        ApplyKeyboardIndicatorStates();
        foreach (var setting in snapshot.Controls.QuickSettings)
        {
            if (ShouldApplyQuickSettingState(setting.Setting, setting.Enabled))
                ApplySharedQuickSettingState(setting.Setting, setting.Enabled, HasCapability(snapshot, CapabilityKey(setting.Setting)));
        }
    }

    private bool ShouldApplyQuickSettingState(QuickSettingKind setting, bool? hardwareEnabled)
    {
        if (pendingQuickSettings.ShouldPreserve(setting, hardwareEnabled)) return false;
        pendingQuickSettings.TryConfirm(setting, hardwareEnabled);
        trayQuickConsole?.SetQuickSettingBusy(setting, false);
        return true;
    }

    private static bool HasCapability(HomeStateSnapshot snapshot, string key) =>
        snapshot.Capabilities.Items.Any(item => string.Equals(item.Key, key, StringComparison.Ordinal) && item.State == CapabilityState.Available);

    private void ApplySharedQuickSettingState(QuickSettingKind setting, bool? enabled, bool available)
    {
        Sidebar.ApplyQuickSettingState(setting, enabled, available);
        trayQuickConsole?.ApplyQuickSettingState(setting, enabled);
        if (setting == QuickSettingKind.StrongCooling)
        {
            state.StrongCooling = enabled == true;
            PageWorkspace.ApplyStrongCoolingState(enabled, available);
        }
        else if (setting == QuickSettingKind.LidLogo)
            PageWorkspace.ApplyLidLogoState(enabled, available);
    }

    private static string CapabilityKey(QuickSettingKind setting) =>
        setting == QuickSettingKind.StrongCooling
            ? "strongCooling"
            : $"quickSetting:{ToLowerCamel(setting.ToString())}";

    private static string ToLowerCamel(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private void QueueBackdropActivityRefresh()
    {
        if (DispatcherQueue.HasThreadAccess) RefreshBackdropActivity();
        else DispatcherQueue.TryEnqueue(RefreshBackdropActivity);
    }

    private void RefreshBackdropActivity()
    {
        if (allowClose) return;
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var savingPower = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        var highContrast = accessibilitySettings.HighContrast;
        ModeAmbientBackdrop.SetMotionPaused(ModeAmbientActivityPolicy.ShouldPause(isDeactivated, minimized, savingPower));
        ModeAmbientBackdrop.SetDecorationsVisible(ModeAmbientActivityPolicy.DecorationsVisible(highContrast));
        Hero.SetAdaptiveEffectsVisible(!highContrast);
        Hero.SetHighContrast(highContrast);
    }

    private void OnNavigationRequested(object? sender, string destination)
    {
        if (destination == "Home")
        {
            ShowPage("Home");
            return;
        }

        if (destination == "Performance")
        {
            ShowPage("Performance");
            return;
        }

        if (destination is "Gpu" or "Fan" or "Lighting" or "Automation" or "Settings")
            ShowPage(destination);
    }

    private void ShowPage(string destination)
    {
        if (destination is not ("Home" or "Performance" or "PerformanceV2" or "PerformanceV2Preset" or "PerformanceV2Advanced" or "Gpu" or "GpuAdvanced" or "Fan" or "Lighting" or "Automation" or "Settings")) return;
        if (!pageNavigation.TryRequest(destination)) return;

        ClosePageToolTips();
        SetPageFadeMasksVisible(destination != "Home");
        Sidebar.SelectNavigation(destination is "PerformanceV2" or "PerformanceV2Preset" or "PerformanceV2Advanced" ? "Performance" : destination == "GpuAdvanced" ? "Gpu" : destination);
        var plan = pageTransitions.Begin(activePage, destination, motion, animate: true);
        pageTransitionStoryboard?.Stop();
        pageTransitionStoryboard = null;
        ShowPageContent(activePage);
        NormalizePageLayers(activePage);

        var outgoing = PageElements(activePage).ToArray();
        var incoming = PageElements(destination).ToArray();
        if (plan.Snap)
        {
            ShowPageContent(destination);
            SetPageLayer(outgoing, visible: false, hitTestVisible: false);
            SetPageLayer(incoming, visible: true, hitTestVisible: true);
            activePage = destination;
            if (latestTelemetry is { } telemetry) ApplyPageTelemetry(telemetry, destination);
            return;
        }

        SetPageLayer(outgoing, visible: true, opacity: 1, hitTestVisible: false);
        SetPageLayer(incoming.Except(outgoing), visible: true, opacity: 0, hitTestVisible: false);
        BeginPageExitTransition(outgoing, incoming, destination, plan);
    }

    private void ClosePageToolTips()
    {
        if (Content?.XamlRoot is not { } root) return;
        // Popup surfaces survive a collapsed owner; dismiss them before its page fades out.
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root).ToArray())
            if (popup.Child is ToolTip tooltip) tooltip.IsOpen = false;
    }

    private void BeginPageExitTransition(
        IReadOnlyList<FrameworkElement> outgoing,
        IReadOnlyList<FrameworkElement> incoming,
        string destination,
        PrototypePageTransitionPlan plan)
    {
        var storyboard = new Storyboard();
        foreach (var element in outgoing)
            AddPageAnimation(storyboard, element, opacity: 0, duration: plan.ExitDuration);

        pageTransitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(pageTransitionStoryboard, storyboard)) return;
            BeginPageEnterTransition(outgoing, incoming, destination, plan);
        };
        storyboard.Begin();
    }

    private void BeginPageEnterTransition(
        IReadOnlyList<FrameworkElement> outgoing,
        IReadOnlyList<FrameworkElement> incoming,
        string destination,
        PrototypePageTransitionPlan plan)
    {
        ShowPageContent(destination);
        SetPageLayer(outgoing, visible: false, hitTestVisible: false);
        SetPageLayer(incoming, visible: true, opacity: 0, hitTestVisible: false);

        var storyboard = new Storyboard();
        foreach (var element in incoming)
            AddPageAnimation(storyboard, element, opacity: 1, duration: plan.EnterDuration);

        pageTransitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(pageTransitionStoryboard, storyboard)) return;
            SetPageLayer(incoming, visible: true, opacity: 1, hitTestVisible: true);
            activePage = destination;
            if (latestTelemetry is { } telemetry) ApplyPageTelemetry(telemetry, destination);
            pageTransitionStoryboard = null;
        };
        storyboard.Begin();
    }

    private IReadOnlyList<FrameworkElement> PageElements(string page) => page switch
    {
        "Home" => [Hero, HomeMonitorGrid, HomeModeBar],
        "Performance" or "PerformanceV2" or "PerformanceV2Preset" or "PerformanceV2Advanced" => [PerformanceWorkspaceV2Preview],
        "Gpu" or "GpuAdvanced" => [GpuWorkspaceV2],
        _ => [PageWorkspace]
    };

    private IReadOnlyList<FrameworkElement> AllPageElements() =>
        [Hero, HomeMonitorGrid, HomeModeBar, PerformanceWorkspace, PerformanceWorkspaceV2Preview, GpuWorkspaceV2, PageWorkspace];

    private void SetPageFadeMasksVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TopPageFadeMask.Visibility = visibility;
        BottomPageFadeMask.Visibility = Visibility.Collapsed;
    }

    private void NormalizePageLayers(string currentPage)
    {
        var current = PageElements(currentPage);
        foreach (var element in AllPageElements())
        {
            var isCurrent = current.Contains(element);
            element.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
            element.Opacity = isCurrent ? 1 : 0;
            element.IsHitTestVisible = isCurrent;
        }
    }

    private void ShowPageContent(string page)
    {
        if (page == "PerformanceV2Preset")
            PerformanceWorkspaceV2Preview.ShowPresetPreview();
        if (page == "PerformanceV2Advanced")
            PerformanceWorkspaceV2Preview.ShowAdvancedPreview();
        if (page == "GpuAdvanced")
            GpuWorkspaceV2.ShowAdvancedPreview();
        PageWorkspace.Show(page);
    }

    private static void SetPageLayer(
        IEnumerable<FrameworkElement> elements,
        bool visible,
        double opacity = 1,
        bool hitTestVisible = false)
    {
        foreach (var element in elements)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            element.Opacity = opacity;
            element.IsHitTestVisible = hitTestVisible;
        }
    }

    private static void AddPageAnimation(
        Storyboard storyboard,
        FrameworkElement element,
        double opacity,
        TimeSpan duration)
    {
        var opacityAnimation = new DoubleAnimation
        {
            To = opacity,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
        storyboard.Children.Add(opacityAnimation);
    }

    private async void OnModeRequested(PrototypePerformanceMode mode)
    {
        if (allowClose) return;
        int revision = ++manualModeRevision;
        pendingModePresetCancellation?.Cancel();
        var controlMode = mode switch
        {
            PrototypePerformanceMode.Office => ControlModeId.Office,
            PrototypePerformanceMode.Turbo => ControlModeId.Turbo,
            PrototypePerformanceMode.Custom => customProfile switch
            {
                "Profile2" => ControlModeId.Custom2,
                "Profile3" => ControlModeId.Custom3,
                _ => ControlModeId.Custom1
            },
            _ => ControlModeId.Gaming
        };
        queuedTurboTier = null;
        if (customActivationPending || isModeCommandPending)
        {
            queuedModeRequest = mode;
            PreviewModeVisuals(controlMode);
            return;
        }
        queuedModeRequest = null;
        if (mode == PrototypePerformanceMode.Custom)
        {
            OnCustomProfileRequested(customProfile);
            return;
        }
        var key = RememberedPerformancePreset(controlMode);
        if (!appliedMode.ShouldApply(mode) &&
            !(key.HasValue && PerformanceWorkspaceV2Preview.IsFollowingPreset))
        {
            PublishConfirmedMode(controlMode, confirmedPerformancePreset, animate: true);
            RestoreConfirmedModeVisuals();
            return;
        }
        PreviewModeVisuals(controlMode);
        if (!await PauseAdaptiveForManualControlAsync() || revision != manualModeRevision) return;
        if (!key.HasValue)
        {
            var saved = preferences.Load();
            if (saved.PerformancePresetSlots?.TryGetValue(controlMode.ToString(), out int slot) != true || slot is < 1 or > 3) slot = 2;
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Mode preset unavailable: mode={controlMode}; slot={slot}; catalog={trayPresetCatalog.Count}; reason={TrayPresetInfo(PresetKey.Create(controlMode, slot)).Reason}; session={homeSession.Status}\n");
        }
        if (key.HasValue && PerformanceWorkspaceV2Preview.IsFollowingPreset && !isModeCommandPending)
        {
            modeCommandTask = ApplyOfficialModeWithPresetAsync(mode, controlMode, key.Value, revision);
            return;
        }
        if (!appliedMode.ShouldApply(mode) && !isModeCommandPending) return;
        modeReconciliation.Request(mode);
        if (isModeCommandPending) return;
        modeCommandTask = ApplyPendingModesAsync();
    }

    private async Task ApplyOfficialModeWithPresetAsync(
        PrototypePerformanceMode mode, ControlModeId controlMode, PresetKey preset, int revision)
    {
        using var presetPreparation = new CancellationTokenSource();
        pendingModePresetCancellation = presetPreparation;
        isModeCommandPending = true;
        HomeModeBar.IsCommandPending = true;
        trayQuickConsole?.SetModeBusy(true);
        homeSession.SupersedeAutomaticRestore();
        PreviewModeVisuals(controlMode);
        var request = modeReconciliation.Request(mode);
        try
        {
            await ExitTurboBranchAsync(lifetimeCancellation.Token);
            if (revision != manualModeRevision) return;
            // A mode-button click first has the same hardware meaning as the keyboard mode key.
            // The optional preset is a separate transaction whose rollback starts at this mode.
            var outcome = await modeCommands.ApplyAsync(mode, lifetimeCancellation.Token);
            if (revision != manualModeRevision)
            {
                // Keep the actual acknowledgement for the next request, without replacing its preview.
                if (outcome.Applied && homeSession.State?.Controls.PerformanceMode == outcome.ContractMode)
                {
                    state.Mode = PrototypeModeCommandCoordinator.Map(outcome.ContractMode);
                    appliedMode.Confirm(state.Mode);
                }
                modeReconciliation.Reject(request);
                return;
            }
            if (!outcome.Applied || homeSession.State is not { } confirmed ||
                confirmed.Controls.PerformanceMode != outcome.ContractMode)
            {
                modeReconciliation.Reject(request);
                AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Official mode not confirmed: requested={mode}; observed={homeSession.State?.Controls.PerformanceMode}; result={outcome.Result}\n");
                await ShowCustomProfileStatusAsync("模式切换未完成；请查看硬件回读", WindowSurface.XamlRoot);
                return;
            }
            modeReconciliation.AcceptObservation(mode);
            state.Mode = PrototypeModeCommandCoordinator.Map(outcome.ContractMode);
            appliedMode.Confirm(mode);
            confirmedAutomaticApply = confirmed.Controls.AdaptiveAutomation?.LastApplyUtc;
            PublishConfirmedMode(controlMode, null, animate: true);
            RequestModeVisuals(mode, animate: true);

            if (!await ApplyPerformancePresetAsync(preset, isCurrentRequest: () => revision == manualModeRevision,
                queueCancellationToken: presetPreparation.Token) &&
                revision == manualModeRevision)
            {
                // Never reapply the old mode or claim an unverified preset after its failure.
                var observed = homeSession.State?.Controls.PerformanceMode;
                AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Official mode preset not applied: requested={mode}; observed={observed}; preset={preset}\n");
                string feedback = observed == outcome.ContractMode
                    ? "模式已切换；" + PerformanceWorkspaceV2Preview.LastPresetFeedback
                    : "当前模式以硬件回读为准；" + PerformanceWorkspaceV2Preview.LastPresetFeedback;
                await ShowCustomProfileStatusAsync(feedback, WindowSurface.XamlRoot);
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            modeReconciliation.Reject(request);
        }
        catch (Exception error)
        {
            modeReconciliation.Reject(request);
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Official mode and preset: {error}\n");
            await ShowCustomProfileStatusAsync("模式操作未完成；当前状态以硬件回读为准", WindowSurface.XamlRoot);
        }
        finally
        {
            if (ReferenceEquals(pendingModePresetCancellation, presetPreparation)) pendingModePresetCancellation = null;
            isModeCommandPending = false;
            HomeModeBar.IsCommandPending = false;
            trayQuickConsole?.SetModeBusy(false);
            if (homeSession.State is { } current) ApplyHomeState(current);
            RestoreConfirmedModeVisuals();
            ResumeQueuedModeRequest();
        }
    }

    private void ResumeQueuedModeRequest()
    {
        if (queuedModeRequest is null && queuedTurboTier is null || customActivationPending || isModeCommandPending || allowClose) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (customActivationPending || isModeCommandPending || allowClose) return;
            if (queuedTurboTier is { } tier)
            {
                queuedTurboTier = null;
                queuedModeRequest = null;
                OnTurboTierRequested(tier);
            }
            else if (queuedModeRequest is { } mode)
            {
                queuedModeRequest = null;
                OnModeRequested(mode);
            }
        });
    }

    private async Task ApplyPendingModesAsync()
    {
        isModeCommandPending = true;
        HomeModeBar.IsCommandPending = true;
        if (modeReconciliation.Current is { } pendingMode)
            PreviewModeVisuals(pendingMode.Mode switch
            {
                PrototypePerformanceMode.Office => ControlModeId.Office,
                PrototypePerformanceMode.Turbo => ControlModeId.Turbo,
                _ => ControlModeId.Gaming
            });
        try
        {
            await ExitTurboBranchAsync(lifetimeCancellation.Token);
            while (true)
            {
                var request = modeReconciliation.Current;
                if (request is null) return;
                if (!appliedMode.ShouldApply(request.Mode))
                {
                    modeReconciliation.AcceptObservation(request.Mode);
                    return;
                }

                var outcome = await modeCommands.ApplyAsync(request.Mode, lifetimeCancellation.Token);
                if (queuedModeRequest is not null || queuedTurboTier is not null)
                {
                    if (outcome.Applied && homeSession.State?.Controls.PerformanceMode == outcome.ContractMode)
                    {
                        state.Mode = PrototypeModeCommandCoordinator.Map(outcome.ContractMode);
                        appliedMode.Confirm(state.Mode);
                    }
                    modeReconciliation.Reject(request);
                    return;
                }
                if (!outcome.Applied)
                {
                    if (!modeReconciliation.IsCurrent(request)) continue;
                    modeReconciliation.Reject(request);
                    trayQuickConsole?.ShowStatus("模式切换未完成；已保留硬件回读状态");
                    return;
                }

                if (modeReconciliation.IsCurrent(request) && homeSession.State is { } confirmed &&
                    confirmed.Controls.PerformanceMode == outcome.ContractMode)
                {
                    modeReconciliation.AcceptObservation(request.Mode);
                    state.Mode = request.Mode;
                    appliedMode.Confirm(request.Mode);
                    PublishConfirmedMode(request.Mode switch
                    {
                        PrototypePerformanceMode.Office => ControlModeId.Office,
                        PrototypePerformanceMode.Gaming => ControlModeId.Gaming,
                        PrototypePerformanceMode.Turbo => ControlModeId.Turbo,
                        _ => null
                    }, null, animate: true);
                    confirmedAutomaticApply = confirmed.Controls.AdaptiveAutomation?.LastApplyUtc;
                    RequestModeVisuals(request.Mode, animate: true);
                }
                trayQuickConsole?.ShowStatus("模式已切换，硬件回读已确认");
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            isModeCommandPending = false;
            HomeModeBar.IsCommandPending = false;
            RestoreConfirmedModeVisuals();
            ResumeQueuedModeRequest();
        }
    }

    private void PreviewModeVisuals(ControlModeId mode)
    {
        var profile = mode is ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3
            ? $"Profile{(int)mode - (int)ControlModeId.Custom1 + 1}" : customProfile;
        var visualMode = PrototypeModeCommandCoordinator.Map(AdaptiveTargetMap.PerformanceModeFor(PresetKey.Create(mode, 2)));
        RequestModeVisuals(visualMode, animate: true, profile);
        Hero.SetBrandingState(userPreferences.LogoStyle, mode, animate: true);
        trayQuickConsole?.PreviewSelection(mode);
    }

    private void RestoreConfirmedModeVisuals()
    {
        if (queuedModeRequest is { } queued)
        {
            PreviewModeVisuals(queued switch
            {
                PrototypePerformanceMode.Office => ControlModeId.Office,
                PrototypePerformanceMode.Turbo => ControlModeId.Turbo,
                PrototypePerformanceMode.Custom => customProfile switch
                {
                    "Profile2" => ControlModeId.Custom2,
                    "Profile3" => ControlModeId.Custom3,
                    _ => ControlModeId.Custom1
                },
                _ => ControlModeId.Gaming
            });
            return;
        }
        RequestModeVisuals(state.Mode, animate: true);
        Hero.SetBrandingState(userPreferences.LogoStyle, confirmedControlMode, animate: true);
        trayQuickConsole?.PreviewSelection(null);
    }

    private void RequestModeVisuals(PrototypePerformanceMode mode, bool animate, string? profile = null)
    {
        var selectedProfile = profile ?? customProfile;
        var scene = activeScene is { } current && current.Mode == mode &&
            (mode != PrototypePerformanceMode.Custom || current.CustomProfile == selectedProfile)
            ? current
            : backgrounds.Select(mode, selectedProfile);
        // Confirmation must neither choose another plate nor snap an in-flight preview.
        if (animate && activeScene?.Key == scene.Key) return;
        var plan = transitions.Begin(activeScene, scene, motion, animate);
        activeScene = scene;

        ApplyModeTransitionPlan(plan);
    }

    private void ApplyModeTransitionPlan(ModeTransitionPlan plan)
    {
        ModeAmbientBackdrop.ApplyTransition(plan);
        HomeModeBar.ApplyMode(plan.Target.Theme, plan.ShouldAnimateVisuals, motion, motionProfile.CardDuration);
        ModeAmbientBackdrop.SetTurboTier(
            plan.Target.Mode == PrototypePerformanceMode.Turbo,
            turboTier,
            plan.ShouldAnimateVisuals && motion.Translation > 0 ? TimeSpan.FromMilliseconds(220) : TimeSpan.Zero);
        Hero.ApplyMode(plan.Target.Theme, turboTier, plan.Target.CustomProfile, plan.ShouldAnimateVisuals ? motion.Mode : TimeSpan.Zero);
        HomeMonitorGrid.ApplyTemperatureWall(plan.Target.Theme.TemperatureWallC);
        StartModeColorTransition(plan);
    }

    private void StartModeColorTransition(ModeTransitionPlan plan)
    {
        StopModeColorTransition();
        var targets = plan.BrushTargets
            .Select(target => (target.Key, Color: WithAlpha(ParseHex(target.Hex), target.Alpha)))
            .ToArray();
        modeColorKeys = targets.Select(target => target.Key).ToArray();
        modeColorTransition.Begin(
            targets.Select(target => Brush(target.Key).Color).ToArray(),
            targets.Select(target => target.Color).ToArray());

        if (!plan.ShouldAnimateVisuals || motion.Mode <= TimeSpan.Zero)
        {
            ApplyModeColors(modeColorTransition.Sample(1));
            return;
        }

        modeColorDuration = userPreferences.LogoStyle == "explore" ? TimeSpan.FromMilliseconds(220) : motion.Mode;
        modeColorClock.Restart();
        CompositionTarget.Rendering += OnModeColorRendering;
        modeColorRenderingSubscribed = true;
    }

    private void OnModeColorRendering(object? sender, object args)
    {
        if (!modeColorRenderingSubscribed || modeColorDuration <= TimeSpan.Zero) return;
        var progress = Math.Clamp(modeColorClock.Elapsed.TotalMilliseconds / modeColorDuration.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        ApplyModeColors(modeColorTransition.Sample(eased));
        if (progress >= 1)
        {
            ApplyModeColors(modeColorTransition.Sample(1));
            StopModeColorTransition();
        }
    }

    private void ApplyModeColors(IReadOnlyList<Color> colors)
    {
        for (var index = 0; index < modeColorKeys.Length; index++)
            Brush(modeColorKeys[index]).Color = colors[index];
    }

    private void StopModeColorTransition()
    {
        if (modeColorRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnModeColorRendering;
            modeColorRenderingSubscribed = false;
        }
        modeColorClock.Stop();
    }

    private void OnCustomProfileRequested(string profile)
    {
        if (customActivationPending || isModeCommandPending || allowClose) return;
        ControlModeId? mode = profile switch { "Profile1" => ControlModeId.Custom1, "Profile2" => ControlModeId.Custom2, "Profile3" => ControlModeId.Custom3, _ => null };
        if (mode is null) return;
        var key = RememberedPerformancePreset(mode.Value);
        if (key is null) { ManagePerformancePresets(mode.Value); return; }
        customActivationTask = ApplyPerformancePresetAsync(key.Value);
    }

    private async Task<bool> ApplyPerformancePresetAsync(PresetKey key, Func<bool>? isCurrentRequest = null,
        CancellationToken queueCancellationToken = default)
    {
        if (key.Mode == ControlModeId.Turbo && turboBranch?.ActiveTier is not null)
        {
            await ShowCustomProfileStatusAsync("静音/极限狂飙使用内置策略；切回普通狂飙后可应用预设", WindowSurface.XamlRoot);
            return false;
        }
        PresetKey.Create(key.Mode, key.Slot);
        if (!await PauseAdaptiveForManualControlAsync()) return false;
        if (isCurrentRequest?.Invoke() == false) return false;
        var targetMode = AdaptiveTargetMap.PerformanceModeFor(key);
        homeSession.SupersedeAutomaticRestore();
        customActivationPending = true;
        HomeModeBar.IsCommandPending = true;
        PreviewModeVisuals(key.Mode);
        trayQuickConsole?.SetModeBusy(true);
        try
        {
            await ExitTurboBranchAsync(lifetimeCancellation.Token);
            var root = trayQuickConsole is { } visiblePopup && visiblePopup.AppWindow.IsVisible
                ? visiblePopup.PopupXamlRoot ?? WindowSurface.XamlRoot : WindowSurface.XamlRoot;
            if (await new ControlPresetStore().LoadAsync(ControlPageId.Performance, key, CancellationToken.None) is null)
            {
                string message = $"请先在性能页保存 {key.Mode} 的预设 {key.Slot}";
                await ShowCustomProfileStatusAsync(message, root);
                return false;
            }
            // The service applies and verifies the complete CPU preset as one transaction.
            bool applied = await PerformanceWorkspaceV2Preview.ApplyStoredPresetAsync(key, root, isCurrentRequest: isCurrentRequest,
                queueCancellationToken: queueCancellationToken);
            if (isCurrentRequest?.Invoke() == false) return false;
            if (!applied)
            {
                if (trayQuickConsole?.AppWindow.IsVisible == true)
                    trayQuickConsole.ShowStatus(PerformanceWorkspaceV2Preview.LastPresetFeedback);
                return false;
            }
            var controls = homeSession.State?.Controls;
            if (controls?.CpuTuning?.OemCustomPowerMode != true)
            {
                await ShowCustomProfileStatusAsync("自定义功耗未读回确认；预设未标记为使用", root);
                return false;
            }
            PageWorkspace.SetLightingPresetTarget(key);
            state.Mode = PrototypeModeCommandCoordinator.Map(targetMode);
            modeReconciliation.Request(state.Mode);
            modeReconciliation.AcceptObservation(state.Mode);
            ConfirmPerformancePreset(key, animate: true);
            trayQuickConsole?.ShowStatus("");
            return true;
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Custom profile: {error}\n");
            trayQuickConsole?.ShowStatus("预设应用失败，请重新读取硬件状态");
            return false;
        }
        finally
        {
            customActivationPending = false;
            HomeModeBar.IsCommandPending = isModeCommandPending;
            trayQuickConsole?.SetModeBusy(isModeCommandPending);
            if (homeSession.State is { } current) ApplyHomeState(current);
            RestoreConfirmedModeVisuals();
            ResumeQueuedModeRequest();
        }
    }

    private async Task ShowCustomProfileStatusAsync(string message, XamlRoot root)
    {
        if (trayQuickConsole is { } popup && popup.AppWindow.IsVisible)
        {
            popup.ShowStatus(message);
            return;
        }
        if (!allowClose)
            await PerformanceWorkspaceV2Preview.ShowPresetStatusAsync(message);
    }

    private void OnTurboTierRequested(string tier)
    {
        if (allowClose || tier is not ("Normal" or "Quiet" or "Extreme")) return;
        ++manualModeRevision;
        pendingModePresetCancellation?.Cancel();
        queuedModeRequest = null;
        if (customActivationPending || isModeCommandPending)
        {
            queuedTurboTier = tier;
            HomeModeBar.SetConfirmedTurboTier(turboTier);
            return;
        }
        queuedTurboTier = null;
        modeCommandTask = ApplyTurboTierAsync(tier);
    }

    private int manualModeRevision;
    private CancellationTokenSource? pendingModePresetCancellation;
    private Task<bool>? manualPauseTask;
    private Task<bool> PauseAdaptiveForManualControlAsync()
    {
        if (manualPauseTask is { IsCompleted: false } pending) return pending;
        return manualPauseTask = PauseAdaptiveAsync();
    }

    private async Task<bool> PauseAdaptiveAsync()
    {
        while (adaptiveActivationPending || strategyActivationPending)
        {
            if (allowClose) return false;
            await Task.Delay(40);
        }
        if (!adaptiveModeEnabled) return true;
        adaptiveActivationPending = true;
        Hero.ApplyAdaptiveModeState(false, available: true, pending: true);
        trayQuickConsole?.ApplyAdaptiveState(false, available: true, pending: true);
        try
        {
            homeSession.SupersedeAutomaticRestore();
            if (!await PageWorkspace.SetAutomationEnabledConfirmedAsync(false))
            {
                OnRestoreWarning("自适应未能暂停，手动调整未提交；请检查连接");
                return false;
            }
            userPreferences = preferences.Load();
            adaptiveModeEnabled = false;
            return true;
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Pause adaptive for manual control: {error}\n");
            OnRestoreWarning("自适应暂停结果未知，手动调整未提交");
            return false;
        }
        finally
        {
            adaptiveActivationPending = false;
            bool available = homeSession.Status == HomeSessionStatus.Connected;
            Hero.ApplyAdaptiveModeState(adaptiveModeEnabled, available);
            trayQuickConsole?.ApplyAdaptiveState(adaptiveModeEnabled, available);
        }
    }

    private async void OnAdaptiveModeChanged(bool enabled)
    {
        if (adaptiveActivationPending || strategyActivationPending) return;
        if (isModeCommandPending || customActivationPending)
        {
            Hero.ApplyAdaptiveModeState(adaptiveModeEnabled, available: true);
            trayQuickConsole?.ApplyAdaptiveState(adaptiveModeEnabled, available: true);
            OnRestoreWarning("正在切换模式，请稍后启用自适应");
            return;
        }
        adaptiveActivationPending = true;
        Hero.ApplyAdaptiveModeState(enabled, available: true, pending: true);
        trayQuickConsole?.ApplyAdaptiveState(enabled, available: true, pending: true);
        try
        {
            homeSession.SupersedeAutomaticRestore();
            if (enabled && turboBranch?.ActiveTier is not null)
                await ExitTurboBranchAsync(lifetimeCancellation.Token);
            if (await PageWorkspace.SetAutomationEnabledConfirmedAsync(enabled))
            {
                userPreferences = preferences.Load();
                adaptiveModeEnabled = enabled;
                trayQuickConsole?.ShowStatus("");
            }
            else
            {
                trayQuickConsole?.ShowStatus("自适应配置未确认，已保留原状态");
                OnRestoreWarning("自适应配置未确认，已保留原状态");
            }
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Adaptive toggle: {error}\n");
            trayQuickConsole?.ShowStatus("自适应配置未确认，已保留原状态");
            OnRestoreWarning("自适应配置未确认，已保留原状态");
        }
        finally
        {
            adaptiveActivationPending = false;
            bool available = homeSession.Status == HomeSessionStatus.Connected;
            Hero.ApplyAdaptiveModeState(adaptiveModeEnabled, available);
            trayQuickConsole?.ApplyAdaptiveState(adaptiveModeEnabled, available);
        }
    }

    private void OnRestoreWarning(string message) => DispatcherQueue.TryEnqueue(() =>
    {
        if (allowClose || lifetimeCancellation.IsCancellationRequested || string.IsNullOrWhiteSpace(message)) return;
        restoreWarningTimer ??= DispatcherQueue.CreateTimer();
        restoreWarningTimer.IsRepeating = false;
        restoreWarningTimer.Interval = TimeSpan.FromSeconds(8);
        restoreWarningTimer.Tick -= DismissRestoreWarning;
        restoreWarningTimer.Tick += DismissRestoreWarning;
        restoreWarningTimer.Stop();
        RestoreInfo.Message = message;
        RestoreInfo.IsOpen = true;
        restoreWarningTimer.Start();
    });

    private void DismissRestoreWarning(DispatcherQueueTimer sender, object args) => RestoreInfo.IsOpen = false;

    private void OnStrongCoolingChanged(bool enabled)
    {
        if (trayFanCommandPending)
        {
            trayQuickConsole?.ShowStatus("正在调整风扇，请稍后切换强冷");
            if (homeSession.State is { } state) ApplyHomeState(state);
            return;
        }
        if (pendingQuickSettings.ShouldPreserve(QuickSettingKind.StrongCooling, null)) return;
        var requestVersion = pendingQuickSettings.Set(QuickSettingKind.StrongCooling, enabled);
        trayQuickConsole?.SetQuickSettingBusy(QuickSettingKind.StrongCooling, true);
        strongCoolingCommandTask = strongCoolingCommands.RequestAsync(enabled, lifetimeCancellation.Token);
        _ = ObserveStrongCoolingCommandAsync(strongCoolingCommandTask, requestVersion);
    }

    private void OnQuickSettingChanged(QuickSettingKind setting, bool enabled)
    {
        if (setting == QuickSettingKind.WinKey)
        {
            if (!windowsKeyLock.TrySetEnabled(enabled))
            {
                Sidebar.ApplyQuickSettingState(setting, windowsKeyLock.IsEnabled, true);
            }
            else
            {
                Sidebar.ApplyQuickSettingState(setting, windowsKeyLock.IsEnabled, true);
            }
            trayQuickConsole?.ApplyQuickSettingState(setting, windowsKeyLock.IsEnabled);
            trayQuickConsole?.SetQuickSettingBusy(setting, false);
            trayQuickConsole?.ShowStatus(windowsKeyLock.IsEnabled == enabled ? "" : "Win 键未应用，已保留原状态");
            return;
        }

        if (setting is QuickSettingKind.NumLock or QuickSettingKind.CapsLock)
        {
            pendingQuickSettings.Set(setting, enabled);
            if (!keyboardIndicators.TryToggle(setting))
            {
                pendingQuickSettings.Clear(setting);
                ApplyKeyboardIndicatorState(setting);
            }
            else
                DispatcherQueue.TryEnqueue(() => ApplyKeyboardIndicatorState(setting));
            return;
        }

        if (setting == QuickSettingKind.StrongCooling)
        {
            OnStrongCoolingChanged(enabled);
            return;
        }

        if (pendingQuickSettings.ShouldPreserve(setting, null)) return;
        var requestVersion = pendingQuickSettings.Set(setting, enabled);
        trayQuickConsole?.SetQuickSettingBusy(setting, true);
        _ = ApplyQuickSettingAsync(setting, enabled, requestVersion);
    }

    private void OnQuickActionRequested(QuickSettingKind setting)
    {
        if (setting != QuickSettingKind.DisplayOff) return;
        displayPowerController.TryTurnOff();
    }

    private async Task ObserveStrongCoolingCommandAsync(Task commandTask, long requestVersion)
    {
        bool confirmed = false;
        try
        {
            await commandTask;
            confirmed = true;
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch { }
        finally { DispatcherQueue.TryEnqueue(() =>
        {
            RestoreStrongCoolingState(requestVersion);
            if (!allowClose) trayQuickConsole?.ShowStatus(confirmed ? "" : "强冷未应用，已保留原状态");
        }); }
    }

    private async Task ApplyStrongCoolingAsync(bool enabled, CancellationToken cancellationToken)
    {
        var result = await homeSession.ExecuteAsync(
            new SetStrongCoolingCommand(Guid.NewGuid(), enabled),
            cancellationToken);
        if (result.State == CommandState.Applied && result.Error is null && homeSession.State?.Controls.StrongCooling == enabled) return;
        throw new InvalidOperationException("Strong cooling command was not applied.");
    }

    private void RestoreStrongCoolingState(long requestVersion)
    {
        if (!pendingQuickSettings.Clear(QuickSettingKind.StrongCooling, requestVersion)) return;
        trayQuickConsole?.SetQuickSettingBusy(QuickSettingKind.StrongCooling, false);
        var snapshot = homeSession.State;
        var enabled = snapshot?.Controls.StrongCooling;
        var available = snapshot is not null && HasCapability(snapshot, "strongCooling");
        ApplySharedQuickSettingState(QuickSettingKind.StrongCooling, enabled, available);
    }

    private async Task ApplyQuickSettingAsync(QuickSettingKind setting, bool enabled, long requestVersion)
    {
        bool confirmed = false;
        try
        {
            var hardwareEnabled = HomeQuickSettingSemantics.ToHardwareEnabled(setting, enabled);
            var result = await homeSession.ExecuteAsync(new SetQuickSettingCommand(Guid.NewGuid(), setting, hardwareEnabled), lifetimeCancellation.Token);
            confirmed = result.State == CommandState.Applied && result.Error is null &&
                homeSession.State?.Controls.QuickSettings.FirstOrDefault(item => item.Setting == setting)?.Enabled == hardwareEnabled;
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error) { AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Quick setting {setting}: {error}\n"); }
        finally { DispatcherQueue.TryEnqueue(() =>
        {
            RestoreQuickSettingState(setting, requestVersion);
            if (!allowClose) trayQuickConsole?.ShowStatus(confirmed ? "" : "快捷操作未应用，已保留原状态");
        }); }
    }

    private void RestoreQuickSettingState(QuickSettingKind setting, long requestVersion)
    {
        if (!pendingQuickSettings.Clear(setting, requestVersion)) return;
        trayQuickConsole?.SetQuickSettingBusy(setting, false);
        var snapshot = homeSession.State;
        var status = snapshot?.Controls.QuickSettings.FirstOrDefault(item => item.Setting == setting);
        var available = snapshot is not null && HasCapability(snapshot, CapabilityKey(setting));
        ApplySharedQuickSettingState(setting, status?.Enabled, available);
    }

    private static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    private static Color ParseHex(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, WindowProcedure callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, WindowProcedure callback, nuint id);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    private void RestoreMainWindowIfMinimized()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero && IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint MonitorInfofPrimary = 1;
    private const int SwShownormal = 1;

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr clip,
        MonitorEnumProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, nuint referenceData);

    private delegate bool MonitorEnumProcedure(IntPtr monitor, IntPtr hdc, IntPtr monitorRect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public NativeRect(int left, int top, int right, int bottom) => (Left, Top, Right, Bottom) = (left, top, right, bottom);
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private void ToggleFitToWorkArea()
    {
        if (!fittedToWorkArea)
        {
            restoredClientSize = AppWindow.ClientSize;
            restoredPosition = AppWindow.Position;
            ResizeToWorkArea(1);
            fittedToWorkArea = true;
            return;
        }

        AppWindow.ResizeClient(restoredClientSize);
        AppWindow.Move(restoredPosition);
        fittedToWorkArea = false;
    }

}
