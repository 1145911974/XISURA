using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    public bool CanRunClientAutomation(string? ownerSid)
    {
        var session = Volatile.Read(ref activeClientSession);
        return session is null ? !requireClientSessions : session.OwnerSid == ownerSid &&
            session.Phase == ClientControlSessionPhase.Active && Volatile.Read(ref clientSessionClosing) == 0 && SessionEpochMatches(session);
    }

    public async Task<CommandResult?> RecoverExitedClientSessionAsync(IClientProcessProbe probe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = Volatile.Read(ref activeClientSession);
        if (IsSuspended || session?.Phase == ClientControlSessionPhase.Completed) return null;
        if (session is null || !SessionEpochMatches(session))
            return await RestorePriorClientSessionAsync(probe, token);
        Guid operationId = Guid.NewGuid();
        switch (probe.Observe(session.ProcessId, session.ProcessCreationFileTimeUtc))
        {
            case ClientProcessState.Running: return null;
            case ClientProcessState.Exited: break;
            default: return SessionRefused(operationId, ErrorCode.ServiceUnavailable, true);
        }
        // Internal service path only: authority is the frozen, pipe-verified capture, never a request payload.
        var owner = ClientIdentity.LocalAdministrator(session.OwnerSid, session.OwnerSessionId) with
            { ProcessId = session.ProcessId, ProcessCreationFileTimeUtc = session.ProcessCreationFileTimeUtc };
        return await EndClientControlSessionAsync(operationId, session.Handle.SessionId, owner, token);
    }
}
