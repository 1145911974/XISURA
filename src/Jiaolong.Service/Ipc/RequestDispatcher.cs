using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Diagnostics;
using Jiaolong.Service.Commands;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Ipc;

public interface IRequestDispatcher
{
    Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, ClientIdentity client, CancellationToken cancellationToken);
}

public sealed class RequestDispatcher : IRequestDispatcher
{
    private readonly IReadOnlySet<string> allowedOperations;
    private readonly ICommandOrchestrator? commandOrchestrator;
    private readonly HomeServiceRuntime? homeRuntime;
    private readonly IDiagnosticBundleBuilder? diagnosticBundleBuilder;
    private readonly TelemetryRingBuffer? telemetryRingBuffer;
    private readonly IDiagnosticEventReader? diagnosticEventReader;

    public RequestDispatcher(
        IEnumerable<string>? allowedOperations = null,
        ICommandOrchestrator? commandOrchestrator = null,
        HomeServiceRuntime? homeRuntime = null,
        IDiagnosticBundleBuilder? diagnosticBundleBuilder = null,
        TelemetryRingBuffer? telemetryRingBuffer = null,
        IDiagnosticEventReader? diagnosticEventReader = null)
    {
        this.allowedOperations = new HashSet<string>(
            allowedOperations ?? ["getSnapshot", "getCapabilities", "getDeviceState", "getDiagnostics", "exportDiagnostics", "deleteDiagnosticExport", "executeCommand", "subscribeTelemetry"],
            StringComparer.Ordinal);
        this.commandOrchestrator = commandOrchestrator;
        this.homeRuntime = homeRuntime;
        this.diagnosticBundleBuilder = diagnosticBundleBuilder;
        this.telemetryRingBuffer = telemetryRingBuffer;
        this.diagnosticEventReader = diagnosticEventReader;
    }

    public Task<ResponseEnvelope> DispatchAsync(
        RequestEnvelope request,
        ClientIdentity client,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        if (!ClientIdentityVerifier.IsAuthorized(client))
        {
            return Task.FromResult(Error(request, ErrorCode.UnauthorizedClient));
        }

        if (request.DeadlineUtc <= now)
        {
            return Task.FromResult(Error(request, ErrorCode.DeadlineExceeded));
        }

        if (!allowedOperations.Contains(request.Operation))
        {
            return Task.FromResult(Error(request, ErrorCode.ValidationFailed));
        }

        if (string.Equals(request.Operation, "getCapabilities", StringComparison.Ordinal) && homeRuntime is not null)
        {
            return SuccessAsync(request, homeRuntime.GetStateAsync(cancellationToken).ContinueWith(
                task => (object)task.Result.Capabilities, cancellationToken));
        }

        if (string.Equals(request.Operation, "getSnapshot", StringComparison.Ordinal) && homeRuntime is not null)
        {
            return SuccessAsync(request, homeRuntime.ReadTelemetryAsync(cancellationToken).ContinueWith(
                task => (object)task.Result, cancellationToken));
        }

        if (string.Equals(request.Operation, "getDeviceState", StringComparison.Ordinal) && homeRuntime is not null)
        {
            return SuccessAsync(request, homeRuntime.GetHomeStateAsync(cancellationToken).ContinueWith(
                task => (object)task.Result, cancellationToken));
        }

        if (string.Equals(request.Operation, "exportDiagnostics", StringComparison.Ordinal) &&
            homeRuntime is not null && diagnosticBundleBuilder is not null)
        {
            return ExportDiagnosticsAsync(request, client, cancellationToken);
        }

        if (string.Equals(request.Operation, "deleteDiagnosticExport", StringComparison.Ordinal) && diagnosticBundleBuilder is not null)
        {
            return DeleteDiagnosticExportAsync(request, cancellationToken);
        }

        if (string.Equals(request.Operation, "executeCommand", StringComparison.Ordinal) && (commandOrchestrator is not null || homeRuntime is not null))
        {
            return ExecuteCommandAsync(request, client, cancellationToken);
        }

        return Task.FromResult(Error(request, ErrorCode.ServiceUnavailable));
    }

    private async Task<ResponseEnvelope> ExecuteCommandAsync(RequestEnvelope request, ClientIdentity client, CancellationToken cancellationToken)
    {
        HardwareCommand command;
        try
        {
            command = request.Payload.Deserialize(ProtocolJsonContext.Default.HardwareCommand)
                ?? throw new JsonException("Command payload is empty.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Error(request, ErrorCode.InvalidFrame);
        }

        var context = new CommandContext(
            ClientIdentityVerifier.IsAuthorized(client),
            false,
            true,
            true,
            true,
            request.DeadlineUtc,
            request.OperationId);
        var result = homeRuntime is not null
            ? await homeRuntime.ExecuteAsync(command, cancellationToken, client.ConnectionId)
            : await commandOrchestrator!.ExecuteAsync(command, context, cancellationToken);
        return new ResponseEnvelope(
            request.ProtocolVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.MessageId,
            request.OperationId,
            ResponseStatus.Success,
            JsonSerializer.SerializeToElement(result, ProtocolJsonContext.Default.CommandResult),
            null);
    }

    private async Task<ResponseEnvelope> ExportDiagnosticsAsync(
        RequestEnvelope request,
        ClientIdentity client,
        CancellationToken cancellationToken)
    {
        if (!TryReadGuid(request.Payload, "operationId", out var payloadOperationId) ||
            payloadOperationId != request.OperationId)
            return Error(request, ErrorCode.ValidationFailed);

        try
        {
            var state = await homeRuntime!.GetStateAsync(cancellationToken);
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-60);
            var telemetry = telemetryRingBuffer?.Snapshot()
                .Where(snapshot => snapshot.CapturedAtUtc >= cutoff)
                .ToArray() ?? [];
            if (telemetry.Length == 0 && state.Telemetry is not null) telemetry = [state.Telemetry];

            var content = new DiagnosticBundleContent(
                JsonSerializer.Serialize(new { service = typeof(RequestDispatcher).Assembly.GetName().Version?.ToString(), runtime = Environment.Version.ToString() }),
                JsonSerializer.Serialize(state.Identity),
                JsonSerializer.Serialize(state.Capabilities),
                JsonSerializer.Serialize(new { state.SupportState, state.Reason, capturedAtUtc = DateTimeOffset.UtcNow }),
                JsonSerializer.Serialize(telemetry),
                diagnosticEventReader is null
                    ? string.Empty
                    : await diagnosticEventReader.ReadRecentAsync(500, cancellationToken));
            var export = await diagnosticBundleBuilder!.BuildAsync(
                new DiagnosticExportRequest(client.UserSid, content), cancellationToken);
            return Success(request, export);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Error(request, ErrorCode.ServiceUnavailable);
        }
    }

    private async Task<ResponseEnvelope> DeleteDiagnosticExportAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        if (!TryReadGuid(request.Payload, "exportId", out var exportId) ||
            exportId == Guid.Empty || exportId != request.OperationId)
            return Error(request, ErrorCode.ValidationFailed);

        try
        {
            await diagnosticBundleBuilder!.DeleteAsync(exportId, cancellationToken);
            return Success(request, JsonSerializer.SerializeToElement(new { deleted = true }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Error(request, ErrorCode.ServiceUnavailable);
        }
    }

    private static bool TryReadGuid(JsonElement payload, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        return payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(propertyName, out var property) &&
            property.TryGetGuid(out value);
    }

    private static async Task<ResponseEnvelope> SuccessAsync(RequestEnvelope request, Task<object> payloadTask)
    {
        var payload = await payloadTask;
        return Success(request, payload);
    }

    private static ResponseEnvelope Success(RequestEnvelope request, object payload) =>
        new(
            request.ProtocolVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.MessageId,
            request.OperationId,
            ResponseStatus.Success,
            payload switch
            {
                CapabilitySnapshot capabilities => JsonSerializer.SerializeToElement(capabilities, ProtocolJsonContext.Default.CapabilitySnapshot),
                HardwareSnapshot snapshot => JsonSerializer.SerializeToElement(snapshot, ProtocolJsonContext.Default.HardwareSnapshot),
                HomeStateSnapshot state => JsonSerializer.SerializeToElement(state, ProtocolJsonContext.Default.HomeStateSnapshot),
                JsonElement element => element,
                _ => JsonSerializer.SerializeToElement(payload)
            },
            null);

    private static ResponseEnvelope Error(RequestEnvelope request, ErrorCode code) =>
        new(
            request.ProtocolVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.MessageId,
            request.OperationId,
            ResponseStatus.Error,
            null,
            ServiceError.Create(code, request.OperationId, code is ErrorCode.DeadlineExceeded or ErrorCode.ServiceUnavailable));
}
