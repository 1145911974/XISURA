using Jiaolong.Contracts.Models;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed record NativeModeRecoveryAttempt(Guid StepId, Guid ServiceSessionId, long HardwareEpoch, string OwnerSid,
    Guid ClientSessionId, HardwareIdentity Identity, PerformanceMode Before, PerformanceMode Target, DateTimeOffset CapturedAtUtc)
{
    public PerformanceMode? Observed { get; init; }
    public PerformanceMode OriginalBefore { get; init; } = Before;
}

public sealed partial class ControlExecutionJournal
{
    internal sealed record NativeModeOperation(Guid OperationId, Guid ServiceSessionId, long HardwareEpoch, string OwnerSid, Guid ClientSessionId);
    private readonly AsyncLocal<NativeModeScope?> nativeModeOperation = new();
    internal NativeModeOperation? CurrentNativeModeOperation => nativeModeOperation.Value is { IsActive: true } scope ? scope.Operation : null;
    internal IDisposable BindNativeModeOperation(NativeModeOperation operation)
    {
        var scope = new NativeModeScope(this, operation, nativeModeOperation.Value);
        nativeModeOperation.Value = scope;
        return scope;
    }

    private sealed class NativeModeScope(ControlExecutionJournal owner, NativeModeOperation operation, NativeModeScope? prior) : IDisposable
    {
        private int active = 1;
        internal NativeModeOperation Operation => operation;
        internal bool IsActive => Volatile.Read(ref active) != 0;
        public void Dispose() { if (Interlocked.Exchange(ref active, 0) != 0) owner.nativeModeOperation.Value = prior; }
    }

    internal async Task RecordNativeModeAsync(Guid stepId, HardwareIdentity identity, PerformanceMode before, PerformanceMode target,
        PerformanceMode? observed, CancellationToken token)
    {
        var operation = CurrentNativeModeOperation ?? throw new InvalidDataException("nativeModeRecoveryAdmissionMissing");
        await gate.WaitAsync(token);
        try
        {
            var saved = await LoadAsync(token);
            var disk = await new ControlExecutionJournal(RootDirectory).ReadAsync(token);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved), JsonSerializer.SerializeToElement(disk)))
                throw new InvalidDataException("nativeModeRecoveryJournalChanged");
            var pending = saved.Unresolved.SingleOrDefault(item => item.OperationId == operation.OperationId);
            if (pending is null || pending.OwnerSid != operation.OwnerSid || pending.Identity != identity ||
                pending.State.HardwareEpoch != operation.HardwareEpoch || saved.Unresolved.Any(item =>
                    item.OperationId != operation.OperationId && item.NativeModeAttempt is not null))
                throw new InvalidDataException("nativeModeRecoveryAdmissionMismatch");
            var attempt = new NativeModeRecoveryAttempt(stepId, operation.ServiceSessionId, operation.HardwareEpoch,
                operation.OwnerSid, operation.ClientSessionId, identity, before, target, DateTimeOffset.UtcNow) { Observed = observed };
            if (pending.NativeModeAttempt is { } prior)
            {
                if (prior.ServiceSessionId != attempt.ServiceSessionId || prior.HardwareEpoch != attempt.HardwareEpoch ||
                    prior.OwnerSid != attempt.OwnerSid || prior.ClientSessionId != attempt.ClientSessionId || prior.Identity != identity ||
                    observed is not null && (prior.StepId != stepId || prior.Before != before || prior.Target != target))
                    throw new InvalidDataException("nativeModeRecoveryAttemptChanged");
                attempt = attempt with { OriginalBefore = prior.OriginalBefore };
                if (prior.StepId == stepId)
                {
                    if (prior.Before != before || prior.Target != target) throw new InvalidDataException("nativeModeRecoveryStepChanged");
                    attempt = prior with { Observed = observed };
                }
            }
            if (!ValidNativeModeAttempt(attempt, pending)) throw new InvalidDataException("nativeModeRecoveryAttemptInvalid");
            var next = Clone(saved with { Unresolved = saved.Unresolved.Select(item => item.OperationId == operation.OperationId
                ? item with { NativeModeAttempt = attempt } : item).ToArray() });
            await WriteSnapshotAsync(next, token);
            snapshot = next;
        }
        finally { gate.Release(); }
    }

    private static bool KnownNativeMode(PerformanceMode value) => value is PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo;
    private static bool ValidNativeModeAttempt(NativeModeRecoveryAttempt value, UnresolvedControlOperation operation) =>
        value.StepId != Guid.Empty && value.ServiceSessionId != Guid.Empty && value.ClientSessionId != Guid.Empty &&
        value.HardwareEpoch > 0 && value.HardwareEpoch == operation.State.HardwareEpoch &&
        !string.IsNullOrWhiteSpace(value.OwnerSid) && value.OwnerSid.Length <= 256 && value.OwnerSid == operation.OwnerSid &&
        value.Identity == operation.Identity && KnownNativeMode(value.Before) && KnownNativeMode(value.Target) && KnownNativeMode(value.OriginalBefore) &&
        (value.Observed is null || KnownNativeMode(value.Observed.Value)) && value.CapturedAtUtc != DateTimeOffset.MinValue &&
        value.CapturedAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1);
}
