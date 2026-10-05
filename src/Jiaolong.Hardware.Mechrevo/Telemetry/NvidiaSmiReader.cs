using System.Diagnostics;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Mechrevo.Telemetry;

public sealed class NvidiaSmiReader
{
    private const int MaxOutputCharacters = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly string[] Arguments =
    [
        "--query-gpu=temperature.gpu,utilization.gpu,power.draw,pstate,clocks_event_reasons.gpu_idle,clocks_event_reasons.applications_clocks_setting,clocks_event_reasons.sw_power_cap,clocks_event_reasons.hw_slowdown,clocks_event_reasons.hw_thermal_slowdown,clocks_event_reasons.hw_power_brake_slowdown,clocks_event_reasons.sw_thermal_slowdown,clocks_event_reasons.sync_boost,clocks_event_reasons.board_limit,clocks_event_reasons.reliability,enforced.power.limit,power.max_limit",
        "--format=csv,noheader,nounits"
    ];
    private static readonly string[] BasicArguments =
    [
        "--query-gpu=temperature.gpu,utilization.gpu,power.draw,pstate,clocks_event_reasons.gpu_idle,clocks_event_reasons.applications_clocks_setting,clocks_event_reasons.sw_power_cap,clocks_event_reasons.hw_slowdown,clocks_event_reasons.hw_thermal_slowdown,clocks_event_reasons.hw_power_brake_slowdown,clocks_event_reasons.sw_thermal_slowdown,clocks_event_reasons.sync_boost,clocks_event_reasons.board_limit,clocks_event_reasons.reliability",
        "--format=csv,noheader,nounits"
    ];
    private readonly string? executablePath;
    private readonly Func<string, CancellationToken, Task<string?>>? outputProvider;
    private readonly Func<string, bool> executableVerifier;

    public NvidiaSmiReader(
        string? executablePath = null,
        Func<string, CancellationToken, Task<string?>>? outputProvider = null,
        Func<string, bool>? executableVerifier = null)
    {
        this.executablePath = executablePath;
        this.outputProvider = outputProvider;
        this.executableVerifier = executableVerifier ?? (_ => false);
    }

    public async Task<NvidiaSmiReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (outputProvider is null && !IsTrustedNvidiaSmiPath(executablePath))
            return NvidiaSmiReading.Unknown;
        foreach (var arguments in new[] { Arguments, BasicArguments })
        {
            try
            {
                var output = outputProvider is null
                    ? await ReadProcessOutputAsync(arguments, cancellationToken)
                    : await outputProvider(arguments[0], cancellationToken);
                if (output is not null && NvidiaSmiParser.TryParse(output, out var reading))
                    return reading;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch { }
        }
        return NvidiaSmiReading.Unknown;
    }

    private async Task<string?> ReadProcessOutputAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) return null;
            var output = await ReadCappedAsync(process.StandardOutput, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTrustedNvidiaSmiPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation",
            "NVSMI",
            "nvidia-smi.exe");
        var system = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        var fullPath = Path.GetFullPath(path);
        return (string.Equals(fullPath, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, Path.GetFullPath(system), StringComparison.OrdinalIgnoreCase)) && File.Exists(path);
    }

    private static async Task<string?> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[MaxOutputCharacters + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken);
            if (read == 0) break;
            count += read;
        }

        return count > MaxOutputCharacters ? null : new string(buffer, 0, count);
    }
}
