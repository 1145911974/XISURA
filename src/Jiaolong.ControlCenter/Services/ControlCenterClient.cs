using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Diagnostics;
using Jiaolong_ControlCenter.ViewModels;
using ControlCenterDiagnosticExportRequest = Jiaolong_ControlCenter.ViewModels.DiagnosticExportRequest;

namespace Jiaolong_ControlCenter.Services;

public sealed class ControlCenterClient : IAsyncDisposable, IDiagnosticExportClient
{
    public const string PipeName = "Jiaolong.ControlCenter.v1";
    private const int MaxFrameBytes = 1_048_576;
    private readonly string pipeName;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MessageEnvelope>> pending = new();
    private readonly object stateGate = new();
    private NamedPipeClientStream? pipe;
    private CancellationTokenSource? readLoopCancellation;
    private Task? readLoop;
    private Channel<HardwareSnapshot> telemetryChannel = CreateTelemetryChannel();

    public ControlCenterClient(string? pipeName = null) => this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
    internal ControlCenterClient CreatePeer() => new(pipeName);
    public bool IsConnected { get { lock (stateGate) return pipe is { IsConnected: true }; } }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (pipe is { IsConnected: true }) return;
            await DisconnectAsync();

            var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync(10_000, cancellationToken);
                var hello = new HelloEnvelope(new ProtocolVersion(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, "1.0.0");
                await WriteDirectAsync(candidate, hello, cancellationToken);
                var response = await ReadDirectAsync(candidate, cancellationToken);
                if (response is not HelloAckEnvelope { ProtocolVersion.Major: 1 })
                    throw new InvalidDataException("Control service protocol negotiation failed.");

                lock (stateGate)
                {
                    pipe = candidate;
                    readLoopCancellation = new CancellationTokenSource();
                    telemetryChannel = CreateTelemetryChannel();
                    readLoop = ReadLoopAsync(candidate, readLoopCancellation.Token);
                }
            }
            catch
            {
                await candidate.DisposeAsync();
                throw;
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        SendRequestAsync<CapabilitySnapshot>(Guid.NewGuid(), "getCapabilities", EmptyPayload(), TimeSpan.FromSeconds(10), cancellationToken);

    public Task<HomeStateSnapshot> GetHomeStateAsync(CancellationToken cancellationToken) =>
        SendRequestAsync<HomeStateSnapshot>(Guid.NewGuid(), "getDeviceState", EmptyPayload(), TimeSpan.FromSeconds(10), cancellationToken);

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        SendRequestAsync<HardwareSnapshot>(Guid.NewGuid(), "getSnapshot", EmptyPayload(), TimeSpan.FromSeconds(10), cancellationToken);

    public async Task<TResult> SendAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken)
        where TCommand : HardwareCommand
    {
        ArgumentNullException.ThrowIfNull(command);
        var payload = JsonSerializer.SerializeToElement<HardwareCommand>(command, ProtocolJsonContext.Default.HardwareCommand);
        return await SendRequestAsync<TResult>(command.OperationId, "executeCommand", payload, TimeSpan.FromSeconds(10), cancellationToken);
    }

    public Task<CommandResult> SendCommandAsync(HardwareCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SendAsync<HardwareCommand, CommandResult>(command, cancellationToken);
    }

    public Task<DiagnosticExport> StageDiagnosticsAsync(
        ControlCenterDiagnosticExportRequest request,
        CancellationToken cancellationToken) =>
        SendRequestAsync<DiagnosticExport>(
            request.OperationId,
            "exportDiagnostics",
            JsonSerializer.SerializeToElement(request, JsonOptions),
            TimeSpan.FromSeconds(60),
            cancellationToken);

    public async Task DeleteStagedDiagnosticsAsync(DiagnosticExport export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        var exportId = Path.GetFileNameWithoutExtension(export.FilePath);
        if (!Guid.TryParse(exportId, out var operationId))
            throw new ArgumentException("Diagnostic export path does not contain a valid export id.", nameof(export));

        _ = await SendRequestAsync<JsonElement>(
            operationId,
            "deleteDiagnosticExport",
            JsonSerializer.SerializeToElement(new { exportId }, JsonOptions),
            TimeSpan.FromSeconds(10),
            cancellationToken);
    }

    public async IAsyncEnumerable<HardwareSnapshot> SubscribeTelemetryAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
        var request = new RequestEnvelope(
            new ProtocolVersion(1, 0),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Guid.Empty,
            "subscribeTelemetry",
            DateTimeOffset.UtcNow.AddSeconds(10),
            JsonSerializer.SerializeToElement(new { intervalSeconds = 0.5 }, JsonOptions));
        await WriteMessageAsync(request, cancellationToken);

        await foreach (var snapshot in telemetryChannel.Reader.ReadAllAsync(cancellationToken))
            yield return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        await connectionGate.WaitAsync();
        try
        {
            await DisconnectAsync();
        }
        finally
        {
            connectionGate.Release();
            connectionGate.Dispose();
            writeGate.Dispose();
        }
    }

    private async Task<TResult> SendRequestAsync<TResult>(
        Guid operationId,
        string operation,
        JsonElement payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
        var messageId = Guid.NewGuid();
        var completion = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[messageId] = completion;
        try
        {
            await WriteMessageAsync(new RequestEnvelope(
                new ProtocolVersion(1, 0),
                messageId,
                DateTimeOffset.UtcNow,
                operationId,
                operation,
                DateTimeOffset.UtcNow.Add(timeout),
                payload), cancellationToken);

            var response = await completion.Task.WaitAsync(timeout, cancellationToken);
            if (response is not ResponseEnvelope envelope ||
                envelope.Status != ResponseStatus.Success ||
                envelope.Payload is not { } responsePayload)
                throw new ControlCenterServiceException((response as ResponseEnvelope)?.Error);
            return responsePayload.Deserialize<TResult>(JsonOptions)
                ?? throw new InvalidDataException($"Control service returned an empty {operation} response.");
        }
        finally
        {
            pending.TryRemove(messageId, out _);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream activePipe, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && activePipe.IsConnected)
            {
                var message = await ReadDirectAsync(activePipe, cancellationToken);
                switch (message)
                {
                    case ResponseEnvelope response when pending.TryRemove(response.ReplyToMessageId, out var completion):
                        completion.TrySetResult(response);
                        break;
                    case EventEnvelope { EventName: "telemetry.updated" } telemetry:
                        var snapshot = telemetry.Payload.Deserialize<HardwareSnapshot>(JsonOptions);
                        if (snapshot is not null) telemetryChannel.Writer.TryWrite(snapshot);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            foreach (var completion in pending.Values) completion.TrySetException(failure ?? new EndOfStreamException("Control service connection ended."));
            telemetryChannel.Writer.TryComplete(failure);
            lock (stateGate)
            {
                if (ReferenceEquals(pipe, activePipe)) pipe = null;
            }
        }
    }

    private async Task WriteMessageAsync(MessageEnvelope message, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var activePipe = pipe is { IsConnected: true } candidate
                ? candidate
                : throw new InvalidOperationException("Control service is not connected.");
            await WriteDirectAsync(activePipe, message, cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task DisconnectAsync()
    {
        NamedPipeClientStream? activePipe;
        CancellationTokenSource? activeCancellation;
        Task? activeReadLoop;
        lock (stateGate)
        {
            activePipe = pipe;
            activeCancellation = readLoopCancellation;
            activeReadLoop = readLoop;
            pipe = null;
            readLoopCancellation = null;
            readLoop = null;
        }

        activeCancellation?.Cancel();
        if (activePipe is not null) await activePipe.DisposeAsync();
        if (activeReadLoop is not null)
        {
            try { await activeReadLoop; } catch { }
        }
        activeCancellation?.Dispose();
    }

    private static Channel<HardwareSnapshot> CreateTelemetryChannel() =>
        Channel.CreateUnbounded<HardwareSnapshot>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new { }, JsonOptions);

    private static async ValueTask WriteDirectAsync(Stream stream, MessageEnvelope message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.MessageEnvelope);
        if (json.Length == 0 || json.Length > MaxFrameBytes) throw new InvalidDataException("Frame exceeds the protocol limit.");
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, json.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<MessageEnvelope> ReadDirectAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid protocol frame length.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.MessageEnvelope)
            ?? throw new InvalidDataException("Invalid protocol frame.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public sealed class ControlCenterServiceException(ServiceError? error)
    : InvalidOperationException(error?.MessageKey ?? "Control service rejected the request.")
{
    public ServiceError? Error { get; } = error;
}
