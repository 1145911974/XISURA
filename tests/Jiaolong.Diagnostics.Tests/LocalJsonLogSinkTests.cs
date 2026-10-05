using Jiaolong.Diagnostics;

namespace Jiaolong.Diagnostics.Tests;

[TestClass]
public sealed class LocalJsonLogSinkTests
{
    [TestMethod]
    public async Task Log_sink_reads_only_the_latest_bounded_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "JiaolongLogTests", Guid.NewGuid().ToString("N"));
        await using var sink = new LocalJsonLogSink(root);
        try
        {
            foreach (var marker in new[] { "first", "second", "third" })
            {
                await sink.WriteAsync(new DiagnosticEvent
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    EventName = DiagnosticEventNames.ServiceStarted,
                    Fields = new Dictionary<string, string> { ["marker"] = marker }
                }, CancellationToken.None);
            }

            var logs = await sink.ReadRecentAsync(2, CancellationToken.None);

            Assert.AreEqual(2, logs.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.IsFalse(logs.Contains("first", StringComparison.Ordinal));
            StringAssert.Contains(logs, "second");
            StringAssert.Contains(logs, "third");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Log_sink_writes_one_redacted_ndjson_event()
    {
        var root = Path.Combine(Path.GetTempPath(), "JiaolongLogTests", Guid.NewGuid().ToString("N"));
        var sink = new LocalJsonLogSink(root);
        await sink.WriteAsync(
            new DiagnosticEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                EventName = DiagnosticEventNames.ServiceStarted,
                Level = "info",
                Fields = new Dictionary<string, string> { ["user"] = "Administrator", ["token"] = "secret-token" }
            },
            CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(root, "events.ndjson"));
        StringAssert.Contains(text, "service.started");
        Assert.IsFalse(text.Contains("secret-token", StringComparison.Ordinal));
    }
}
