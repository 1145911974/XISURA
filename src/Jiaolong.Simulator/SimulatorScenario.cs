using System.Text.Json;
using System.Text.Json.Serialization;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Simulator;

public sealed class SimulatorScenario
{
    [JsonPropertyName("fingerprint")]
    public SimulatorFingerprint Fingerprint { get; init; } = new();

    [JsonPropertyName("capabilities")]
    public SimulatorCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("telemetrySeedSeconds")]
    public int TelemetrySeedSeconds { get; init; } = 60;

    [JsonPropertyName("telemetryFrames")]
    public SimulatorTelemetryFrame[] TelemetryFrames { get; init; } = Array.Empty<SimulatorTelemetryFrame>();

    [JsonPropertyName("controls")]
    public Dictionary<string, SimulatorControl> Controls { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("outcomes")]
    public Dictionary<string, SimulatorOperationOutcome> Outcomes { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("conflictDetected")]
    public bool ConflictDetected { get; init; }

    public HardwareFingerprint ToHardwareFingerprint() => new(
        Fingerprint.BoardProduct,
        Fingerprint.BiosVersion,
        Fingerprint.CpuModel,
        Fingerprint.GpuName,
        Fingerprint.HasVerifiedWritableEvidence)
    {
        CpuVendor = Fingerprint.CpuVendor,
        GpuVendorId = Fingerprint.GpuVendorId,
        GpuPnpDeviceIdsExact = Fingerprint.GpuPnpDeviceIdsExact
    };

    public static async Task<SimulatorScenario> LoadAsync(string scenarioPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        await using var stream = File.OpenRead(scenarioPath);
        var scenario = await JsonSerializer.DeserializeAsync<SimulatorScenario>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            },
            cancellationToken);

        return scenario ?? throw new InvalidDataException("Simulator scenario is empty.");
    }

    public void Validate()
    {
        if (TelemetrySeedSeconds < 60 || TelemetryFrames.Length == 0)
        {
            throw new InvalidDataException("Simulator scenarios require at least 60 seconds of telemetry seed and one frame.");
        }

        if (!Capabilities.CanMonitor)
        {
            throw new InvalidDataException("Simulator scenarios must expose monitoring.");
        }

        foreach (var key in Controls.Keys.Concat(Outcomes.Keys))
        {
            if (!new ControlKey(key).IsKnown && !string.Equals(key, "default", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unknown simulator control key '{key}'.");
            }
        }

        foreach (var outcome in Outcomes.Values)
        {
            if (!SimulatorOperationOutcome.AllowedOutcomes.Contains(outcome.Outcome, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Unknown simulator outcome '{outcome.Outcome}'.");
            }

            if (outcome.DelayMs < 0 || outcome.DelayMs > 60000)
            {
                throw new InvalidDataException("Simulator delay must be between 0 and 60000 milliseconds.");
            }
        }
    }
}

public sealed class SimulatorFingerprint
{
    public string BoardProduct { get; init; } = string.Empty;
    public string BiosVersion { get; init; } = string.Empty;
    public string CpuModel { get; init; } = string.Empty;
    public string GpuName { get; init; } = string.Empty;
    public bool HasVerifiedWritableEvidence { get; init; }
    public string CpuVendor { get; init; } = string.Empty;
    public string GpuVendorId { get; init; } = string.Empty;
    public string[] GpuPnpDeviceIdsExact { get; init; } = Array.Empty<string>();
}

public sealed class SimulatorCapabilities
{
    public bool CanMonitor { get; init; }
    public bool AnyHardwareWriteEnabled { get; init; }
    public bool ConflictDetected { get; init; }
}

public sealed class SimulatorTelemetryFrame
{
    public DateTimeOffset AtUtc { get; init; }
    public string HardwareState { get; init; } = "normal";
    public double? CpuTemperatureC { get; init; }
    public double? GpuTemperatureC { get; init; }
    public int? CpuPowerWatts { get; init; }
    public int? GpuPowerWatts { get; init; }

    public HardwareSnapshot ToSnapshot(DateTimeOffset capturedAtUtc) => new(
        capturedAtUtc,
        HardwareState,
        CpuTemperatureC,
        GpuTemperatureC,
        CpuPowerWatts,
        GpuPowerWatts);
}

public sealed class SimulatorControl
{
    public double? NumericValue { get; init; }
    public bool? BooleanValue { get; init; }
    public string? TextValue { get; init; }
    public bool IsAvailable { get; init; } = true;
    public string? UnavailableReason { get; init; }
}

public sealed class SimulatorOperationOutcome
{
    public static IReadOnlyList<string> AllowedOutcomes { get; } =
        ["success", "writeFailure", "readbackMismatch", "rollbackFailure"];

    public string Outcome { get; init; } = "success";
    public int DelayMs { get; init; }
    public object? ReadBackValue { get; init; }
}
