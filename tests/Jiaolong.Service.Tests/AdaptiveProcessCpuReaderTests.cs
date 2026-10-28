using Jiaolong.Service.Home;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class AdaptiveProcessCpuReaderTests
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
    private static AdaptiveProcessCpuSnapshot Sample(int pid, double seconds, long birth = 1,
        string sid = "owner", int session = 1) => new(pid, birth, "app.exe", sid, session, TimeSpan.FromSeconds(seconds));

    [TestMethod]
    public void Aggregates_only_same_owner_session_and_normalizes_logical_processors()
    {
        var clock = new Clock();
        var samples = new[] { Sample(1, 1), Sample(2, 2), Sample(3, 900, sid: "other"), Sample(4, 900, session: 2) };
        var reader = new AdaptiveProcessCpuReader(clock, 4, _ => samples);
        Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
        clock.Seconds = 2; samples = [Sample(1, 2), Sample(2, 3), Sample(3, 950, sid: "other"), Sample(4, 950, session: 2)];
        Assert.AreEqual(25d, reader.Read("APP.exe", "owner", 1, "scope")!.Value, .001);
        clock.Seconds = 3;
        Assert.AreEqual(0d, reader.Read("app.exe", "owner", 1, "scope")!.Value);
    }

    [TestMethod]
    public void Membership_restart_rollback_and_long_gap_each_require_new_baseline()
    {
        var clock = new Clock(); var samples = new[] { Sample(1, 1) };
        var reader = new AdaptiveProcessCpuReader(clock, 2, _ => samples);
        Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
        foreach (var next in new[] {
            new[] { Sample(1, 2), Sample(2, 1) }, new[] { Sample(1, 3) },
            new[] { Sample(1, 4, birth: 2) }, new[] { Sample(1, 1, birth: 2) } })
        {
            clock.Seconds++; samples = next;
            Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
        }
        clock.Seconds += 6; samples = [Sample(1, 2, birth: 2)];
        Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
        clock.Seconds++; samples = [Sample(1, 2.5, birth: 2)];
        Assert.AreEqual(25d, reader.Read("app.exe", "owner", 1, "scope")!.Value, .001);
    }

    [TestMethod]
    public void Missing_unreadable_and_failed_capture_clear_history_instead_of_partial_cpu()
    {
        var clock = new Clock(); AdaptiveProcessCpuSnapshot[]? samples = [Sample(1, 1)]; bool fail = false;
        var reader = new AdaptiveProcessCpuReader(clock, 1, _ => fail ? throw new IOException() : samples);
        foreach (var invalid in new AdaptiveProcessCpuSnapshot[]?[] { null, [], [Sample(1, 1, sid: "other")], [Sample(1, 1), Sample(1, 1)] })
        {
            samples = [Sample(1, 1)]; Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
            clock.Seconds++; samples = invalid; Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
        }
        samples = [Sample(1, 2)]; fail = true; clock.Seconds++;
        Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
        fail = false; clock.Seconds++; Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
    }

    [TestMethod]
    public void Scope_changes_reset_and_invalid_requests_never_capture()
    {
        var clock = new Clock(); int calls = 0;
        var reader = new AdaptiveProcessCpuReader(clock, 1, _ => { calls++; return [Sample(1, clock.Seconds)]; });
        Assert.IsNull(reader.Read("app.exe", "owner", 1, "a"));
        clock.Seconds++; Assert.IsNull(reader.Read("app.exe", "owner", 1, "b"));
        clock.Seconds++; Assert.AreEqual(100d, reader.Read("app.exe", "owner", 1, "b")!.Value);
        reader.Reset(); clock.Seconds++; Assert.IsNull(reader.Read("app.exe", "owner", 1, "b"));
        var before = calls;
        foreach (var name in new[] { "../app.exe", "app", "a*.exe" }) Assert.IsNull(reader.Read(name, "owner", 1, "b"));
        Assert.IsNull(reader.Read("app.exe", "owner", 0, "b"));
        Assert.AreEqual(before, calls);
    }
}
