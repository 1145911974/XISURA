namespace Jiaolong.Hardware.Mechrevo.Wmi;

public interface IMiReadTransport
{
    Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken);
}

public interface IMiWriteTransport
{
    Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
}

public sealed record MiReadResult(byte[]? Payload, DataQuality Quality, string? Reason);

public sealed class MiCommonInterfaceClient(IMiReadTransport? transport = null, IMiWriteTransport? writeTransport = null,
    Func<bool>? hardwareAccessAllowed = null, Func<bool>? hardwareReadAllowed = null)
{
    private readonly IMiReadTransport? readTransport = transport;
    private readonly IMiWriteTransport? writeTransport = writeTransport;

    private bool IsHardwareAccessAllowed(bool readOnly = false) =>
        (readOnly ? hardwareReadAllowed?.Invoke() : null) ?? hardwareAccessAllowed?.Invoke() ?? true;

    private void EnsureHardwareAccess(bool readOnly = false)
    {
        if (!IsHardwareAccessAllowed(readOnly)) throw new IOException("hardwareAccessPaused");
    }

    public async Task<MiReadResult> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureHardwareAccess(readOnly: true);
        if (readTransport is null) return new MiReadResult(null, DataQuality.Unknown, "readTransportUnavailable");

        try
        {
            var payload = await readTransport.ReadAsync(binding, cancellationToken);
            return payload is { Length: > 0 }
                ? new MiReadResult(payload, DataQuality.Good, null)
                : new MiReadResult(null, DataQuality.Unknown, "emptyReadResponse");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch when (IsHardwareAccessAllowed(readOnly: true))
        {
            return new MiReadResult(null, DataQuality.Unknown, "readFailed");
        }
        finally { EnsureHardwareAccess(readOnly: true); }
    }

    public Task<MiReadResult> ReadAsync(VerifiedWmiBinding binding, CancellationToken cancellationToken) =>
        ReadAsync(new MiReadBinding(binding.Namespace, binding.Class, binding.InstanceName, binding.ReadType, binding.MethodName), cancellationToken);

    public async Task<MiReadResult> WriteOnceAsync(
        VerifiedWmiBinding binding,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        bool writeAttempted = false;
        try
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (payload.Length > 28) throw new ArgumentException("MI payload cannot exceed 28 bytes.", nameof(payload));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureHardwareAccess();
            if (writeTransport is null) return new MiReadResult(null, DataQuality.Unknown, "writeTransportUnavailable");

            var request = new byte[32];
            request[1] = checked((byte)binding.WriteType);
            request[3] = binding.MethodName;
            payload.CopyTo(request.AsMemory(4));
            try
            {
                EnsureHardwareAccess();
                writeAttempted = true;
                var response = await writeTransport.WriteAsync(binding, request, cancellationToken);
                return response is not null
                    ? new MiReadResult(response, DataQuality.Good, null)
                    : new MiReadResult(null, DataQuality.Unknown, "emptyWriteResponse");
            }
            finally { EnsureHardwareAccess(); }
        }
        catch (Exception error)
        {
            error.Data["miWriteAttempted"] = writeAttempted;
            throw;
        }
    }
}
