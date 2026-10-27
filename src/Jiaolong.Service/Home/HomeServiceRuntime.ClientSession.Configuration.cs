using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    public Task<CommandResult> ConfigureClientSessionExitAsync(Guid operationId, ClientSessionExitConfiguration configuration,
        ClientIdentity client, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Execution.WithHardwareGateAsync(async () =>
        {
            var current = Volatile.Read(ref activeClientSession);
            if (current is null || configuration is null || current.Handle.SessionId != configuration.SessionId || !MatchesSessionProcess(current, client))
                return SessionRefused(operationId, ErrorCode.UnauthorizedClient);
            if (operationId == Guid.Empty || !ClientControlSessionStore.ValidDefaults(configuration.DeviceDefaults, current.Handle.DeviceKey))
                return SessionRefused(operationId, ErrorCode.ValidationFailed);
            if (!SessionEpochMatches(current)) return SessionRefused(operationId, ErrorCode.RollbackFailed, true);
            if (Volatile.Read(ref clientSessionClosing) != 0 || current.Phase == ClientControlSessionPhase.Closing)
                return SessionRefused(operationId, ErrorCode.CancelledBeforeApply);
            bool replay = current.Phase == ClientControlSessionPhase.Active;
            if (replay && current.ExitDefaults != configuration.DeviceDefaults)
                return SessionRefused(operationId, ErrorCode.ConflictDetected);
            try
            {
                if (journal is not null && !PriorScopeMatches(current, await journal.ReadAsync(token)))
                    return BlockClientSessionForRecovery(operationId);
                var configured = await clientSessions!.WriteAsync(current with
                    { Phase = ClientControlSessionPhase.Active, ExitDefaults = configuration.DeviceDefaults }, token,
                    requireExisting: true, configureDefaults: true);
                Volatile.Write(ref activeClientSession, configured);
                return SessionEpochMatches(configured) ? SessionCaptured(operationId, configured.Handle, replay)
                    : BlockClientSessionForRecovery(operationId);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { return BlockClientSessionForRecovery(operationId); }
        }, token);
    }

    private Guid? AdmittedClientSessionId => Volatile.Read(ref activeClientSession) is { } session &&
        session.OwnerSid == Execution.CurrentAdmission?.Owner ? session.Handle.SessionId : null;

    private bool SameClientSession(OwnedField? field) => field?.ClientSessionId == AdmittedClientSessionId;
}
