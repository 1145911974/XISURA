using Jiaolong_ControlCenter.Pages;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.UiTests;

[TestClass]
public sealed class SettingsPageSmokeTests
{
    [TestMethod]
    public void Settings_page_exposes_local_only_diagnostics_sections()
    {
        Assert.IsNotNull(typeof(SettingsPage));
        CollectionAssert.AreEqual(
            new[] { "应用行为", "设备兼容", "官方依赖", "服务健康", "诊断" },
            new SettingsViewModel(new NoopDiagnosticExportClient()).SectionNames.ToArray());
    }

    private sealed class NoopDiagnosticExportClient : IDiagnosticExportClient
    {
        public Task<Jiaolong.Diagnostics.DiagnosticExport> StageDiagnosticsAsync(
            DiagnosticExportRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteStagedDiagnosticsAsync(
            Jiaolong.Diagnostics.DiagnosticExport export,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
