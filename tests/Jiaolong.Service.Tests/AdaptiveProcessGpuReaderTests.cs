using Jiaolong.Service.Home;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class AdaptiveProcessGpuReaderTests
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Seconds * 1000 + Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromFileTime(100_000_000 + Seconds * 10_000_000 + Milliseconds * 10_000);
    }
    private sealed class Fixture
    {
        public readonly Clock Clock = new();
        public AdaptiveProcessCpuSnapshot[]? Processes = [Process(1), Process(2)];
        public AdaptiveProcessGpuCounter[]? Counters;
        public bool Fail;
        public bool ChangeDuringCapture;
        public readonly AdaptiveProcessGpuReader Reader;
        public Fixture()
        {
            Reader = new(Clock, (_, _, _) => Processes, _ =>
            {
                if (Fail) throw new IOException();
                if (ChangeDuringCapture) Processes = [Process(1, birth: 2), Process(2)];
                return Counters;
            });
            Set(0, 0);
        }
        public void Set(ulong a, ulong b)
        {
            var stamp = (ulong)Clock.GetUtcNow().ToFileTime();
            Counters = [Counter(1, a, stamp), Counter(1, b, stamp, 1)];
        }
        public double? Read(string scope = "scope") => Reader.Read("app.exe", "owner", 1, scope);
    }
    private static AdaptiveProcessCpuSnapshot Process(int pid, long birth = 1, string sid = "owner", int session = 1) =>
        new(pid, birth, "app.exe", sid, session, TimeSpan.Zero);
    private static AdaptiveProcessGpuCounter Counter(int pid, ulong ticks, ulong time, int engine = 0) =>
        new($"pid_{pid}_luid_0x00000000_0x00000001_phys_0_eng_{engine}_engtype_3D", ticks, time, 10_000_000);
    private static double Known(double? value)
    {
        Assert.IsNotNull(value);
        return value.Value;
    }

    [TestMethod]
    public void Uses_raw_timer_delta_and_maximum_engine_not_sum_and_preserves_valid_zero()
    {
        var f = new Fixture(); Assert.IsNull(f.Read());
        f.Clock.Seconds++; f.Set(2_000_000, 4_000_000);
        Assert.AreEqual(40d, Known(f.Read()), .001);
        f.Clock.Seconds++; f.Set(2_000_000, 4_000_000);
        Assert.AreEqual(0d, Known(f.Read()));
        f.Clock.Seconds++; f.Clock.Milliseconds = -100; f.Set(2_900_000, 4_900_000);
        Assert.AreEqual(10d, Known(f.Read()), .001, "Slightly short raw intervals must not break one-second activity ticks.");
    }

    [TestMethod]
    public void Identity_birth_and_process_membership_changes_cannot_reuse_activity()
    {
        var f = new Fixture(); Assert.IsNull(f.Read());
        f.Clock.Seconds++; f.Processes = [Process(1, birth: 2), Process(2)]; f.Set(3_000_000, 3_000_000);
        Assert.IsNull(f.Read());
        f.Clock.Seconds++; f.Set(4_000_000, 4_000_000); Assert.AreEqual(10d, Known(f.Read()), .001);
        f.Clock.Seconds++; f.Processes = [Process(1, long.MaxValue)]; f.Set(5_000_000, 5_000_000); Assert.IsNull(f.Read());
        f.Processes = [Process(1, sid: "other")]; Assert.IsNull(f.Read());
        f.Processes = [Process(1, session: 2)]; Assert.IsNull(f.Read());
        f.Processes = null; Assert.IsNull(f.Read());
        var racing = new Fixture(); Assert.IsNull(racing.Read());
        racing.Clock.Seconds++; racing.Set(2_000_000, 2_000_000); racing.ChangeDuringCapture = true;
        Assert.IsNull(racing.Read(), "PID lifetime changed while counters were collected");
    }

    [TestMethod]
    public void Invalid_time_frequency_reset_scope_and_read_failure_produce_unknown()
    {
        foreach (var invalid in new[] { "stale", "frequency", "future", "long", "short", "failure", "scope", "reset" })
        {
            var f = new Fixture(); Assert.IsNull(f.Read());
            f.Clock.Seconds = invalid == "long" ? 6 : invalid == "short" ? 0 : 1; f.Set(2_000_000, 2_000_000);
            if (invalid == "stale") f.Counters = [Counter(1, 2_000_000, 1)];
            if (invalid == "frequency") f.Counters = [f.Counters![0] with { Frequency100ns = 1 }];
            if (invalid == "future") f.Counters = [f.Counters![0] with { Timestamp100ns = 999_000_000 }];
            if (invalid == "failure") f.Fail = true;
            if (invalid == "reset") f.Reader.Reset();
            Assert.IsNull(f.Read(invalid == "scope" ? "new-scope" : "scope"), invalid);
        }
        Background_capture_never_waits_or_duplicates_pending_work_and_discards_reset_results();
    }

    [TestMethod]
    public void Counter_membership_bad_names_rollback_and_overfull_engine_are_unknown()
    {
        foreach (var invalid in new[] { "missing", "added", "name", "rollback", "overfull", "duplicate" })
        {
            var f = new Fixture(); f.Set(1_000_000, 1_000_000); Assert.IsNull(f.Read());
            f.Clock.Seconds++; f.Set(2_000_000, 2_000_000);
            if (invalid == "missing") f.Counters = [f.Counters![0]];
            if (invalid == "added") f.Counters = [.. f.Counters!, Counter(2, 1_000_000, 110_000_000)];
            if (invalid == "name") f.Counters = [f.Counters![0] with { Name = "pid_1_invalid" }];
            if (invalid == "rollback") f.Counters = [f.Counters![0] with { Utilization100ns = 0 }, f.Counters[1]];
            if (invalid == "overfull") f.Counters = [f.Counters![0] with { Utilization100ns = 30_000_000 }, f.Counters[1]];
            if (invalid == "duplicate") f.Counters = [f.Counters![0], f.Counters[0]];
            Assert.IsNull(f.Read(), invalid);
        }
    }

    private static void Background_capture_never_waits_or_duplicates_pending_work_and_discards_reset_results()
    {
        var clock = new Clock();
        using var permits = new SemaphoreSlim(0);
        int started = 0;
        var reader = new AdaptiveProcessGpuReader(clock, (_, _, _) => [Process(1)], _ =>
        {
            Interlocked.Increment(ref started);
            if (!permits.Wait(TimeSpan.FromSeconds(5))) return null;
            return [Counter(1, (ulong)clock.Seconds * 1_000_000, (ulong)clock.GetUtcNow().ToFileTime())];
        }, backgroundCapture: true);
        void AwaitStarted(int expected)
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
                return Volatile.Read(ref started) >= expected;
            }, TimeSpan.FromSeconds(3)), "Background collection did not advance");
        }
        try
        {
            Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope"));
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref started) == 1, TimeSpan.FromSeconds(3)));
            for (int i = 0; i < 20; i++) { reader.Reset(); Assert.IsNull(reader.Read("app.exe", "owner", 1, "scope")); }
            Assert.AreEqual(1, Volatile.Read(ref started), "Reset must retain the pending flight");
            clock.Seconds = 1; permits.Release(); AwaitStarted(2); // Old generation cannot establish the baseline.
            clock.Seconds = 2; permits.Release(); AwaitStarted(3); // First accepted sample is still unknown.
            clock.Seconds = 3; permits.Release();
            double? value = null;
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                value = reader.Read("app.exe", "owner", 1, "scope");
                return value.HasValue;
            }, TimeSpan.FromSeconds(3)));
            Assert.AreEqual(10d, Known(value), .001);
        }
        finally { reader.Reset(); permits.Release(10); }
    }
}
