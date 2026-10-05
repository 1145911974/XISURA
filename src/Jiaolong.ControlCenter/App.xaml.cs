using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Jiaolong_ControlCenter.Prototype;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Jiaolong_ControlCenter;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private bool restorePending;

    internal void RestoreExistingWindow()
    {
        if (_window is null) { restorePending = true; return; }
        restorePending = false;
        if (_window is PrototypeWindow prototype) prototype.RestoreExistingWindow();
        else
        {
            _window.AppWindow.Show();
            if (_window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.Restore();
            _window.Activate();
        }
    }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        Services.ProcessInputMethod.UseMicrosoftPinyin();
        InitializeComponent();
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] AppDomain Unhandled: {e.ExceptionObject}\n");
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] UnobservedTask: {e.Exception}\n");
            }
            catch { }
        };
        UnhandledException += (s, e) =>
        {
            try
            {
                Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] Unhandled: {e.Message}\n{e.Exception}\n");
            }
            catch { }
        };
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try
            {
                Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] ProcessExit triggered! StackTrace:\n{Environment.StackTrace}\n");
            }
            catch { }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] OnLaunched started\n");
            var commandLine = Environment.GetCommandLineArgs();
            // Placement is also available in installed builds; fixture pages stay debug-only.
            var acceptanceSecondaryDisplay = commandLine.Any(argument =>
                string.Equals(argument, "--accept-secondary", StringComparison.OrdinalIgnoreCase));
#if DEBUG
            var logoLab = commandLine
                .Any(argument => string.Equals(argument, "--logo-lab", StringComparison.OrdinalIgnoreCase));
            var adaptiveCore = logoLab && commandLine
                .Any(argument => string.Equals(argument, "--adaptive-core", StringComparison.OrdinalIgnoreCase));
            var acceptancePage = commandLine
                .Where(argument => argument.StartsWith("--accept-page=", StringComparison.OrdinalIgnoreCase))
                .Select(argument => argument["--accept-page=".Length..])
                .FirstOrDefault();
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] Instantiating window: logoLab={logoLab}, acceptancePage={acceptancePage}\n");
            _window = logoLab
                ? new LogoLabWindow(adaptiveCore)
                : new PrototypeWindow(acceptancePage, acceptanceSecondaryDisplay);
#else
            _window = new PrototypeWindow(acceptanceSecondaryDisplay: acceptanceSecondaryDisplay);
#endif
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] Calling _window.Activate()\n");
            _window.Activate();
            if (_window is PrototypeWindow prototype)
            {
                Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] Calling prototype.EnsureVisible()\n");
                prototype.EnsureVisible();
            }
            if (restorePending) RestoreExistingWindow();
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] OnLaunched completed successfully\n");
        }
        catch (Exception ex)
        {
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTime.Now:O}] OnLaunched exception: {ex}\n");
        }
    }
}
