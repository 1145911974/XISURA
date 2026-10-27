using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static bool IdleFanReleaseShape(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, Guid exitOperation)
    {
        bool EpochMatches(long epoch) => epoch == session.Handle.HardwareEpoch ||
            session.Handle.HardwareEpoch < long.MaxValue && epoch == session.Handle.HardwareEpoch + 1;
        bool ReleasePage(PageApplyResult[] pages) => pages is [{ Page: ControlPageId.Fan, Key: null, Revision: 0,
            State: ApplyState.Unknown, ConfirmedFields.Length: 0, UnverifiedFields.Length: 0, SkippedFields.Length: 0,
            ErrorCode: "RollbackFailed" }];
        if (session.Phase != ClientControlSessionPhase.Closing || session.Baseline.ActiveFanControlPlan is not null || session.Baseline.StrongCooling != false ||
            saved.Current is not { PendingStep: null, CpuWriteBatch: null, WindowsWriteProofHash: null,
                NativeModeOwnerClientSessionId: null, WindowsScopeSessionId: null, NativeModeApplyState: ApplyState.Confirmed } current ||
            saved.RecoveryScope is not { FanOwnerClientSessionId: null } scope ||
            !EpochMatches(current.HardwareEpoch) || current.Pages.Length != 0 && !ReleasePage(current.Pages)) return false;
        string[] keys = ["fan.strategy", "fan.maximumRpm", "fan.fixedRpm", "fan.cpu", "fan.gpu"];
        bool FieldsMatch(Dictionary<string, FieldExecutionState> fields, Guid operation)
        {
            var fan = fields.Where(pair => pair.Key.StartsWith("fan.", StringComparison.OrdinalIgnoreCase)).ToArray();
            return fields.Count == keys.Length && fan.Length == keys.Length && fan.All(pair => keys.Contains(pair.Key, StringComparer.Ordinal) &&
                pair.Value is { Origin: ControlOrigin.ManualLive, WriteState: ApplyState.SubmittedUnverified,
                    BaselineDetails: null, LastWrittenDetails: null, WriteBeforeDetails: null, WriteTargetDetails: null } field &&
                field.ClientSessionId == session.Handle.SessionId && field.OwnerOperationId == operation &&
                (pair.Key == "fan.strategy"
                    ? ExitSame(field.Baseline, SettingRequest.Set("Auto")) && ExitSame(field.WriteBefore, field.Baseline) && ExitSame(field.LastWritten, field.Baseline)
                    : field.Baseline is null && field.WriteBefore is null && field.LastWritten is null));
        }
        if (!current.Fields.TryGetValue("fan.strategy", out var strategy) || strategy.OwnerOperationId == Guid.Empty ||
            !FieldsMatch(current.Fields, strategy.OwnerOperationId)) return false;
        if (!scope.Fields.TryGetValue("fan.strategy", out var authority) ||
            !keys.All(key => scope.Fields.TryGetValue(key, out var field) && field == authority) ||
            saved.Unresolved.Count(item => item.OperationId != exitOperation) > 1 ||
            saved.Unresolved.Any(item => item.OperationId == Guid.Empty || item.GpuRecovery is not null || item.GpuAttempt is not null ||
                item.NativeModeAttempt is not null || item.State.PendingStep is not null || item.State.CpuWriteBatch is not null ||
                item.State.WindowsWriteProofHash is not null || item.State.NativeModeOwnerClientSessionId is not null ||
                item.State.WindowsScopeSessionId is not null || item.State.NativeModeApplyState != ApplyState.Confirmed ||
                !EpochMatches(item.State.HardwareEpoch) ||
                !FieldsMatch(item.State.Fields, strategy.OwnerOperationId) ||
                (item.OperationId == exitOperation
                    ? item.CommandKind != "EndClientControlSession" || item.OwnerSid != session.OwnerSid || item.State.Pages.Length != 0
                    : item.CommandKind != "ReleaseFanControlCommand" || item.OwnerSid != "service" || !ReleasePage(item.State.Pages)))) return false;
        // Shutdown invalidates the epoch without granting new field authority.
        return PriorScopeMatches(session, saved with
        {
            Current = current with { HardwareEpoch = session.Handle.HardwareEpoch },
            Unresolved = saved.Unresolved.Select(item => item with { OwnerSid = session.OwnerSid,
                State = item.State with { HardwareEpoch = session.Handle.HardwareEpoch } }).ToArray()
        });
    }

    private async Task<bool> VerifyIdleFanReleaseAsync(ClientControlSessionSnapshot session, Guid exitOperation,
        HomeControlState? controls, CancellationToken token, ControlJournalSnapshot? original = null)
    {
        if (journal is null || fanOwner is not null || fanOwnerClientSessionId is not null ||
            controls is not null && !IdleFanReleaseLive(controls)) return false;
        var saved = original ?? await journal.ReadAsync(token);
        if (!IdleFanReleaseShape(session, saved, exitOperation)) return false;
        await journal.VerifyIdleFanReleaseHistoryAsync(session, saved, token);
        return SamePriorJournal(saved, await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token));
    }

    private static bool IdleFanReleaseLive(HomeControlState controls) => controls.ActiveFanControlPlan is null &&
        !controls.FanAutomaticCeilingActive && controls.StrongCooling == false && controls.FanEcMapping is null or { Restored: true } &&
        (controls.FanEcControl is null || (controls.FanEcControl.Control & 0x0A) == 0 &&
            controls.FanEcControl.CpuTarget == 0 && controls.FanEcControl.GpuTarget == 0);
}
