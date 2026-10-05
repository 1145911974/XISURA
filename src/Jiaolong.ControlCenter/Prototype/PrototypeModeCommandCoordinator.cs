using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Prototype;

public enum PrototypeModeApplyKind
{
    Applied,
    Rejected,
    Unavailable,
    Busy,
}

public sealed record PrototypeModeApplyOutcome(
    PrototypePerformanceMode RequestedMode,
    PerformanceMode ContractMode,
    PrototypeModeApplyKind Kind,
    CommandResult? Result)
{
    public bool Applied => Kind == PrototypeModeApplyKind.Applied;
}

public sealed class PrototypeAppliedModeState
{
    public PrototypePerformanceMode? AppliedMode { get; private set; }

    public bool ShouldApply(PrototypePerformanceMode mode) => AppliedMode != mode;

    public void Confirm(PrototypePerformanceMode mode) => AppliedMode = mode;
}

public sealed record PrototypeModeRequest(long Version, PrototypePerformanceMode Mode);

public sealed class PrototypeModeReconciliation
{
    private readonly object gate = new();
    private long nextVersion;
    private PrototypeModeRequest? pending;

    public PrototypePerformanceMode? PendingMode
    {
        get { lock (gate) return pending?.Mode; }
    }

    public PrototypeModeRequest Request(PrototypePerformanceMode mode)
    {
        lock (gate)
        {
            pending = new(++nextVersion, mode);
            return pending;
        }
    }

    public PrototypeModeRequest? Current
    {
        get { lock (gate) return pending; }
    }

    public bool IsCurrent(PrototypeModeRequest request)
    {
        lock (gate) return pending?.Version == request.Version;
    }

    public bool AcceptObservation(PrototypePerformanceMode mode)
    {
        lock (gate)
        {
            if (pending is { Mode: var requested } && requested != mode) return false;
            if (pending is { Mode: var acknowledged } && acknowledged == mode) pending = null;
            return true;
        }
    }

    public void Reject(PrototypeModeRequest request)
    {
        lock (gate)
        {
            if (pending?.Version == request.Version) pending = null;
        }
    }
}

public sealed class PrototypeModeCommandCoordinator(
    Func<SetPerformanceModeCommand, CancellationToken, Task<CommandResult>> sendAsync)
{
    private int isApplying;

    public static PerformanceMode Map(PrototypePerformanceMode mode) => mode switch
    {
        PrototypePerformanceMode.Office => PerformanceMode.Quiet,
        PrototypePerformanceMode.Gaming => PerformanceMode.Balanced,
        PrototypePerformanceMode.Turbo => PerformanceMode.Turbo,
        PrototypePerformanceMode.Custom => PerformanceMode.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static PrototypePerformanceMode Map(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Quiet => PrototypePerformanceMode.Office,
        PerformanceMode.Balanced => PrototypePerformanceMode.Gaming,
        PerformanceMode.Turbo => PrototypePerformanceMode.Turbo,
        PerformanceMode.Custom => PrototypePerformanceMode.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public async Task<PrototypeModeApplyOutcome> ApplyAsync(
        PrototypePerformanceMode mode,
        CancellationToken cancellationToken)
    {
        var contractMode = Map(mode);
        if (Interlocked.CompareExchange(ref isApplying, 1, 0) != 0)
            return new(mode, contractMode, PrototypeModeApplyKind.Busy, null);

        try
        {
            var command = new SetPerformanceModeCommand(Guid.NewGuid(), contractMode);
            var result = await sendAsync(command, cancellationToken);
            var kind = result.State == CommandState.Applied && result.Error is null
                ? PrototypeModeApplyKind.Applied
                : PrototypeModeApplyKind.Rejected;
            return new(mode, contractMode, kind, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return new(mode, contractMode, PrototypeModeApplyKind.Rejected, null);
        }
        catch
        {
            return new(mode, contractMode, PrototypeModeApplyKind.Unavailable, null);
        }
        finally
        {
            Interlocked.Exchange(ref isApplying, 0);
        }
    }
}
