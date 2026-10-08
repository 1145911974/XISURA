using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Jiaolong_ControlCenter.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new(new ControlCenterClient());

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            var snapshot = await ViewModel.LoadAsync(CancellationToken.None);
            StartupToggle.IsOn = snapshot.StartWithWindows;
            ThemeComboBox.SelectedIndex = snapshot.Preferences.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0
            };
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取设置失败：{exception.Message}";
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs args)
    {
        try { ViewModel.SetStartupEnabled(StartupToggle.IsOn); }
        catch (Exception exception) { StatusText.Text = $"更新开机启动失败：{exception.Message}"; }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ThemeComboBox.SelectedIndex < 0) return;
        var theme = ThemeComboBox.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        await SavePreferencesAsync(theme);
    }

    private async Task SavePreferencesAsync(string? theme)
    {
        try
        {
            var current = ViewModel.Preferences;
            await ViewModel.SavePreferencesAsync(
                current with { Theme = theme ?? current.Theme },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存用户偏好失败：{exception.Message}";
        }
    }

    private void OnRepairNotesClick(object sender, RoutedEventArgs args) => RepairExpander.IsExpanded = true;

    private void OnOpenInstallerFolderClick(object sender, RoutedEventArgs args)
    {
        try { ViewModel.OpenOfficialInstallerFolder(); }
        catch (Exception exception) { StatusText.Text = $"打开本地依赖目录失败：{exception.Message}"; }
    }

    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var mainWindow = MainWindow.Instance ?? throw new InvalidOperationException("Main window handle is unavailable.");
            var picker = new FileSavePicker(mainWindow.AppWindow.Id)
            {
                SuggestedFileName = $"Jiaolong-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                DefaultFileExtension = ".zip"
            };
            picker.FileTypeChoices.Add("诊断压缩包", new List<string> { ".zip" });
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;

            var export = await ViewModel.ExportDiagnosticsAsync(CancellationToken.None);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination.Path)!);
            var file = await folder.CreateFileAsync(Path.GetFileName(destination.Path), Windows.Storage.CreationCollisionOption.ReplaceExisting);
            var copy = await ViewModel.CopyExportToAsync(export, file, CancellationToken.None);
            StatusText.Text = copy.StagingCleanupConfirmed
                ? "诊断包已导出到用户选择的位置。"
                : "诊断包已导出；服务端暂存副本未能确认清理，请检查硬件服务版本和连接。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"导出诊断包失败：{exception.Message}";
        }
    }
}
