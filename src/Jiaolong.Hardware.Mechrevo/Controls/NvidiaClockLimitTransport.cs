using System.Diagnostics;
using System.Globalization;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public sealed record NvidiaClockDevice(string Uuid, int MinimumMhz, int MaximumMhz);
public sealed record NvidiaPowerRange(int MinimumWatts, int MaximumWatts, int DefaultWatts)
{
    public double? CurrentWatts { get; init; }
}

public sealed class NvidiaClockLimitTransport
{
    private readonly string path = File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"))
        ? Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");

    public async Task<NvidiaClockDevice> ProbeAsync(CancellationToken token)
    {
        var identity = await RunAsync(["--query-gpu=uuid", "--format=csv,noheader,nounits"], token);
        var ids = identity.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length != 1 || !ids[0].StartsWith("GPU-", StringComparison.Ordinal) || !Guid.TryParse(ids[0][4..], out _))
            throw new IOException("gpuClockDeviceAmbiguous");
        var output = await RunAsync(["-i", ids[0], "--query-supported-clocks=graphics", "--format=csv,noheader,nounits"], token);
        var clocks = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var clock) ? clock : 0)
            .Where(clock => clock is >= 100 and <= 6000).ToArray();
        if (clocks.Length == 0) throw new IOException("gpuClockRangeUnavailable");
        return new(ids[0], clocks.Min(), clocks.Max());
    }

    public static string[] BuildArguments(NvidiaClockDevice device, int? maximum)
    {
        if (!device.Uuid.StartsWith("GPU-", StringComparison.Ordinal) || !Guid.TryParse(device.Uuid[4..], out _) ||
            maximum is int value && (value < device.MinimumMhz || value > device.MaximumMhz))
            throw new ArgumentOutOfRangeException(nameof(maximum));
        // Zero lower bound preserves idle downclocking, matching the third-party console.
        return maximum is int mhz
            ? ["-i", device.Uuid, "-lgc", "0," + mhz.ToString(CultureInfo.InvariantCulture)]
            : ["-i", device.Uuid, "-rgc"];
    }

    public async Task ApplyAsync(NvidiaClockDevice device, int? maximum, CancellationToken token) =>
        _ = await RunAsync(BuildArguments(device, maximum), token);

    public async Task<NvidiaPowerRange> ReadPowerRangeAsync(NvidiaClockDevice device, CancellationToken token)
    {
        _ = BuildArguments(device, null);
        var output = await RunAsync(["-i", device.Uuid, "--query-gpu=power.min_limit,power.max_limit,power.default_limit,power.limit", "--format=csv,noheader,nounits"], token);
        return ParsePowerRange(output);
    }

    internal static NvidiaPowerRange ParsePowerRange(string output)
    {
        var fields = output.Trim().Split(',');
        if (fields.Length != 4 || !double.TryParse(fields[0], CultureInfo.InvariantCulture, out var minimum) ||
            !double.TryParse(fields[1], CultureInfo.InvariantCulture, out var maximum) ||
            !double.TryParse(fields[2], CultureInfo.InvariantCulture, out var standard) ||
            !double.IsFinite(minimum) || !double.IsFinite(maximum) || !double.IsFinite(standard) ||
            minimum < 1 || maximum > 1000 || minimum > standard || standard > maximum)
            throw new IOException("gpuPowerRangeUnavailable");
        double? current = null;
        if (fields[3].Trim() is not ("[N/A]" or "N/A"))
        {
            if (!double.TryParse(fields[3], CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value < minimum || value > maximum)
                throw new IOException("gpuPowerLimitReadbackUnavailable");
            current = value;
        }
        return new((int)Math.Ceiling(minimum), (int)Math.Floor(maximum), (int)Math.Round(standard))
        { CurrentWatts = current };
    }

    private async Task<string> RunAsync(string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("gpuClockProcessStartFailed");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0) throw new IOException($"nvidiaSmiExit{process.ExitCode}: {stderr} {stdout}");
            // Some driver versions print unsupported warnings despite a zero exit code.
            if (stdout.Contains("not supported", StringComparison.OrdinalIgnoreCase) || stderr.Contains("not supported", StringComparison.OrdinalIgnoreCase))
                throw new IOException("gpuClockNotSupported");
            return stdout;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(output, error); } catch { }
            throw;
        }
    }
}
