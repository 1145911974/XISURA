using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Home;

// The admission lock is short; the hardware gate holds through a complete mode/page transaction.
public sealed class ControlExecutionCoordinator(
    Func<HardwareCommand, CancellationToken, Guid, Task<CommandResult>> execute,
    Func<HardwareCommand, CancellationToken, Task> prepare,
    Func<Task> observeUnknown,
    Func<HardwareCommand, bool>? isAutomatic = null,
    Func<Jiaolong.Contracts.Models.ControlExecutionState?>? getExecutionState = null)
{
    private const int MaximumOperations = 256;
    private readonly object admission = new();
    private readonly SemaphoreSlim hardwareGate = new(1, 1);
    private readonly Dictionary<Guid, Operation> operations = new();
    private long generation;
    private int activeWrites;
    private readonly AsyncLocal<ExecutionAdmission?> currentAdmission = new();
    public ExecutionAdmission? CurrentAdmission => currentAdmission.Value;
    public bool IsIdle => Volatile.Read(ref activeWrites) == 0;
    public ControlPresetCatalog Catalog { get; } = new();
    public long Generation => Interlocked.Read(ref generation);
    public long HardwareEpoch { get; private set; } = DateTimeOffset.UtcNow.UtcTicks;

    internal Task<CommandResult>? FindOperation(HardwareCommand command, string owner)
    {
        lock (admission)
            return operations.TryGetValue(command.OperationId, out var prior)
                ? prior.Owner == owner && prior.Fingerprint == JsonSerializer.Serialize(command, ProtocolJsonContext.Default.HardwareCommand)
                    ? prior.Completion.Task : Task.FromResult(Rejected(command, ErrorCode.IdempotencyConflict))
                : null;
    }

    public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token,
        Guid connectionId = default, string owner = "service", ClientIdentity? clientIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is ApplyControlRequestCommand apply && (apply.Request is null || apply.Request.OperationId != command.OperationId ||
            apply.Request.Origin == Jiaolong.Contracts.Models.ControlOrigin.TurboBranch ||
            apply.Request.Origin is Jiaolong.Contracts.Models.ControlOrigin.Adaptive or Jiaolong.Contracts.Models.ControlOrigin.StartupRestore or Jiaolong.Contracts.Models.ControlOrigin.TurboBranch &&
            isAutomatic?.Invoke(command) != true))
            return Task.FromResult(Rejected(command, ErrorCode.UnauthorizedClient));
        if (command.OperationId == Guid.Empty || CommandValidation.Validate(command) is not null)
            return Task.FromResult(Rejected(command, ErrorCode.ValidationFailed));
        string fingerprint = JsonSerializer.Serialize(command, ProtocolJsonContext.Default.HardwareCommand);
        Operation operation;
        lock (admission)
        {
            if (operations.TryGetValue(command.OperationId, out var prior))
                return prior.Owner == owner && prior.Fingerprint == fingerprint ? prior.Completion.Task
                    : Task.FromResult(Rejected(command, ErrorCode.IdempotencyConflict));
            bool automatic = isAutomatic?.Invoke(command) == true;
            if (automatic && operations.Values.Any(item => item.Managed && !item.Automatic && !item.Completion.Task.IsCompleted))
                return Task.FromResult(Rejected(command, ErrorCode.CancelledBeforeApply));
            foreach (var completed in operations.Where(pair => pair.Value.Completion.Task.IsCompletedSuccessfully &&
                         !IsUnresolved(pair.Value.Completion.Task.Result))
                         .Take(Math.Max(0, operations.Count - MaximumOperations + 1)).Select(pair => pair.Key).ToArray())
                operations.Remove(completed);
            if (operations.Count >= MaximumOperations)
                return Task.FromResult(Rejected(command, ErrorCode.CommandInProgress));
            bool managed = IsManaged(command);
            operation = new(owner, fingerprint, managed ? Interlocked.Increment(ref generation) : Generation, managed, automatic)
                { ClientIdentity = clientIdentity };
            operations.Add(command.OperationId, operation);
        }
        _ = ExecuteCoreAsync(command, operation, token, connectionId);
        return operation.Completion.Task;
    }

    public bool IsCurrent(long expected) => Generation == expected;
    public void InvalidateIntent() { lock (admission) Interlocked.Increment(ref generation); }

    // Caller holds the hardware gate; recovery keeps the original owner's durable GPU journal scope.
    internal async Task<T> WithRecoveryAdmissionAsync<T>(Guid operationId, string owner, Func<Task<T>> action)
    {
        var previous = currentAdmission.Value;
        currentAdmission.Value = new(Generation, owner, false)
            { OperationId = operationId, CommandKind = "EndClientControlSession" };
        try { return await action(); }
        finally { currentAdmission.Value = previous; }
    }

    public async Task<T> WithHardwareGateAsync<T>(Func<Task<T>> action, CancellationToken token = default,
        HardwareCommand? lifecycleCommand = null)
    {
        await hardwareGate.WaitAsync(token);
        Interlocked.Increment(ref activeWrites);
        var previousAdmission = currentAdmission.Value;
        if (lifecycleCommand is not null) currentAdmission.Value = new(Generation, "service", false)
            { CommandKind = lifecycleCommand.GetType().Name, OperationId = lifecycleCommand.OperationId };
        try { return await action(); }
        finally { currentAdmission.Value = previousAdmission; Interlocked.Decrement(ref activeWrites); hardwareGate.Release(); }
    }

    public void InvalidateHardware()
    {
        lock (admission)
        {
            Interlocked.Increment(ref generation);
            HardwareEpoch++;
            // Retain operation results: a retry across resume must not repeat an uncertain write.
        }
    }

    private async Task ExecuteCoreAsync(HardwareCommand command, Operation operation, CancellationToken token, Guid connectionId)
    {
        bool acquired = false;
        bool started = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (command is PublishPresetCatalogCommand publication)
            {
                operation.Completion.TrySetResult(Catalog.Publish(publication, operation.Owner));
                return;
            }
            await prepare(command, token);
            await hardwareGate.WaitAsync(token);
            acquired = true;
            if (operation.Managed && !IsCurrent(operation.Generation))
            {
                operation.Completion.TrySetResult(Rejected(command, ErrorCode.CancelledBeforeApply));
                return;
            }
            started = true;
            Interlocked.Increment(ref activeWrites);
            currentAdmission.Value = new(operation.Generation, operation.Owner, operation.Automatic)
                { CommandKind = command.GetType().Name, OperationId = command.OperationId, ClientIdentity = operation.ClientIdentity };
            var result = await execute(command, token, connectionId);
            operation.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            if (started)
            {
                try { await observeUnknown(); } catch { }
                operation.Completion.TrySetResult(new(command.OperationId, CommandState.RecoveryRequired, null,
                    RequiredUserAction.None, ServiceError.Create(ErrorCode.DeadlineExceeded, command.OperationId, false), false)
                { Mutation = HardwareMutation.Unknown, ExecutionState = getExecutionState?.Invoke() });
            }
            else operation.Completion.TrySetResult(Rejected(command, ErrorCode.CancelledBeforeApply));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            operation.Completion.TrySetResult(Rejected(command, ErrorCode.ServiceUnavailable) with
            { Mutation = started ? HardwareMutation.Unknown : HardwareMutation.NoMutation });
        }
        catch (Exception)
        {
            operation.Completion.TrySetResult(Rejected(command, ErrorCode.InternalFailure) with
            { Mutation = started ? HardwareMutation.Unknown : HardwareMutation.NoMutation });
        }
        finally { currentAdmission.Value = null; if (started) Interlocked.Decrement(ref activeWrites); if (acquired) hardwareGate.Release(); }
    }

    internal static bool IsManaged(HardwareCommand command) => command is SetPerformanceModeCommand or
        SetCpuTuningCommand or SetCpuTuningBatchCommand or SetGpuFrequencyLimitCommand or SetGpuVfCurveCommand or
        SetGpuMemoryOffsetCommand or SetGpuCoreOffsetCommand or SetGpuPowerLimitCommand or SetGpuPowerPolicyCommand or
        SetFanControlCommand or ReleaseFanControlCommand or SetStrongCoolingCommand or SetKeyboardLightingCommand or RestoreKeyboardLightingPreviewCommand or SetLidLogoCommand or
        ApplyControlRequestCommand or ApplyTurboBranchCommand or ReleaseTurboBranchCommand;

    private static CommandResult Rejected(HardwareCommand command, ErrorCode code) =>
        new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
            ServiceError.Create(code, command.OperationId, false), false) { Mutation = HardwareMutation.NoMutation };

    private static bool IsUnresolved(CommandResult result) => result.State == CommandState.RecoveryRequired ||
        result.Mutation == HardwareMutation.Unknown && result.Error is not null ||
        result.ExecutionState?.NativeModeApplyState == Jiaolong.Contracts.Models.ApplyState.Unknown ||
        result.ExecutionState?.Pages.Any(page => page.State == Jiaolong.Contracts.Models.ApplyState.Unknown) == true;

    private sealed record Operation(string Owner, string Fingerprint, long Generation, bool Managed, bool Automatic)
    {
        public ClientIdentity? ClientIdentity { get; init; }
        public TaskCompletionSource<CommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed record ExecutionAdmission(long Generation, string Owner, bool Automatic)
    {
        public ClientIdentity? ClientIdentity { get; init; }
        public Guid OperationId { get; init; }
        public string? CommandKind { get; init; }
    }
}
