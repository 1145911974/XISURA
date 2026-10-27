using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Home;

public sealed record AdaptiveProcessCpuSnapshot(int ProcessId, long CreationTime, string Executable,
    string OwnerSid, int SessionId, TimeSpan ProcessorTime);

public sealed class AdaptiveProcessCpuReader
{
    private readonly Func<string, AdaptiveProcessCpuSnapshot[]?>? capture;
    private readonly TimeProvider clock;
    private readonly int processorCount;
    private Dictionary<(int Pid, long Creation), TimeSpan>? baseline;
    private (string Executable, string Owner, int Session, string Scope)? scope;
    private long sampledAt;
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(5);

    public AdaptiveProcessCpuReader(TimeProvider? timeProvider = null, int? processorCount = null,
        Func<string, AdaptiveProcessCpuSnapshot[]?>? capture = null)
    {
        this.capture = capture;
        clock = timeProvider ?? TimeProvider.System;
        this.processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    }

    public void Reset() { baseline = null; scope = null; sampledAt = 0; }

    public double? Read(string executable, string ownerSid, int sessionId, string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executable.IndexOfAny(['/', '\\', ':', '*', '?']) >= 0 || string.IsNullOrWhiteSpace(ownerSid) ||
            sessionId <= 0 || string.IsNullOrWhiteSpace(scopeKey))
        { Reset(); return null; }
        var nextScope = (executable.ToUpperInvariant(), ownerSid, sessionId, scopeKey);
        if (scope != nextScope) Reset();
        AdaptiveProcessCpuSnapshot[]? snapshots;
        try { snapshots = capture is null ? Capture(executable, ownerSid, sessionId) : capture(executable); }
        catch { Reset(); return null; }
        if (snapshots is null) { Reset(); return null; }
        var selected = snapshots.Where(item => item.SessionId == sessionId && item.OwnerSid == ownerSid &&
            string.Equals(item.Executable, executable, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0 || selected.Any(item => item.ProcessId <= 0 || item.CreationTime <= 0 || item.ProcessorTime < TimeSpan.Zero) ||
            selected.Select(item => (item.ProcessId, item.CreationTime)).Distinct().Count() != selected.Length)
        { Reset(); return null; }
        var now = clock.GetTimestamp();
        var current = selected.ToDictionary(item => (item.ProcessId, item.CreationTime), item => item.ProcessorTime);
        var previous = baseline;
        var elapsed = previous is null ? TimeSpan.Zero : clock.GetElapsedTime(sampledAt, now);
        baseline = current; scope = nextScope; sampledAt = now;
        if (previous is null || elapsed <= TimeSpan.Zero || elapsed > MaximumInterval || previous.Count != current.Count ||
            current.Any(item => !previous.TryGetValue(item.Key, out var before) || item.Value < before)) return null;
        var cpuSeconds = current.Sum(item => (item.Value - previous[item.Key]).TotalSeconds);
        var percent = 100 * cpuSeconds / elapsed.TotalSeconds / processorCount;
        return double.IsFinite(percent) && percent is >= 0 and <= 100 ? percent : null;
    }

    private static AdaptiveProcessCpuSnapshot[]? Capture(string executable, string ownerSid, int sessionId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var result = new List<AdaptiveProcessCpuSnapshot>();
        var processes = Process.GetProcessesByName(executable[..^4]);
        try
        {
            foreach (var process in processes)
            {
                using var handle = OpenProcess(0x1000, false, process.Id); // Query only; never require elevation of the target.
                if (handle.IsInvalid) return null;
                var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length) ||
                    !string.Equals(Path.GetFileName(path.ToString()), executable, StringComparison.OrdinalIgnoreCase)) return null;
                if (!OpenProcessToken(handle, 0x0008, out var token)) return null;
                using (token)
                {
                    using var identity = new WindowsIdentity(token.DangerousGetHandle());
                    var sid = identity.User?.Value;
                    if (sid is null || !GetTokenInformation(token, 12, out var session, sizeof(int), out _)) return null;
                    if (sid != ownerSid || session != sessionId) continue;
                    if (!GetProcessTimes(handle, out var creation, out var exited, out var kernel, out var user) || exited != 0) return null;
                    result.Add(new(process.Id, creation, executable, sid, session, TimeSpan.FromTicks(checked(kernel + user))));
                }
            }
            return result.ToArray();
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int value, int length, out int returnedLength);
}
