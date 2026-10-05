using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Protocol;

namespace Jiaolong.Service.Ipc;

public interface IFrameCodec
{
    ValueTask<MessageEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken);

    ValueTask WriteAsync(Stream stream, MessageEnvelope message, CancellationToken cancellationToken);
}

public sealed class FrameCodec : IFrameCodec
{
    public const int MaxFrameBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask<MessageEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new FrameCodecException(ErrorCode.InvalidFrame);
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(stream, rented.AsMemory(0, length), cancellationToken);
            var json = StrictUtf8.GetString(rented, 0, length);
            var message = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MessageEnvelope);
            return message ?? throw new FrameCodecException(ErrorCode.InvalidFrame);
        }
        catch (DecoderFallbackException exception)
        {
            throw new FrameCodecException(ErrorCode.InvalidFrame, exception);
        }
        catch (JsonException exception)
        {
            throw new FrameCodecException(ErrorCode.InvalidFrame, exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public async ValueTask WriteAsync(Stream stream, MessageEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.MessageEnvelope);
        if (json.Length == 0 || json.Length > MaxFrameBytes)
        {
            throw new FrameCodecException(ErrorCode.InvalidFrame);
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, json.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new FrameCodecException(ErrorCode.InvalidFrame);
            }

            offset += read;
        }
    }

    private static Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        ReadExactlyAsync(stream, buffer.AsMemory(), cancellationToken);
}

public sealed class FrameCodecException : Exception
{
    public FrameCodecException(ErrorCode code, Exception? innerException = null)
        : base(code.ToWireValue(), innerException)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}
