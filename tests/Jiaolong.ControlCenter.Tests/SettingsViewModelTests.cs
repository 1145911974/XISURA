using System.Text.Json;
using Jiaolong.Contracts.Errors;
using Jiaolong.Diagnostics;
using Jiaolong_ControlCenter.Services;
using Jiaolong_ControlCenter.ViewModels;
using ControlCenterDiagnosticExportRequest = Jiaolong_ControlCenter.ViewModels.DiagnosticExportRequest;
using Windows.Storage;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
    [TestMethod]
    public void Startup_preference_updates_the_registered_user_startup_entry()
    {
        var startup = new RecordingStartupRegistration();
        var vm = new SettingsViewModel(new RecordingDiagnosticClient(), startup: startup);

        vm.SetStartupEnabled(true);
        Assert.IsTrue(startup.IsEnabled);
        Assert.IsTrue(vm.StartWithWindows);

        vm.SetStartupEnabled(false);
        Assert.IsFalse(startup.IsEnabled);
        Assert.IsFalse(vm.StartWithWindows);
        CollectionAssert.AreEqual(new[] { true, false }, startup.Changes);
    }

    [TestMethod]
    public async Task Diagnostic_export_uses_service_staging_then_user_process_copy()
    {
        var client = new RecordingDiagnosticClient();
        var vm = new SettingsViewModel(client);

        await vm.ExportDiagnosticsAsync(CancellationToken.None);

        Assert.IsTrue(client.SentCommands.Single().PayloadPropertyNames.SetEquals(new[] { "operationId" }));
        Assert.IsFalse(client.SentCommands.Single().SerializedPayload.Contains("destination", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Diagnostic_copy_reports_saved_file_when_service_cleanup_fails()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stagedPath = Path.Combine(Path.GetTempPath(), $"Jiaolong-staged-{suffix}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"Jiaolong-export-{suffix}.zip");
        var contents = new byte[] { 1, 2, 3, 5, 8 };
        await File.WriteAllBytesAsync(stagedPath, contents);
        await File.WriteAllBytesAsync(destinationPath, []);

        try
        {
            var unavailable = new ControlCenterServiceException(
                ServiceError.Create(ErrorCode.ServiceUnavailable, Guid.NewGuid(), isRetryable: true));
            var client = new RecordingDiagnosticClient(unavailable);
            var vm = new SettingsViewModel(client);
            var destination = await StorageFile.GetFileFromPathAsync(destinationPath);
            var result = await vm.CopyExportToAsync(
                new DiagnosticExport(stagedPath, [], string.Empty),
                destination,
                CancellationToken.None);

            Assert.IsFalse(result.StagingCleanupConfirmed);
            Assert.AreEqual(1, client.DeleteCalls);
            CollectionAssert.AreEqual(contents, await File.ReadAllBytesAsync(destinationPath));
        }
        finally
        {
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }
}

internal sealed class RecordingStartupRegistration : IStartupRegistration
{
    public bool IsEnabled { get; private set; }
    public List<bool> Changes { get; } = [];

    public void Enable(string? executablePath = null)
    {
        IsEnabled = true;
        Changes.Add(true);
    }

    public void Disable()
    {
        IsEnabled = false;
        Changes.Add(false);
    }
}

internal sealed class RecordingDiagnosticClient : IDiagnosticExportClient
{
    private readonly Exception? deleteFailure;

    public RecordingDiagnosticClient(Exception? deleteFailure = null) => this.deleteFailure = deleteFailure;

    public List<RecordedDiagnosticCommand> SentCommands { get; } = [];
    public int DeleteCalls { get; private set; }

    public Task<DiagnosticExport> StageDiagnosticsAsync(ControlCenterDiagnosticExportRequest request, CancellationToken cancellationToken)
    {
        SentCommands.Add(new(
            new HashSet<string>(StringComparer.Ordinal) { "operationId" },
            JsonSerializer.Serialize(new { request.OperationId })));
        return Task.FromResult(new DiagnosticExport("staged.zip", [], string.Empty));
    }

    public Task DeleteStagedDiagnosticsAsync(DiagnosticExport export, CancellationToken cancellationToken)
    {
        DeleteCalls++;
        return deleteFailure is null ? Task.CompletedTask : Task.FromException(deleteFailure);
    }
}

internal sealed record RecordedDiagnosticCommand(IReadOnlySet<string> PayloadPropertyNames, string SerializedPayload);
