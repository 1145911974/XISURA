using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private async Task PrepareRetainedExitAsync(Guid operationId, ClientControlSessionSnapshot session, CancellationToken token)
    {
        if (journal is null) throw new InvalidDataException("exitJournalMissing");
        var disk = await clientSessions!.ReadAsync(token);
        if (disk is null || !ClientControlSessionStore.SameCapture(session, disk) || disk.Phase != session.Phase ||
            session.Phase is not (ClientControlSessionPhase.Active or ClientControlSessionPhase.Closing))
            throw new InvalidDataException("exitSessionChanged");
        var store = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
        if (File.Exists(store.FilePath) && new FileInfo(store.FilePath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("exitRecoveryTooLarge");
        var previous = File.Exists(store.FilePath) ? await store.ReadAsync<ExitPlan>(token) : null;
        if (previous is null && File.Exists(store.FilePath)) throw new InvalidDataException("exitRecoveryInvalid");
        if (previous is not null)
        {
            if (previous.SessionId == session.Handle.SessionId)
            {
                if (!previous.KeepChanges) throw new InvalidDataException("exitRecoveryAlreadyStarted");
                VerifyRetainedSession(session, previous);
                return;
            }
            await VerifyArchivedPriorExitPlanAsync(session, previous, token);
        }
        var saved = await journal.ReadAsync(token);
        var windows = await ReadWindowsScopesAsync(session,
            saved.Current?.WindowsScopeSessionId is not null || windowsScopeSessionId == session.Handle.SessionId, token);
        var plan = new ExitPlan(1, session.Handle.SessionId, operationId,
            [new("retainedChanges", null, null) { Status = "Verified" }])
        { KeepChanges = true, RetainedSession = session with { Phase = ClientControlSessionPhase.Closing },
            RetainedJournal = saved, RetainedWindows = windows };
        if (!ValidRetainedExitPlan(plan)) throw new InvalidDataException("exitRetentionUnresolved");
        if (!SamePriorJournal(saved, await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token)))
            throw new InvalidDataException("exitJournalChangedOrMissing");
        if (JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { WriteIndented = true }).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("exitRetentionTooLarge");
        var latest = await store.ReadAsync<ExitPlan>(token);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(previous), JsonSerializer.SerializeToElement(latest)))
            throw new InvalidDataException("exitRecoveryChanged");
        await store.WriteAsync(plan, token);
    }

    private static bool ValidRetainedExitPlan(ExitPlan plan) => plan.IdleFanRestart is not null
        ? ValidIdleFanRestartPlan(plan) : plan is
        { SchemaVersion: 1, KeepChanges: true, LegacyEmptyProof: null, WindowsScopeSessionId: null, GpuRecoveryReady: true,
          RetainedSession: { Phase: ClientControlSessionPhase.Closing, Handle: not null, Identity: not null, Baseline: not null },
          RetainedJournal: { SchemaVersion: 1, Unresolved.Length: 0, RecoveryScope.Fields: not null,
              Current: { PendingStep: null, Fields: not null, Pages: not null } } } &&
        plan.OperationId != Guid.Empty && plan.SessionId != Guid.Empty &&
        plan.SessionId == plan.RetainedSession.Handle.SessionId &&
        plan.Steps is [{ Key: "retainedChanges", Status: "Verified", Expected: null, Target: null }] &&
        plan.RetainedJournal.Current.Fields.Values.All(field => field is { WriteState: ApplyState.Confirmed }) &&
        plan.RetainedJournal.RecoveryScope.Fields.Values.All(field => field is not null) &&
        (plan.RetainedJournal.Current.NativeModeOwnerClientSessionId is null ||
            plan.RetainedJournal.Current.NativeModeApplyState == ApplyState.Confirmed) &&
        // Windows keeps its last pre-write proof after success; the control journal determines whether it is unresolved.
        (plan.RetainedWindows is null || plan.RetainedWindows.SessionId == plan.SessionId) &&
        (plan.RetainedJournal.FanEcRecovery is null ||
            ControlExecutionJournal.ValidFanCheckpoint(plan.RetainedJournal.FanEcRecovery.Checkpoint) &&
            plan.RetainedJournal.FanEcRecovery.Checkpoint.Pending.Count == 0 &&
            plan.RetainedJournal.FanEcRecovery.Checkpoint.MappingLease is not { Pending.Length: > 0 } &&
            (plan.RetainedJournal.FanEcRecovery.Scope == RetainedFanScope(plan) ||
                ControlExecutionJournal.FanCheckpointComplete(plan.RetainedJournal.FanEcRecovery.Checkpoint))) &&
        (plan.RetainedJournal.FanFirmwareRecovery is null ||
            plan.RetainedJournal.FanFirmwareRecovery.Observed == plan.RetainedJournal.FanFirmwareRecovery.Target &&
            (plan.RetainedJournal.FanFirmwareRecovery.Scope == RetainedFanScope(plan) ||
                ControlExecutionJournal.FanFirmwareComplete(plan.RetainedJournal.FanFirmwareRecovery))) &&
        PriorScopeMatches(plan.RetainedSession, plan.RetainedJournal);

    private static FanEcRecoveryScope RetainedFanScope(ExitPlan plan) => new(plan.RetainedSession!.ServiceSessionId,
        plan.RetainedSession.Handle.HardwareEpoch, plan.SessionId, plan.RetainedSession.OwnerSid, plan.RetainedSession.Identity);

    private static void VerifyRetainedSession(ClientControlSessionSnapshot session, ExitPlan plan)
    {
        if (!ValidRetainedExitPlan(plan) || !ClientControlSessionStore.SameCapture(session, plan.RetainedSession!))
            throw new InvalidDataException("exitRetentionCaptureChanged");
    }

    private static ControlJournalSnapshot ReleasedRetainedJournal(ExitPlan plan)
    {
        var original = plan.RetainedJournal!;
        var state = original.Current! with
        { Fields = [], Pages = [], NativeModeOwnerClientSessionId = null, WindowsScopeSessionId = null,
            PendingStep = null, CpuWriteBatch = null, WindowsWriteProofHash = null };
        // Retained checkpoints remain in the durable decision; they were not restored.
        return original with { Current = state, RecoveryScope = new(plan.RetainedSession!.ServiceSessionId, new(), null, 0, null),
            Unresolved = plan.IdleFanRestart is null ? original.Unresolved : [],
            FanEcRecovery = null, FanFirmwareRecovery = null };
    }

    private async Task VerifyRetainedExitEvidenceAsync(ClientControlSessionSnapshot session, ExitPlan plan, CancellationToken token)
    {
        VerifyRetainedSession(session, plan);
        var windows = await ReadWindowsScopesAsync(session, plan.RetainedWindows is not null, token);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(windows), JsonSerializer.SerializeToElement(plan.RetainedWindows)))
            throw new InvalidDataException("exitRetentionWindowsChanged");
        var disk = await (clientSessions ?? new ClientControlSessionStore(journal!.RootDirectory)).ReadAsync(token);
        if (disk is null || !ClientControlSessionStore.SameCapture(session, disk) || disk.Phase != session.Phase)
            throw new InvalidDataException("exitRetentionSessionChanged");
        var saved = await new ControlExecutionJournal(journal!.RootDirectory).ReadAsync(token);
        if (!SamePriorJournal(saved, ReleasedRetainedJournal(plan)))
            throw new InvalidDataException("exitRetentionJournalChanged");
    }

    private async Task<CommandResult> CompleteRetainedExitAsync(Guid operationId, ClientControlSessionSnapshot session,
        ExitPlan plan, CancellationToken token)
    {
        VerifyRetainedSession(session, plan);
        var windows = await ReadWindowsScopesAsync(session, plan.RetainedWindows is not null, token);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(windows), JsonSerializer.SerializeToElement(plan.RetainedWindows)))
            throw new InvalidDataException("exitRetentionWindowsChanged");
        var disk = await clientSessions!.ReadAsync(token);
        if (disk is null || disk.Phase != session.Phase || !ClientControlSessionStore.SameCapture(session, disk))
            throw new InvalidDataException("exitRetentionSessionChanged");
        if (session.Phase == ClientControlSessionPhase.Active)
        {
            // A crash may leave the durable Keep decision ahead of Closing. Verify every original record first.
            if (!SamePriorJournal(await journal!.ReadAsync(token), plan.RetainedJournal!) ||
                !SamePriorJournal(await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token), plan.RetainedJournal!))
                throw new InvalidDataException("exitRetentionJournalChanged");
            session = await clientSessions.WriteAsync(session with { Phase = ClientControlSessionPhase.Closing }, token, requireExisting: true);
            Volatile.Write(ref activeClientSession, session);
        }
        if (session.Phase != ClientControlSessionPhase.Closing) throw new InvalidDataException("exitRetentionSessionChanged");
        var released = ReleasedRetainedJournal(plan);
        var saved = await journal!.ReadAsync(token);
        if (!SamePriorJournal(saved, await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token)) ||
            !SamePriorJournal(saved, plan.RetainedJournal!) && !SamePriorJournal(saved, released))
            throw new InvalidDataException("exitRetentionJournalChanged");
        // The explicit restart acknowledgement owns no lighting and must perform no hardware operation.
        if (plan.IdleFanRestart is null) await provider.StopClientLightingDynamicsAsync(token);
        await journal!.ReleaseRetainedSessionAsync(plan.RetainedJournal!, released, token);
        ownedFields.Clear();
        lastSubmittedModeClientSessionId = null; lastSubmittedMode = null;
        fanOwner = null; fanOwnerClientSessionId = null; windowsScopeSessionId = null;
        turboBranches.Clear();
        Volatile.Write(ref controlExecutionState, released.Current!);
        Interlocked.Exchange(ref clientSessionClosing, 1);
        var completed = await clientSessions.WriteAsync(session with { Phase = ClientControlSessionPhase.Completed }, token, requireExisting: true);
        Volatile.Write(ref activeClientSession, completed);
        return SessionCaptured(operationId, completed.Handle, false);
    }
}
