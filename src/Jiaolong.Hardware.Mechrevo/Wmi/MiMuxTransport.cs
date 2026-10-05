using System.IO;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Mechrevo.Wmi;

public sealed class MiMuxTransport : IMuxTransport
{
    private readonly MiCommonInterfaceClient client;
    private readonly VerifiedWmiBinding binding;

    public MiMuxTransport(MiCommonInterfaceClient client, VerifiedWmiBinding binding)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (binding.ReadType != 250 || binding.WriteType != 251 || binding.MethodName != 9)
            throw new ArgumentException("The binding is not the verified MUX protocol.", nameof(binding));
    }

    public async Task<MuxMode> ReadMuxAsync(CancellationToken cancellationToken)
    {
        var result = await client.ReadAsync(binding, cancellationToken).ConfigureAwait(false);
        if (result.Quality != DataQuality.Good || result.Payload is not { } payload || !MiMuxModeCodec.TryDecode(payload, out var mode))
            throw new InvalidDataException("muxModeReadFailed");
        return mode;
    }

    public async Task WriteMuxAsync(MuxMode mode, CancellationToken cancellationToken)
    {
        if (!MiMuxModeCodec.TryEncode(mode, out var value)) throw new ArgumentOutOfRangeException(nameof(mode));
        var result = await client.WriteOnceAsync(binding, new[] { value }, cancellationToken).ConfigureAwait(false);
        if (result.Quality != DataQuality.Good)
            throw new IOException("muxModeWriteFailed");
    }
}

public static class MiMuxModeCodec
{
    public static bool TryDecode(ReadOnlySpan<byte> payload, out MuxMode mode)
    {
        mode = default;
        if (payload.Length <= 4) return false;
        switch (payload[4])
        {
            case 0: mode = MuxMode.Hybrid; return true;
            case 1: mode = MuxMode.Discrete; return true;
            default: return false;
        }
    }

    public static bool TryEncode(MuxMode mode, out byte value)
    {
        value = mode switch
        {
            MuxMode.Hybrid => 0,
            MuxMode.Discrete => 1,
            _ => byte.MaxValue
        };
        return value != byte.MaxValue;
    }
}
