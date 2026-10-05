using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using Jiaolong_ControlCenter.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Jiaolong_ControlCenter;

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly ShellViewModel shell = new();
    private readonly NavigationService navigation;
    private readonly UserPreferencesStore preferences = new();
    private readonly ControlCenterClient client = new();
    private readonly TrayIconService tray;
    private int exitRequested;
    private bool allowClose;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        navigation = new NavigationService(shell);
        navigation.Navigated += OnNavigated;
        tray = new TrayIconService(HandleTrayCommand);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RootFrame.Navigate(typeof(HomePage), shell.SelectedDestination);
        ShellNavigation.SelectedItem = ShellNavigation.MenuItems[0];
        SizeChanged += OnWindowSizeChanged;
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string id)
        {
            var destination = ShellDestination.All.First(x => x.Id == id);
            navigation.Navigate(destination);
        }
    }

    private void OnNavigated(object? sender, ShellDestination destination) =>
        RootFrame.Navigate(destination.Id switch
        {
            "Home" => typeof(HomePage),
            "Performance" => typeof(PerformancePage),
            "Gpu" => typeof(GpuPage),
            "Fan" => typeof(FanPage),
            "Lighting" => typeof(LightingPage),
            "Automation" => typeof(AutomationPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(MainPage)
        }, destination);

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        ShellNavigation.OpenPaneLength = args.Size.Width switch
        {
            >= 1600 => 232,
            >= 1366 => 208,
            _ => 192
        };
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        if (preferences.MinimizeToTrayOnClose)
        {
            tray.Show(WindowNative.GetWindowHandle(this));
            AppWindow.Hide();
            return;
        }

        _ = ReleaseAndExitAsync();
    }

    private void HandleTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                AppWindow.Show();
                break;
            case TrayCommand.ReleaseFanControl:
                _ = ReleaseAsync();
                break;
            case TrayCommand.Exit:
                _ = ReleaseAndExitAsync();
                break;
        }
    }

    private async Task ReleaseAndExitAsync()
    {
        if (Interlocked.Exchange(ref exitRequested, 1) != 0) return;
        if (!await ReleaseAsync())
        {
            Interlocked.Exchange(ref exitRequested, 0);
            AppWindow.Show();
            return;
        }

        tray.Dispose();
        await client.DisposeAsync();
        allowClose = true;
        AppWindow.Destroy();
    }

    private async Task<bool> ReleaseAsync()
    {
        try
        {
            var result = await client.SendAsync<ReleaseFanControlCommand, CommandResult>(
                new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested),
                CancellationToken.None);
            return result.Error is null;
        }
        catch
        {
            return false;
        }
    }
}
