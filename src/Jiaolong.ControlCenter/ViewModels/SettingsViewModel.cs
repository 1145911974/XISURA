using Jiaolong.Diagnostics;
using Jiaolong_ControlCenter.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Windows.Storage;

namespace Jiaolong_ControlCenter.ViewModels;

public sealed record SettingsSnapshot(
    UserPreferences Preferences,
    bool StartWithWindows,
    string Fingerprint,
    string BiosVersion,
    string ManifestId,
    string SafeModeReason,
    string ServiceName,
    string DriverName,
    string InstalledVersion,
    string AllowedVersionRange,
    string SignatureStatus,
    string ServiceState,
    string ServiceUptime,
    string CircuitState,
    string LastRecovery);

public sealed record DiagnosticExportRequest(Guid OperationId);

public sealed record DiagnosticExportCopyResult(bool StagingCleanupConfirmed);

public interface IDiagnosticExportClient
{
    Task<DiagnosticExport> StageDiagnosticsAsync(DiagnosticExportRequest request, CancellationToken cancellationToken);
    Task DeleteStagedDiagnosticsAsync(DiagnosticExport export, CancellationToken cancellationToken);
}

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IDiagnosticExportClient diagnosticClient;
    private readonly UserPreferencesStore preferences;
    private readonly IStartupRegistration startup;
    private DiagnosticExport? stagedExport;

    public SettingsViewModel(
        IDiagnosticExportClient diagnosticClient,
        UserPreferencesStore? preferences = null,
        IStartupRegistration? startup = null)
    {
        this.diagnosticClient = diagnosticClient;
        this.preferences = preferences ?? new UserPreferencesStore();
        this.startup = startup ?? new StartupRegistrationService();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public UserPreferences Preferences { get; private set; } = new(false, "system", false);
    public bool StartWithWindows { get; private set; }
    public string RepairNotes { get; } = "查看当前程序安装目录中的程序与依赖文件。";
    public string OfficialInstallerFolder => AppContext.BaseDirectory;
    public IReadOnlyList<string> SectionNames { get; } = ["应用行为", "设备兼容", "官方依赖", "服务健康", "诊断"];
    public IReadOnlyList<string> AllowedDiagnosticEntries { get; } =
    [
        "versions.json",
        "fingerprint.json",
        "capabilities.json",
        "service-health.json",
        "telemetry-60s.json",
        "logs.ndjson",
        "checksums.sha256"
    ];

    public async Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        Preferences = await preferences.LoadAsync(cancellationToken);
        StartWithWindows = startup.IsEnabled;
        OnPropertyChanged(nameof(Preferences));
        OnPropertyChanged(nameof(StartWithWindows));
        return new SettingsSnapshot(
            Preferences,
            StartWithWindows,
            "未知",
            "未知",
            "未知",
            "能力证据不足；保持只读",
            "JiaolongControlService",
            "未知",
            "未知",
            "未知",
            "未知",
            "未知",
            "未知",
            "未知",
            "未知");
    }

    public async Task SavePreferencesAsync(UserPreferences value, CancellationToken cancellationToken)
    {
        Preferences = await Task.Run(() => preferences.Update(current => current with
        {
            MinimizeToTrayOnClose = value.MinimizeToTrayOnClose,
            Theme = value.Theme,
            ReduceMotion = value.ReduceMotion
        }), cancellationToken);
        OnPropertyChanged(nameof(Preferences));
    }

    public void SetStartupEnabled(bool enabled)
    {
        if (enabled) startup.Enable();
        else startup.Disable();
        StartWithWindows = enabled;
        OnPropertyChanged(nameof(StartWithWindows));
    }

    public async Task<DiagnosticExport> ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var request = new DiagnosticExportRequest(Guid.NewGuid());
        stagedExport = await diagnosticClient.StageDiagnosticsAsync(request, cancellationToken);
        return stagedExport;
    }

    public async Task<DiagnosticExportCopyResult> CopyExportToAsync(
        DiagnosticExport export,
        StorageFile destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            await using var source = new FileStream(export.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            await using var target = await destination.OpenStreamForWriteAsync();
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Diagnostic export copy step failed; staged export was retained.", exception);
        }

        try
        {
            await diagnosticClient.DeleteStagedDiagnosticsAsync(export, cancellationToken);
            if (ReferenceEquals(stagedExport, export)) stagedExport = null;
            return new DiagnosticExportCopyResult(StagingCleanupConfirmed: true);
        }
        catch (Exception exception) when (exception is ControlCenterServiceException or IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        {
            // The destination is committed; a failed service cleanup must not report the export itself as failed.
            return new DiagnosticExportCopyResult(StagingCleanupConfirmed: false);
        }
    }

    public void OpenOfficialInstallerFolder()
    {
        if (!Directory.Exists(OfficialInstallerFolder))
        {
            throw new DirectoryNotFoundException($"Official installer folder is unavailable: {OfficialInstallerFolder}");
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OfficialInstallerFolder}\"")
        {
            UseShellExecute = true
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
