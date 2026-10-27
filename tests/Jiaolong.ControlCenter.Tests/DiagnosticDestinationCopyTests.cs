using System.Text;
using Jiaolong_ControlCenter.Prototype.Controls;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class DiagnosticDestinationCopyTests
{
    [TestMethod]
    public async Task Completed_copy_replaces_existing_or_creates_new_destination_without_staging_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "xisura-diagnostic-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool existing in new[] { false, true })
            {
                string destination = Path.Combine(root, existing ? "existing.zip" : "new.zip");
                if (existing) await File.WriteAllTextAsync(destination, "old");
                using var source = new MemoryStream(Encoding.UTF8.GetBytes("complete"));
                await DiagnosticDestinationCopy.CopyAsync(source, destination, CancellationToken.None);
                Assert.AreEqual("complete", await File.ReadAllTextAsync(destination));
            }
            CollectionAssert.AreEquivalent(new[] { "existing.zip", "new.zip" },
                Directory.GetFiles(root).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Failed_copy_preserves_existing_destination_and_removes_staging_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "xisura-diagnostic-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "existing.zip");
            await File.WriteAllTextAsync(destination, "old");
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await DiagnosticDestinationCopy.CopyAsync(new FailingStream(), destination, CancellationToken.None));
            Assert.AreEqual("old", await File.ReadAllTextAsync(destination));
            CollectionAssert.AreEquivalent(new[] { destination }, Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FailingStream : Stream
    {
        private bool started;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (started) throw new IOException("Simulated interrupted copy");
            started = true;
            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
