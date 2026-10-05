using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Simulator;

public sealed class SimulatorHardwareAdapter : IHardwareAdapter, IHardwareControlRestorer
{
    private readonly SimulatorScenario scenario;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, SimulatorControl> controls;
    private readonly List<HardwareSnapshot> history;
    private readonly List<SimulatorWriteRecord> writeRecords = [];
    private int telemetryCursor;

    private SimulatorHardwareAdapter(SimulatorScenario scenario, TimeProvider timeProvider)
    {
        this.scenario = scenario;
        this.timeProvider = timeProvider;
        controls = new Dictionary<string, SimulatorControl>(scenario.Controls, StringComparer.Ordinal);
        history = SeedHistory(scenario, timeProvider);
    }

    public SimulatorCapabilities Capabilities => scenario.Capabilities;

    public SimulatorScenario Scenario => scenario;

    public IReadOnlyList<HardwareSnapshot> History => history;

    public IReadOnlyList<SimulatorWriteRecord> WriteRecords => writeRecords;

    public static async Task<SimulatorHardwareAdapter> LoadAsync(
        string scenarioPath,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        var scenario = await SimulatorScenario.LoadAsync(scenarioPath, cancellationToken);
        scenario.Validate();
        return new SimulatorHardwareAdapter(scenario, timeProvider ?? TimeProvider.System);
    }

    public Task<HardwareSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = scenario.TelemetryFrames[telemetryCursor++ % scenario.TelemetryFrames.Length];
        return Task.FromResult(frame.ToSnapshot(timeProvider.GetUtcNow()));
    }

    public Task<ControlSnapshot> ReadControlAsync(ControlKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!key.IsKnown || !controls.TryGetValue(key.Value, out var control))
        {
            return Task.FromResult(new ControlSnapshot(
                key,
                timeProvider.GetUtcNow(),
                null,
                null,
                null,
                false,
                "unknownOrUnavailableControl"));
        }

        return Task.FromResult(new ControlSnapshot(
            key,
            timeProvider.GetUtcNow(),
            control.NumericValue,
            control.BooleanValue,
            control.TextValue,
            control.IsAvailable,
            control.UnavailableReason));
    }

    public async Task WriteAsync(ValidatedHardwareWrite write, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(write, cancellationToken);
        if (!string.Equals(result.Outcome, "success", StringComparison.Ordinal))
        {
            throw new SimulatorOperationException(result);
        }
    }

    public async Task<SimulatorWriteResult> ExecuteAsync(
        ValidatedHardwareWrite write,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!write.IsWellFormed)
        {
            throw new ArgumentException("Simulator writes must be validated typed values.", nameof(write));
        }

        var requestedValue = GetValue(write);
        if (!Capabilities.AnyHardwareWriteEnabled)
        {
            var disabledResult = new SimulatorWriteResult(
                write.OperationId,
                write.Key,
                requestedValue,
                null,
                "writeFailure",
                true,
                0);
            writeRecords.Add(new SimulatorWriteRecord(
                write.OperationId,
                write.Key,
                disabledResult.Outcome,
                timeProvider.GetUtcNow()));
            return disabledResult;
        }

        var outcome = scenario.Outcomes.TryGetValue(write.Key.Value, out var configured)
            ? configured
            : scenario.Outcomes.GetValueOrDefault("default", new SimulatorOperationOutcome());
        var readBackValue = outcome.Outcome switch
        {
            "readbackMismatch" => outcome.ReadBackValue ?? CreateMismatch(requestedValue),
            "writeFailure" => null,
            _ => outcome.ReadBackValue ?? requestedValue
        };
        var rollbackSucceeded = !string.Equals(outcome.Outcome, "rollbackFailure", StringComparison.Ordinal);

        if (outcome.Outcome is "success" or "readbackMismatch" or "rollbackFailure")
        {
            controls[write.Key.Value] = new SimulatorControl
            {
                NumericValue = write.NumericValue,
                BooleanValue = write.BooleanValue,
                TextValue = write.TextValue,
                IsAvailable = true
            };
        }

        var result = new SimulatorWriteResult(
            write.OperationId,
            write.Key,
            requestedValue,
            readBackValue,
            outcome.Outcome,
            rollbackSucceeded,
            outcome.DelayMs);
        writeRecords.Add(new SimulatorWriteRecord(
            write.OperationId,
            write.Key,
            outcome.Outcome,
            timeProvider.GetUtcNow()));
        return result;
    }

    public Task ReleaseFanControlAsync(ReleaseReason reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RestoreControlAsync(ControlSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.IsAvailable || !snapshot.Key.IsKnown)
        {
            throw new InvalidOperationException("Simulator cannot restore an unavailable control.");
        }

        controls[snapshot.Key.Value] = new SimulatorControl
        {
            NumericValue = snapshot.NumericValue,
            BooleanValue = snapshot.BooleanValue,
            TextValue = snapshot.TextValue,
            IsAvailable = true
        };
        return Task.CompletedTask;
    }

    private static List<HardwareSnapshot> SeedHistory(SimulatorScenario scenario, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var history = new List<HardwareSnapshot>(scenario.TelemetrySeedSeconds);
        for (var index = 0; index < scenario.TelemetrySeedSeconds; index++)
        {
            var frame = scenario.TelemetryFrames[index % scenario.TelemetryFrames.Length];
            history.Add(frame.ToSnapshot(now.AddSeconds(index - scenario.TelemetrySeedSeconds + 1)));
        }

        return history;
    }

    private static object? GetValue(ValidatedHardwareWrite write) =>
        write.NumericValue ?? (object?)write.BooleanValue ?? write.TextValue;

    private static object? CreateMismatch(object? requestedValue) => requestedValue switch
    {
        double number => number + 1,
        int number => number + 1,
        bool boolean => !boolean,
        string text => text + "-mismatch",
        _ => null
    };
}

public sealed record SimulatorWriteResult(
    Guid OperationId,
    ControlKey Key,
    object? RequestedValue,
    object? ReadBackValue,
    string Outcome,
    bool RollbackSucceeded,
    int DelayMs);

public sealed record SimulatorWriteRecord(
    Guid OperationId,
    ControlKey Key,
    string Outcome,
    DateTimeOffset RecordedAtUtc);

public sealed class SimulatorOperationException(SimulatorWriteResult result)
    : HardwareOperationException(
        result.Key,
        result.Outcome,
        result.RequestedValue,
        result.ReadBackValue,
        result.RollbackSucceeded)
{
    public SimulatorWriteResult Result { get; } = result;
}
