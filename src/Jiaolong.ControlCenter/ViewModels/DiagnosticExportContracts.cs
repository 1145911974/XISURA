using Jiaolong.Diagnostics;

namespace Jiaolong_ControlCenter.ViewModels;

public sealed record DiagnosticExportRequest(Guid OperationId);

public interface IDiagnosticExportClient
{
    Task<DiagnosticExport> StageDiagnosticsAsync(DiagnosticExportRequest request, CancellationToken cancellationToken);
    Task DeleteStagedDiagnosticsAsync(DiagnosticExport export, CancellationToken cancellationToken);
}
