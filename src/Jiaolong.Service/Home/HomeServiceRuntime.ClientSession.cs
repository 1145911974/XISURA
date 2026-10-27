using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Ipc;
using Jiaolong.Service.Storage;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private ClientControlSessionStore? clientSessions;
    private ClientControlSessionSnapshot? activeClientSession;
    private int clientSessionClosing;

    private CommandResult BlockClientSessionForRecovery(Guid operationId)
    {
        if (Volatile.Read(ref activeClientSession)?.Phase == ClientControlSessionPhase.Active)
            Interlocked.Exchange(ref clientSessionClosing, 1);
        return SessionRefused(operationId, ErrorCode.RollbackFailed, true);
    }

    public Task<CommandResult> FreezeClientSessionWritesAsync(Guid operationId, Guid clientSessionId,
        ClientIdentity client, CancellationToken token) =>
        FreezeClientSessionWritesAsync(operationId, clientSessionId, client, token, keepChanges: false);

    private Task<CommandResult> FreezeClientSessionWritesAsync(Guid operationId, Guid clientSessionId,
        ClientIdentity client, CancellationToken token, bool keepChanges)
    {
        token.ThrowIfCancellationRequested();
        var current = Volatile.Read(ref activeClientSession);
        if (current is null || current.Handle.SessionId != clientSessionId || !MatchesSessionProcess(current, client))
            return Task.FromResult(SessionRefused(operationId, ErrorCode.UnauthorizedClient));
        if (operationId == Guid.Empty) return Task.FromResult(SessionRefused(operationId, ErrorCode.ValidationFailed));
        // Close admission before waiting: already queued work must not run ahead of the drain.
        Interlocked.Exchange(ref clientSessionClosing, 1);
        return Execution.WithHardwareGateAsync(async () =>
        {
            current = Volatile.Read(ref activeClientSession)!;
            if (current is null || current.Handle.SessionId != clientSessionId || !MatchesSessionProcess(current, client))
                return SessionRefused(operationId, ErrorCode.UnauthorizedClient);
            if (!SessionEpochMatches(current)) return SessionRefused(operationId, ErrorCode.RollbackFailed, true);
            try
            {
                // The gate has drained in-flight writes; commit Keep before Closing can survive a restart.
                if (keepChanges) await PrepareRetainedExitAsync(operationId, current, token);
                var frozen = await clientSessions!.WriteAsync(current with { Phase = ClientControlSessionPhase.Closing }, token, requireExisting: true);
                Volatile.Write(ref activeClientSession, frozen);
                return SessionEpochMatches(frozen) ? SessionCaptured(operationId, frozen.Handle, current.Phase == ClientControlSessionPhase.Closing)
                    : SessionRefused(operationId, ErrorCode.RollbackFailed, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { return SessionRefused(operationId, ErrorCode.RollbackFailed, true); }
        }, token);
    }

    private bool SessionEpochMatches(ClientControlSessionSnapshot session) => !IsSuspended &&
        session.ServiceSessionId == SessionId && session.Handle.HardwareEpoch == Execution.HardwareEpoch && session.Identity == State.Identity;

    private static bool MatchesSessionProcess(ClientControlSessionSnapshot session, ClientIdentity? client) =>
        client is not null && ClientIdentityVerifier.IsAuthorized(client) && client.UserSid == session.OwnerSid &&
        client.SessionId == session.OwnerSessionId && client.ProcessId == session.ProcessId &&
        client.ProcessCreationFileTimeUtc == session.ProcessCreationFileTimeUtc;

    private CommandResult? ValidateClientSessionCommand(HardwareCommand command, ClientIdentity? client, string? ownerSid)
    {
        var current = Volatile.Read(ref activeClientSession);
        if (current is null)
            return requireClientSessions || command.ClientSessionId is not null
                ? SessionRefused(command.OperationId, ErrorCode.UnauthorizedClient) : null;
        bool automatic = client is null &&
            (command is PublishPresetCatalogCommand && ownerSid == AutomaticCatalogOwner(current.OwnerSid) ||
             ownerSid == current.OwnerSid && provider is AdaptiveAutomationHardwareProvider adaptive && adaptive.IsAutomaticOperation(command.OperationId)) &&
            (command.ClientSessionId is null || command.ClientSessionId == current.Handle.SessionId);
        if (!automatic && (command.ClientSessionId != current.Handle.SessionId || ownerSid != current.OwnerSid ||
            !MatchesSessionProcess(current, client))) return SessionRefused(command.OperationId, ErrorCode.UnauthorizedClient);
        if (!SessionEpochMatches(current)) return SessionRefused(command.OperationId, ErrorCode.RollbackFailed, true);
        return Volatile.Read(ref clientSessionClosing) != 0 || current.Phase != ClientControlSessionPhase.Active
            ? SessionRefused(command.OperationId, ErrorCode.CancelledBeforeApply) : null;
    }

    public Task<CommandResult> BeginClientControlSessionAsync(Guid operationId, ClientIdentity client,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ClientIdentityVerifier.IsAuthorized(client) || string.IsNullOrWhiteSpace(client.UserSid) ||
            client.ProcessId <= 0 || client.ProcessCreationFileTimeUtc <= 0 ||
            client.ProcessCreationFileTimeUtc > DateTime.UtcNow.ToFileTimeUtc())
            return Task.FromResult(SessionRefused(operationId, ErrorCode.UnauthorizedClient));
        if (operationId == Guid.Empty)
            return Task.FromResult(SessionRefused(operationId, ErrorCode.ValidationFailed));
        return Execution.WithHardwareGateAsync(() => CaptureClientSessionAsync(operationId, client, token), token);
    }

    private async Task<CommandResult> CaptureClientSessionAsync(Guid operationId, ClientIdentity client, CancellationToken token)
    {
        long epoch = Execution.HardwareEpoch, lifecycle = powerLifecycle.Version;
        bool Fresh() => !IsSuspended && Execution.HardwareEpoch == epoch && powerLifecycle.Version == lifecycle;
        if (!Fresh()) return SessionRefused(operationId, ErrorCode.ServiceUnavailable);
        try
        {
            if (provider is AdaptiveAutomationHardwareProvider adaptive && await adaptive.HasRetainedLegacyRecoveryAsync(token))
                return BlockClientSessionForRecovery(operationId);
            var current = await GetStateAsync(token);
            if (!Fresh()) return BlockClientSessionForRecovery(operationId);
            if (current.SupportState != DeviceSupportState.Ready)
                return SessionRefused(operationId, ErrorCode.CapabilityUnavailable);
            clientSessions ??= new(journal?.RootDirectory ?? MachinePaths.BaseDirectory);
            var prior = await clientSessions.ReadAsync(token);
            var cached = Volatile.Read(ref activeClientSession);
            if (cached is not null && (prior is null || !ClientControlSessionStore.SameCapture(cached, prior)))
                return BlockClientSessionForRecovery(operationId);
            if (prior is not null) Volatile.Write(ref activeClientSession, prior);
            ClientControlSessionSnapshot? completedSession = null;
            string? completedArchiveSha256 = null;
            if (prior?.Phase == ClientControlSessionPhase.Completed)
            {
                completedArchiveSha256 = await ArchiveCompletedClientSessionAsync(prior, token);
                completedSession = prior;
                prior = null;
            }
            if (prior is not null && (prior.ServiceSessionId != SessionId || prior.Handle.HardwareEpoch != epoch ||
                prior.Identity != current.Identity))
                return BlockClientSessionForRecovery(operationId);
            if (prior is not null && (prior.OwnerSid != client.UserSid || prior.OwnerSessionId != client.SessionId ||
                prior.ProcessId != client.ProcessId || prior.ProcessCreationFileTimeUtc != client.ProcessCreationFileTimeUtc))
                return SessionRefused(operationId, ErrorCode.ConflictDetected);
            if (completedSession is null && Volatile.Read(ref clientSessionClosing) != 0 || prior?.Phase == ClientControlSessionPhase.Closing)
                return SessionRefused(operationId, ErrorCode.ConflictDetected);
            if (journal is not null)
            {
                var savedJournal = await journal.ReadAsync(token);
                if (savedJournal.Unresolved.Length != 0 || prior is null && HasPriorRecovery(savedJournal))
                    return BlockClientSessionForRecovery(operationId);
            }
            if (prior is null && (ownedFields.Count != 0 || lastSubmittedModeClientSessionId is not null ||
                windowsScopeSessionId is not null || fanOwnerClientSessionId is not null || fanOwner is not null ||
                controlExecutionState?.PendingStep is not null))
                return BlockClientSessionForRecovery(operationId);
            string? deviceKey = await provider.ReadDeviceRecoveryKeyAsync(current.Identity, token);
            token.ThrowIfCancellationRequested();
            if (!Fresh()) return BlockClientSessionForRecovery(operationId);
            if (!DeviceRecoveryKey.IsValid(deviceKey)) return prior is not null ? BlockClientSessionForRecovery(operationId)
                : SessionRefused(operationId, ErrorCode.CapabilityUnavailable);
            if (prior is not null && prior.Handle.DeviceKey != deviceKey)
                return BlockClientSessionForRecovery(operationId);
            var boot = readSystemBootTime is null ? null : await readSystemBootTime(token);
            if (!Fresh()) return BlockClientSessionForRecovery(operationId);
            if (readSystemBootTime is not null && (boot is null || boot <= DateTimeOffset.UnixEpoch ||
                boot > DateTimeOffset.FromFileTime(client.ProcessCreationFileTimeUtc) ||
                prior is not null && prior.BootStartedAtUtc != boot)) return BlockClientSessionForRecovery(operationId);
            var power = readSystemPowerContinuity is null ? null : await readSystemPowerContinuity(token);
            if (!Fresh() || readSystemPowerContinuity is not null && (!PowerContinuityEvidence.Valid(power) ||
                prior is not null && prior.PowerContinuity != power)) return BlockClientSessionForRecovery(operationId);
            if (prior is not null)
            {
                token.ThrowIfCancellationRequested();
                return Fresh() ? SessionCaptured(operationId, prior.Handle, true) : BlockClientSessionForRecovery(operationId);
            }
            var baseline = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
            token.ThrowIfCancellationRequested();
            if (!Fresh() || State.Identity != current.Identity) return BlockClientSessionForRecovery(operationId);
            OemCpuConfigurationSnapshot? oemConfiguration = null;
            try { oemConfiguration = await provider.ReadOemCpuConfigurationAsync(token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            token.ThrowIfCancellationRequested();
            if (readSystemBootTime is not null && await readSystemBootTime(token) != boot)
                return BlockClientSessionForRecovery(operationId);
            if (readSystemPowerContinuity is not null && await readSystemPowerContinuity(token) != power)
                return BlockClientSessionForRecovery(operationId);
            if (!Fresh() || State.Identity != current.Identity) return BlockClientSessionForRecovery(operationId);
            var handle = new ClientControlSessionHandle(Guid.NewGuid(), epoch, deviceKey!, DateTimeOffset.UtcNow);
            var saved = new ClientControlSessionSnapshot(1, SessionId, handle, client.UserSid, client.SessionId,
                client.ProcessId, client.ProcessCreationFileTimeUtc, current.Identity,
                new(baseline.PerformanceMode, baseline.CpuTuning, baseline.GpuVf, baseline.KeyboardLighting,
                    baseline.StrongCooling, baseline.ActiveFanControlPlan)
                    { QuickSettings = baseline.QuickSettings, OemCpuConfiguration = oemConfiguration })
                { Phase = requireClientSessions ? ClientControlSessionPhase.Preparing : ClientControlSessionPhase.Active,
                    BootStartedAtUtc = boot, PowerContinuity = power, PreviousCompletedArchiveSha256 = completedArchiveSha256 };
            saved = await clientSessions.WriteAsync(saved, token, completedSessionId: completedSession?.Handle.SessionId);
            // No edit may follow Begin, so recovery authority must already identify this capture.
            var native = baseline.PerformanceMode ?? default;
            var logical = native switch { PerformanceMode.Quiet => ControlModeId.Office,
                PerformanceMode.Turbo => ControlModeId.Turbo, _ => ControlModeId.Gaming };
            var execution = new ControlExecutionState(Execution.Generation, epoch, Execution.Catalog.Revision(client.UserSid),
                logical, native, [], new())
                { NativeModeApplyState = baseline.PerformanceMode is null ? ApplyState.Unknown : ApplyState.Confirmed };
            if (journal is not null)
                await journal.SaveAsync(execution, current.Identity, operationId, false, token, client.UserSid,
                    commandKind: "beginClientSession", noMutation: true,
                    recoveryScope: new(SessionId, new(), null, 0, null));
            Volatile.Write(ref controlExecutionState, execution);
            Volatile.Write(ref activeClientSession, saved);
            Interlocked.Exchange(ref clientSessionClosing, 0);
            token.ThrowIfCancellationRequested();
            // A lifecycle change during durable I/O retains the snapshot as recovery evidence.
            return Fresh() ? SessionCaptured(operationId, handle, false) : BlockClientSessionForRecovery(operationId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return BlockClientSessionForRecovery(operationId); }
    }

    private static CommandResult SessionCaptured(Guid operationId, ClientControlSessionHandle handle, bool replay) =>
        new(operationId, CommandState.Applied, null, RequiredUserAction.None, null, replay)
        { Mutation = HardwareMutation.NoMutation, ClientSession = handle };

    private static CommandResult SessionRefused(Guid operationId, ErrorCode code, bool recovery = false) =>
        new(operationId, recovery ? CommandState.RecoveryRequired : CommandState.Rejected, null, RequiredUserAction.None,
            ServiceError.Create(code, operationId, false), false) { Mutation = HardwareMutation.NoMutation };
}
