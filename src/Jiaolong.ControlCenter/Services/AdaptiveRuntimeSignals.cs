using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.System.Power;

namespace Jiaolong_ControlCenter.Services;

public sealed record AdaptiveRuntimeSignals(string Executable, bool Foreground, int? IdleSeconds, bool? BatterySaver)
{
    public static AdaptiveRuntimeSignals Read()
    {
        var executable = ReadForegroundExecutable();
        return new(executable ?? "", executable is not null, ReadIdleSeconds(), ReadBatterySaver());
    }

    public static string[]? ReadRunningExecutables(IEnumerable<string> candidates)
    {
        var requested = candidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return [];
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var executable = process.ProcessName + ".exe";
                    if (requested.Contains(executable)) running.Add(executable);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return running.ToArray();
    }

    private static string? ReadForegroundExecutable()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
            return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var executable = process.MainModule?.FileName;
            var name = string.IsNullOrEmpty(executable) ? process.ProcessName : Path.GetFileName(executable);
            return string.IsNullOrEmpty(name) ? null : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static int? ReadIdleSeconds()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input)) return null;

        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - input.Time);
        // LASTINPUTINFO is session-local and can report non-monotonic ticks; reject implausible spans.
        return elapsedMilliseconds > int.MaxValue ? null : (int)(elapsedMilliseconds / 1_000);
    }

    private static bool? ReadBatterySaver()
    {
        try
        {
            return PowerManager.EnergySaverStatus switch
            {
                EnergySaverStatus.On => true,
                EnergySaverStatus.Off => false,
                _ => null
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
}
