using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace Jiaolong.Service.Home;

public sealed record AdaptiveProcessGpuCounter(string Name, ulong Utilization100ns, ulong Timestamp100ns, ulong Frequency100ns);

public sealed class AdaptiveProcessGpuReader
{
    private readonly TimeProvider clock;
    private readonly Func<string, string, int, AdaptiveProcessCpuSnapshot[]?> processes;
    private readonly Func<int[], AdaptiveProcessGpuCounter[]?> counters;
    private Dictionary<string, AdaptiveProcessGpuCounter>? baseline;
    private HashSet<(int Pid, long Birth)>? identities;
    private (string Exe, string Sid, int Session, string Key)? scope;
    private long sampledAt;
    private readonly bool background;
    private long generation;
    private Task<Batch?>? flight;
    private sealed record Batch(HashSet<(int Pid, long Birth)> Identities, AdaptiveProcessGpuCounter[] Counters,
        long SampledAt, long Generation, (string Exe, string Sid, int Session, string Key) Scope);
    private static readonly Regex Instance = new(
        @"\Apid_([1-9][0-9]*)_luid_0x[0-9a-fA-F]{1,8}_0x[0-9a-fA-F]{1,8}_phys_[0-9]+_eng_[0-9]+_engtype_[A-Za-z0-9_]+\z",
        RegexOptions.CultureInvariant);

    public AdaptiveProcessGpuReader(TimeProvider? timeProvider = null,
        Func<string, string, int, AdaptiveProcessCpuSnapshot[]?>? captureProcesses = null,
        Func<int[], AdaptiveProcessGpuCounter[]?>? captureCounters = null, bool? backgroundCapture = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        processes = captureProcesses ?? AdaptiveProcessCpuReader.Capture;
        counters = captureCounters ?? CaptureCounters;
        background = backgroundCapture ?? captureCounters is null;
    }

    public double? Read(string executable, string ownerSid, int sessionId, string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executable.IndexOfAny(['/', '\\', ':', '*', '?']) >= 0 || string.IsNullOrWhiteSpace(ownerSid) ||
            sessionId <= 0 || string.IsNullOrWhiteSpace(scopeKey)) { Reset(); return null; }
        var nextScope = (executable.ToUpperInvariant(), ownerSid, sessionId, scopeKey);
        if (scope != nextScope) { Reset(); scope = nextScope; }
        try
        {
            Batch? batch;
            if (background)
            {
                if (flight is null) { StartFlight(nextScope); return null; }
                if (!flight.IsCompleted) return null;
                batch = flight.GetAwaiter().GetResult();
                flight = null;
                StartFlight(nextScope);
                if (batch is null) { Reset(); return null; }
                if (batch.Generation != generation || batch.Scope != nextScope) return null;
                var live = ReadIdentities(processes(executable, ownerSid, sessionId), executable, ownerSid, sessionId);
                if (live is null || !batch.Identities.SetEquals(live)) { Reset(); return null; }
            }
            else batch = CaptureBatch(nextScope, generation);
            if (batch is null) { Reset(); return null; }
            var before = batch.Identities;
            var raw = batch.Counters;
            var utc = clock.GetUtcNow().ToFileTime();
            var current = new Dictionary<string, AdaptiveProcessGpuCounter>(StringComparer.Ordinal);
            foreach (var counter in raw)
            {
                var match = Instance.Match(counter.Name);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                    !before.Any(item => item.Pid == pid) || counter.Frequency100ns != 10_000_000 ||
                    counter.Timestamp100ns > long.MaxValue || counter.Timestamp100ns > (ulong)utc ||
                    (ulong)utc - counter.Timestamp100ns > 30_000_000 ||
                    before.Single(item => item.Pid == pid).Birth >= (long)counter.Timestamp100ns ||
                    !current.TryAdd(counter.Name, counter)) { Reset(); return null; }
            }
            if (current.Count == 0) { Reset(); return null; }
            var now = batch.SampledAt;
            var previous = baseline;
            var sameIdentity = identities?.SetEquals(before) == true;
            var elapsed = previous is null ? TimeSpan.Zero : clock.GetElapsedTime(sampledAt, now);
            baseline = current; identities = before; scope = nextScope; sampledAt = now;
            // A one-second worker tick can be slightly short; do not alternate known/unknown on timer jitter.
            if (previous is null || !sameIdentity || elapsed < TimeSpan.FromMilliseconds(500) || elapsed > TimeSpan.FromSeconds(5) ||
                previous.Count != current.Count || current.Keys.Any(name => !previous.ContainsKey(name))) return null;
            double maximum = 0;
            foreach (var (name, value) in current)
            {
                var old = previous[name];
                if (value.Timestamp100ns <= old.Timestamp100ns || value.Utilization100ns < old.Utilization100ns) return null;
                var interval = value.Timestamp100ns - old.Timestamp100ns;
                if (interval < 5_000_000 || interval > 50_000_000) return null;
                var percent = 100d * (value.Utilization100ns - old.Utilization100ns) / interval;
                if (!double.IsFinite(percent) || percent is < 0 or > 100) return null;
                maximum = Math.Max(maximum, percent);
            }
            return maximum; // Most active process engine on any GPU, never an engine sum.
        }
        catch { Reset(); return null; }
    }

    public void Reset() { baseline = null; identities = null; scope = null; sampledAt = 0; generation++; }

    private void StartFlight((string Exe, string Sid, int Session, string Key) request)
    {
        var requestedGeneration = generation;
        flight = Task.Run(() => CaptureBatch(request, requestedGeneration));
    }

    private Batch? CaptureBatch((string Exe, string Sid, int Session, string Key) request, long requestedGeneration)
    {
        try
        {
            var before = ReadIdentities(processes(request.Exe, request.Sid, request.Session), request.Exe, request.Sid, request.Session);
            if (before is null) return null;
            var raw = counters(before.Select(item => item.Pid).ToArray());
            var after = ReadIdentities(processes(request.Exe, request.Sid, request.Session), request.Exe, request.Sid, request.Session);
            return raw is not null && after is not null && before.SetEquals(after)
                ? new(before, raw, clock.GetTimestamp(), requestedGeneration, request) : null;
        }
        catch { return null; }
    }

    private static HashSet<(int Pid, long Birth)>? ReadIdentities(AdaptiveProcessCpuSnapshot[]? values,
        string executable, string sid, int session)
    {
        if (values is null) return null;
        var selected = values.Where(item => item.OwnerSid == sid && item.SessionId == session &&
            string.Equals(item.Executable, executable, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0 || selected.Any(item => item.ProcessId <= 0 || item.CreationTime <= 0) ||
            selected.Select(item => item.ProcessId).Distinct().Count() != selected.Length) return null;
        return selected.Select(item => (item.ProcessId, item.CreationTime)).ToHashSet();
    }

    private static AdaptiveProcessGpuCounter[]? CaptureCounters(int[] processIds)
    {
        if (!OperatingSystem.IsWindows() || processIds.Length == 0) return null;
        // WQL '_' is a wildcard; bracket it so PID 12 cannot match PID 123.
        var filter = string.Join(" OR ", processIds.Select(pid => $"Name LIKE 'pid[_]{pid}[_]%'"));
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery("SELECT Name,UtilizationPercentage,Timestamp_Sys100NS,Frequency_Sys100NS FROM Win32_PerfRawData_GPUPerformanceCounters_GPUEngine WHERE " + filter), options);
        using var results = searcher.Get();
        var values = new List<AdaptiveProcessGpuCounter>();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (item["Name"] is null || item["UtilizationPercentage"] is null ||
                    item["Timestamp_Sys100NS"] is null || item["Frequency_Sys100NS"] is null) return null;
                values.Add(new(Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? "",
                    Convert.ToUInt64(item["UtilizationPercentage"], CultureInfo.InvariantCulture),
                    Convert.ToUInt64(item["Timestamp_Sys100NS"], CultureInfo.InvariantCulture),
                    Convert.ToUInt64(item["Frequency_Sys100NS"], CultureInfo.InvariantCulture)));
            }
        }
        return values.ToArray();
    }
}
