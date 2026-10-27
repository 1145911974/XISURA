using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class FrameCodecTests
{
    [TestMethod]
    public async Task Fragmented_prefix_and_body_are_reassembled()
    {
        var expected = new HelloEnvelope(new ProtocolVersion(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, "test-client");
        var codec = new FrameCodec();
        await using var encoded = new MemoryStream();
        await codec.WriteAsync(encoded, expected, CancellationToken.None);
        encoded.Position = 0;

        var actual = await codec.ReadAsync(new OneByteReadStream(encoded), CancellationToken.None);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public async Task Frame_larger_than_one_mebibyte_is_rejected_before_allocation()
    {
        var declaredLength = BitConverter.GetBytes(1_048_577);
        await using var stream = new MemoryStream(declaredLength);
        var codec = new FrameCodec();

        var exception = await Assert.ThrowsExactlyAsync<FrameCodecException>(
            () => codec.ReadAsync(stream, CancellationToken.None).AsTask());

        Assert.AreEqual(ErrorCode.InvalidFrame, exception.Code);
        Assert.AreEqual(0, stream.Position - 4);
    }

    [TestMethod]
    public async Task Eof_between_frames_is_a_normal_disconnect()
    {
        var codec = new FrameCodec();
        await using var stream = new MemoryStream();
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => codec.ReadAsync(stream, CancellationToken.None).AsTask());

        var hello = new HelloEnvelope(new ProtocolVersion(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow, "test-client");
        await codec.WriteAsync(stream, hello, CancellationToken.None);
        stream.Position = 0;
        Assert.AreEqual(hello, await codec.ReadAsync(stream, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => codec.ReadAsync(stream, CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task Truncated_prefixes_and_bodies_remain_invalid_frames()
    {
        byte[][] frames = [[4], [4, 0, 0], [4, 0, 0, 0], [4, 0, 0, 0, 123]];
        foreach (var frame in frames)
        {
            await using var stream = new MemoryStream(frame);
            var error = await Assert.ThrowsExactlyAsync<FrameCodecException>(
                () => new FrameCodec().ReadAsync(new OneByteReadStream(stream), CancellationToken.None).AsTask());
            Assert.AreEqual(ErrorCode.InvalidFrame, error.Code);
        }
    }

    [TestMethod]
    public async Task Malformed_utf8_and_json_remain_invalid_frames()
    {
        byte[][] frames = [[1, 0, 0, 0, 255], [1, 0, 0, 0, 123]];
        foreach (var frame in frames)
        {
            await using var stream = new MemoryStream(frame);
            var error = await Assert.ThrowsExactlyAsync<FrameCodecException>(
                () => new FrameCodec().ReadAsync(stream, CancellationToken.None).AsTask());
            Assert.AreEqual(ErrorCode.InvalidFrame, error.Code);
        }
    }

    [TestMethod]
    public async Task Cancelled_reads_remain_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => new FrameCodec().ReadAsync(stream, cancellation.Token).AsTask());
    }

    private sealed class OneByteReadStream(Stream source) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Math.Min(1, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            source.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            source.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
