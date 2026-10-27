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
            allowedOperations ?? ["getSnapshot", "getCapabilities", "getDeviceState", "getDiagnostics", "exportDiagnostics", "deleteDiagnosticExport", "executeCommand", "subscribeTelemetry", "beginClientSession", "configureClientSession", "endClientSession"],
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
        if (request.ProtocolVersion.Major != 1 || request.ProtocolVersion.Minor > 1)
            return Task.FromResult(Error(request, ErrorCode.InvalidFrame));
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

        if (string.Equals(request.Operation, "beginClientSession", StringComparison.Ordinal) && homeRuntime is not null)
            return BeginClientSessionAsync(request, client, cancellationToken);
        if (string.Equals(request.Operation, "configureClientSession", StringComparison.Ordinal) && homeRuntime is not null)
            return ConfigureClientSessionAsync(request, client, cancellationToken);
        if (string.Equals(request.Operation, "endClientSession", StringComparison.Ordinal) && homeRuntime is not null)
            return EndClientSessionAsync(request, client, cancellationToken);

        if (string.Equals(request.Operation, "executeCommand", StringComparison.Ordinal) && (commandOrchestrator is not null || homeRuntime is not null))
        {
            return ExecuteCommandAsync(request, client, cancellationToken);
        }

        return Task.FromResult(Error(request, ErrorCode.ServiceUnavailable));
    }

    private async Task<ResponseEnvelope> EndClientSessionAsync(RequestEnvelope request, ClientIdentity client, CancellationToken token)
    {
        if (request.ProtocolVersion.Minor < 1 || request.Payload.ValueKind != JsonValueKind.Object ||
            request.Payload.EnumerateObject().Any(property => property.Name is not ("sessionId" or "keepChanges")) ||
            request.Payload.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() != 1) ||
            request.Payload.TryGetProperty("keepChanges", out var keep) && keep.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !request.Payload.TryGetProperty("sessionId", out var id) ||
            id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var sessionId) || sessionId == Guid.Empty)
            return Error(request, ErrorCode.InvalidFrame);
        var result = await homeRuntime!.EndClientControlSessionAsync(request.OperationId, sessionId, client, token,
            request.Payload.TryGetProperty("keepChanges", out var policy) && policy.GetBoolean());
        return Success(request, JsonSerializer.SerializeToElement(result, ProtocolJsonContext.Default.CommandResult));
    }

    private async Task<ResponseEnvelope> BeginClientSessionAsync(RequestEnvelope request, ClientIdentity client, CancellationToken token)
    {
        // Process authority comes only from the pipe verifier, never from a JSON payload.
        if (request.ProtocolVersion.Minor < 1 || request.Payload.ValueKind != JsonValueKind.Object ||
            request.Payload.EnumerateObject().Any()) return Error(request, ErrorCode.InvalidFrame);
        var result = await homeRuntime!.BeginClientControlSessionAsync(request.OperationId, client, token);
        return Success(request, JsonSerializer.SerializeToElement(result, ProtocolJsonContext.Default.CommandResult));
    }

    private async Task<ResponseEnvelope> ConfigureClientSessionAsync(RequestEnvelope request, ClientIdentity client, CancellationToken token)
    {
        if (request.ProtocolVersion.Minor < 1) return Error(request, ErrorCode.InvalidFrame);
        ClientSessionExitConfiguration configuration;
        try
        {
            configuration = request.Payload.Deserialize(ProtocolJsonContext.Default.ClientSessionExitConfiguration)
                ?? throw new JsonException("Session configuration is empty.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { return Error(request, ErrorCode.InvalidFrame); }
        var result = await homeRuntime!.ConfigureClientSessionExitAsync(request.OperationId, configuration, client, token);
        return Success(request, JsonSerializer.SerializeToElement(result, ProtocolJsonContext.Default.CommandResult));
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
        if (command.OperationId != request.OperationId || command is ApplyControlRequestCommand apply &&
            (request.ProtocolVersion.Minor < 1 || apply.Request is null || apply.Request.OperationId != command.OperationId ||
             apply.Request.Origin is ControlOrigin.Adaptive or ControlOrigin.StartupRestore or ControlOrigin.TurboBranch))
            return Error(request, ErrorCode.UnauthorizedClient);
        var result = homeRuntime is not null
            ? await homeRuntime.ExecuteAsync(command, cancellationToken, client.ConnectionId, client.UserSid, client)
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
            var ordered = telemetry.OrderBy(sample => sample.CapturedAtUtc).ToArray();
            var gapsMs = ordered.Zip(ordered.Skip(1), (left, right) =>
                (right.CapturedAtUtc - left.CapturedAtUtc).TotalMilliseconds).ToArray();

            var content = new DiagnosticBundleContent(
                JsonSerializer.Serialize(new { service = typeof(RequestDispatcher).Assembly.GetName().Version?.ToString(), runtime = Environment.Version.ToString() }),
                JsonSerializer.Serialize(state.Identity),
                JsonSerializer.Serialize(state.Capabilities),
                JsonSerializer.Serialize(new
                {
                    state.SupportState, state.Reason, capturedAtUtc = DateTimeOffset.UtcNow,
                    controls = state.Controls with { ExecutionState = null, AdaptiveAutomation = state.Controls.AdaptiveAutomation is { } automatic
                        ? automatic with { Reason = AdaptiveAutomationStateStore.DiagnosticReason(automatic.Reason) } : null },
                    controlPipeline = await homeRuntime.ReadControlDiagnosticsAsync(cancellationToken, client.UserSid),
                    telemetryCoverage = new
                    {
                        requestedSeconds = 60, sampleCount = ordered.Length,
                        startUtc = ordered.FirstOrDefault()?.CapturedAtUtc, endUtc = ordered.LastOrDefault()?.CapturedAtUtc,
                        actualSeconds = ordered.Length < 2 ? 0 : (ordered[^1].CapturedAtUtc - ordered[0].CapturedAtUtc).TotalSeconds,
                        maximumGapMs = gapsMs.Length == 0 ? 0 : gapsMs.Max(),
                        gapsOverTwoSeconds = gapsMs.Count(gap => gap > 2000),
                        latestSampleAgeMs = ordered.LastOrDefault() is { } last
                            ? (DateTimeOffset.UtcNow - last.CapturedAtUtc).TotalMilliseconds : (double?)null,
                        latestGpuPowerAgeMs = ordered.LastOrDefault()?.GpuPowerObservedAtUtc is { } gpuAt
                            ? (DateTimeOffset.UtcNow - gpuAt).TotalMilliseconds : (double?)null
                    }
                }),
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
