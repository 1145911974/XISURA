using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private ControlExecutionJournal.FanRecoveryOperation? fanRecoveryOperation;
    private CancellationToken fanRecoveryCancellation;
    private void EnsureFanRecoveryAccess()
    {
        if (fanRecoveryOperation is not null && (BackgroundWorkPaused || controlJournal?.CurrentFanRecoveryOperation != fanRecoveryOperation))
            throw new IOException("fanRecoveryAdmissionExpired");
    }

    public Task<CommandResult> RecoverSessionFanAsync(Guid id, FanEcRecoveryScope scope, CancellationToken token)
    {
        lock (commandGate) lock (gate) return Task.FromResult(RecoverSessionFanLocked(id, scope, token));
    }

    private CommandResult RecoverSessionFanLocked(Guid id, FanEcRecoveryScope scope, CancellationToken token)
    {
        BldingFanEcTransport? transport = fanTransport;
        long ecBefore = transport?.ValueWriteAttempts ?? 0, firmwareBefore = fanFirmwareWriteAttempts;
        bool Written() => (transport?.ValueWriteAttempts ?? 0) != ecBefore || fanFirmwareWriteAttempts != firmwareBefore;
        try
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var operation = controlJournal?.CurrentFanRecoveryOperation;
            if (operation is null || id == Guid.Empty || operation.OperationId != id || operation.Scope != scope || scope.Identity != identity)
                throw new InvalidDataException("fanRecoveryAdmissionMissing");
            var capture = controlJournal!.ValidateFanCaptureAsync(identity, token).GetAwaiter().GetResult();
            var expectedScope = new FanEcRecoveryScope(capture.ServiceSessionId, capture.Handle.HardwareEpoch, capture.Handle.SessionId, capture.OwnerSid, capture.Identity);
            var saved = controlJournal.ReadAsync(token).GetAwaiter().GetResult();
            bool firmwareOnly = ControlExecutionJournal.IsFanFirmwareOnlyRecovery(capture, saved);
            bool completedTransport = transport?.LeaseCompleted == true && saved.FanEcRecovery?.Scope == scope &&
                ControlExecutionJournal.FanCheckpointComplete(saved.FanEcRecovery.Checkpoint) &&
                transport.MatchesRecoveryLease(saved.FanEcRecovery.Checkpoint);
            bool originalTransport = fanRecoveryOperation is null && saved.FanEcRecovery?.Scope == scope &&
                transport?.MatchesRecoveryLease(saved.FanEcRecovery.Checkpoint) == true;
            if (capture.Phase != ClientControlSessionPhase.Closing || scope != expectedScope ||
                !saved.Unresolved.Any(item => item.OperationId == id && item.OwnerSid == scope.OwnerSid && item.Identity == identity) ||
                !firmwareOnly && saved.FanEcRecovery?.Scope != scope || capture.Baseline.StrongCooling is not bool original ||
                firmwareOnly && (transport is not null || activeFanPlan is not null || fanTouched || strongCoolingActive) ||
                transport is not null && fanRecoveryOperation != operation && !completedTransport && !originalTransport || ThirdPartyFanWriterRunning())
                throw new InvalidDataException("fanRecoveryOriginalAuthorityMismatch");
            controlJournal.VerifyFanFirmwareOnlyHistoryAsync(capture, saved, token).GetAwaiter().GetResult();
            // The retained EC callback must observe this admitted retry, not an earlier cancelled request.
            fanRecoveryCancellation = token;
            long work = Volatile.Read(ref backgroundWorkPaused);
            var lifecycle = Volatile.Read(ref powerLifecycle);
            long? version = lifecycle?.Version;
            bool Current() => !BackgroundWorkPaused && Volatile.Read(ref backgroundWorkPaused) == work &&
                ReferenceEquals(Volatile.Read(ref powerLifecycle), lifecycle) && lifecycle?.Version == version &&
                (controlJournal.CurrentFanRecoveryOperation == operation || transport?.LeaseCompleted == true);
            void Check() { fanRecoveryCancellation.ThrowIfCancellationRequested(); if (!Current()) throw new IOException("fanRecoveryLifecycleChanged"); }
            Check();
            if (!TryReadControlByte(StrongCoolingBinding(), out var firmware, token) || firmware > 1 ||
                (original ? saved.FanFirmwareRecovery is not { } record || record.Scope != scope || record.OriginalBefore != 1 ||
                    ControlExecutionJournal.FanFirmwareComplete(record) && firmware != 1 : saved.FanFirmwareRecovery?.Scope == scope || firmware != 0))
                throw new InvalidDataException("fanRecoveryFirmwareOwnershipChanged");
            fanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            fanRecoveryOperation = operation;
            if (!firmwareOnly && transport is null)
            {
                var binding = controlJournal.CreateFanEcRecoveryCheckpointWriterAsync(identity, token).GetAwaiter().GetResult();
                transport = fanTransport = fanTransportFactory(Current, () => Current() && CpuHardwareReadAllowed, checkpoint =>
                { Check(); binding.Writer(checkpoint); Check(); });
                transport.OpenForRecovery(binding.Recovery.Checkpoint);
            }
            Check();
            if (!firmwareOnly)
            {
                fanTouched = true;
                if (!completedTransport) transport!.FinishLease();
                fanLastEcMapping = transport!.ReadMappingState();
                fanLastEcState = transport.HasMappingLease ? null : transport.ReadControlState();
                fanTouched = false;
                if (!transport.HasMappingLease)
                {
                    transport.Dispose();
                    fanTransport = null;
                }
            }
            activeFanPlan = null;
            strongCoolingActive = false;
            fanPreviousStrongCooling = original ? (byte)1 : (byte)0;
            Check();
            RestoreStrongCoolingLocked();
            var completed = controlJournal.ReadAsync(token).GetAwaiter().GetResult();
            if ((!firmwareOnly && (completed.FanEcRecovery is not { } ec || ec.Scope != scope || !ControlExecutionJournal.FanCheckpointComplete(ec.Checkpoint))) ||
                original && (completed.FanFirmwareRecovery is not { } fw || fw.Scope != scope || !ControlExecutionJournal.FanFirmwareComplete(fw)))
                throw new InvalidDataException("fanRecoveryNotVerified");
            Check();
            fanRecoveryOperation = null;
            return FanSubmitted(id) with { Mutation = Written() ? HardwareMutation.Changed : HardwareMutation.NoMutation };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or OperationCanceledException)
        {
            var result = FanRecoveryRequired(id);
            return result with { Mutation = Written() ? HardwareMutation.Unknown : HardwareMutation.NoMutation,
                Error = result.Error! with { Details = new() { ["reason"] = error.Message } } };
        }
    }
}
