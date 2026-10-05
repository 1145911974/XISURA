using System.IO.Pipes;
using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Diagnostics;
using Jiaolong.Service.Home;
using Microsoft.Extensions.Logging;

namespace Jiaolong.Service.Ipc;

public sealed class NamedPipeServiceHost : IAsyncDisposable
{
    public const string DefaultPipeName = "Jiaolong.ControlCenter.v1";
    private static readonly TimeSpan DefaultTelemetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumTelemetryInterval = TimeSpan.FromSeconds(5);

    private readonly string pipeName;
    private readonly IRequestDispatcher dispatcher;
    private readonly HomeServiceRuntime? homeRuntime;
    private readonly TelemetryRingBuffer? telemetryRingBuffer;
    private readonly IClientIdentityVerifier identityVerifier;
    private readonly IFrameCodec codec;
    private readonly ILogger<NamedPipeServiceHost>? logger;
    private readonly object gate = new();
    private NamedPipeServerStream? server;

    public NamedPipeServiceHost(
        string? pipeName = null,
        IRequestDispatcher? dispatcher = null,
        HomeServiceRuntime? homeRuntime = null,
        IClientIdentityVerifier? identityVerifier = null,
        IFrameCodec? codec = null,
        ILogger<NamedPipeServiceHost>? logger = null,
        TelemetryRingBuffer? telemetryRingBuffer = null)
    {
        this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        this.homeRuntime = homeRuntime;
        this.telemetryRingBuffer = telemetryRingBuffer;
        this.dispatcher = dispatcher ?? new RequestDispatcher(homeRuntime: homeRuntime);
        this.identityVerifier = identityVerifier ?? new ClientIdentityVerifier();
        this.codec = codec ?? new FrameCodec();
        this.logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? candidate = null;
            try
            {
                candidate = CreateServer();
                lock (gate) server = candidate;
                await candidate.WaitForConnectionAsync(cancellationToken);
                logger?.LogDebug("Control service accepted a pipe client.");
                _ = HandleClientAsync(candidate, cancellationToken, keepClientConnected: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (candidate is not null) await candidate.DisposeAsync();
                break;
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Control service failed while accepting a pipe client; retrying.");
                if (candidate is not null) await candidate.DisposeAsync();
                try { await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
            finally
            {
                lock (gate)
                {
                    if (candidate is not null && ReferenceEquals(server, candidate)) server = null;
                }
            }
        }
    }

    public async Task AcceptOneAsync(CancellationToken cancellationToken)
    {
        await using var candidate = CreateServer();
        lock (gate) server = candidate;
        try
        {
            await candidate.WaitForConnectionAsync(cancellationToken);
            await HandleClientAsync(candidate, cancellationToken, keepClientConnected: false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(server, candidate)) server = null;
            }
        }
    }

    public async Task<NamedPipeClientStream> ConnectClientAsync(CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(10_000, cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) server?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream client,
        CancellationToken cancellationToken,
        bool keepClientConnected)
    {
        await using var ownedClient = client;
        using var writeGate = new SemaphoreSlim(1, 1);
        using var clientCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? telemetryTask = null;
        var connectionId = Guid.NewGuid();
        try
        {
            var identity = (await identityVerifier.VerifyAsync(client.SafePipeHandle, clientCancellation.Token)) with { ConnectionId = connectionId };
            var hello = await codec.ReadAsync(client, clientCancellation.Token);
            if (hello is not HelloEnvelope { ProtocolVersion.Major: 1 } requestHello)
                throw new FrameCodecException(Jiaolong.Contracts.Errors.ErrorCode.UnsupportedProtocol);

            await WriteAsync(client, new HelloAckEnvelope(
                new ProtocolVersion(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, requestHello.MessageId, "1.0.0"), writeGate, clientCancellation.Token);
            if (!keepClientConnected) return;

            while (!clientCancellation.IsCancellationRequested && client.IsConnected)
            {
                if (await codec.ReadAsync(client, clientCancellation.Token) is not RequestEnvelope request) continue;
                if (request.Operation == "subscribeTelemetry")
                {
                    await WriteAsync(client, Success(request, JsonSerializer.SerializeToElement(true)), writeGate, clientCancellation.Token);
                    telemetryTask ??= PublishTelemetryAsync(client, writeGate, GetTelemetryInterval(request.Payload), clientCancellation.Token);
                    continue;
                }

                var response = await dispatcher.DispatchAsync(request, identity, clientCancellation.Token);
                await WriteAsync(client, response, writeGate, clientCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (FrameCodecException exception)
        {
            logger?.LogWarning("Control service rejected a malformed or unsupported pipe frame: {error}", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger?.LogWarning("Control service rejected an unauthorized pipe client: {error}", exception.Message);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Control service pipe client ended unexpectedly.");
        }
        finally
        {
            clientCancellation.Cancel();
            if (telemetryTask is not null)
            {
                try { await telemetryTask; } catch { }
            }
            if (homeRuntime is not null)
            {
                try
                {
                    var release = await homeRuntime.ReleaseClientFanAsync(connectionId);
                    if (release is not null && release.State != Jiaolong.Contracts.Commands.CommandState.Applied)
                        logger?.LogError("Disconnected client's fan release requires recovery: {state}", release.State);
                }
                catch (Exception exception) { logger?.LogError(exception, "Disconnected client's fan release failed."); }
            }
        }
    }

    private async Task PublishTelemetryAsync(
        NamedPipeServerStream client,
        SemaphoreSlim writeGate,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        if (homeRuntime is null) return;
        ulong sequence = 0;
        while (!cancellationToken.IsCancellationRequested && client.IsConnected)
        {
            await Task.Delay(interval, cancellationToken);
            HardwareSnapshot snapshot;
            try
            {
                snapshot = await homeRuntime.ReadTelemetryAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            telemetryRingBuffer?.Add(snapshot);

            var message = new EventEnvelope(
                new ProtocolVersion(1, 0),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "telemetry.updated",
                ++sequence,
                JsonSerializer.SerializeToElement(snapshot, ProtocolJsonContext.Default.HardwareSnapshot));
            await WriteAsync(client, message, writeGate, cancellationToken);
        }
    }

    private static TimeSpan GetTelemetryInterval(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("intervalSeconds", out var value) ||
            !value.TryGetDouble(out var seconds) ||
            !double.IsFinite(seconds)) return DefaultTelemetryInterval;

        return TimeSpan.FromSeconds(Math.Clamp(seconds, DefaultTelemetryInterval.TotalSeconds, MaximumTelemetryInterval.TotalSeconds));
    }

    private async Task WriteAsync(
        NamedPipeServerStream client,
        MessageEnvelope message,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try { await codec.WriteAsync(client, message, cancellationToken); }
        finally { writeGate.Release(); }
    }

    private static ResponseEnvelope Success(RequestEnvelope request, JsonElement payload) => new(
        request.ProtocolVersion,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        request.MessageId,
        request.OperationId,
        ResponseStatus.Success,
        payload,
        null);

    private NamedPipeServerStream CreateServer() => NamedPipeServerStreamAcl.Create(
        pipeName,
        PipeDirection.InOut,
        8,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough,
        FrameCodec.MaxFrameBytes,
        FrameCodec.MaxFrameBytes,
        PipeAclFactory.CreateSecurityDescriptor());
}
