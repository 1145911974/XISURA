using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static UnresolvedControlOperation? PriorPendingFan(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        var step = CompoundPageStep(saved, ControlPageId.Fan);
        if (saved.Unresolved is not [var pending] || (step?.Command.GetType().Name ?? pending.CommandKind) is not
                (nameof(SetStrongCoolingCommand) or nameof(SetFanControlCommand) or nameof(ReleaseFanControlCommand)) ||
            pending.OwnerSid != session.OwnerSid || pending.Identity != session.Identity ||
            !CompletedCompoundGpu(session, saved) || !CompletedCompoundMode(session, saved) ||
            saved.Current is null || !CanRestorePriorNativeFan(session, saved) ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(pending.State), JsonSerializer.SerializeToElement(saved.Current)) ||
            step is null && pending.State.Pages.Any(page => page.Page != ControlPageId.Fan) ||
            !pending.State.Fields.Any(pair => pair.Key.StartsWith("fan.") && pair.Value.OwnerOperationId == pending.OperationId) ||
            step is null && pending.State.Fields.Any(pair => (pair.Value.OwnerOperationId == pending.OperationId ||
                pair.Value.WriteState is not (ApplyState.Confirmed or ApplyState.SubmittedUnverified)) && !pair.Key.StartsWith("fan.")))
            return null;
        return pending;
    }

    private Task<bool> RestorePriorPendingFanAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, CancellationToken token)
    {
        var pending = PriorPendingFan(session, saved) ?? throw new InvalidDataException("priorPendingFanUnverified");
        return Execution.WithRecoveryAdmissionAsync(pending.OperationId, session.OwnerSid, async () =>
        {
            var scope = new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch,
                session.Handle.SessionId, session.OwnerSid, session.Identity);
            var fanMapping = SessionFanMapping(session, saved);
            var mappingObservedAfter = DateTimeOffset.UtcNow;
            using var admission = journal!.BindFanRecovery(new(pending.OperationId, scope));
            bool previouslyDispatched = hardwareDispatched.Value?.Started == true;
            var result = await TrackHardwareDispatchAsync(() => provider.RecoverSessionFanAsync(pending.OperationId, scope, token));
            if (!previouslyDispatched && result.Mutation == HardwareMutation.NoMutation && hardwareDispatched.Value is { } dispatch)
                dispatch.Started = false;
            var controls = await ReadControlBaselineForExecutionAsync(token, true);
            var step = new ExitStep("fan", null, SettingRequest.Set("Auto"))
            {
                FanStrongCoolingTarget = session.Baseline.StrongCooling,
                FanFirmwareOnly = ControlExecutionJournal.IsFanFirmwareOnlyRecovery(session, saved),
                FanMappingOriginalInitialization = fanMapping?.Original,
                FanMappingTargetWritesStarted = fanMapping?.TargetWritesStarted,
                FanMappingObservedAfterUtc = fanMapping is null ? null : mappingObservedAfter
            };
            if (result is not { State: CommandState.Applied, Error: null } || !ExitMatches(step, controls, true)) return false;
            var verified = await journal.ReadAsync(token);
            if (!SamePriorJournal(verified, await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token)) ||
                PriorPendingFan(session, verified)?.OperationId != pending.OperationId)
                throw new InvalidDataException("priorPendingFanOriginalRecordsChanged");
            if (verified.FanEcRecovery?.Scope == scope && !ControlExecutionJournal.FanCheckpointComplete(verified.FanEcRecovery.Checkpoint) ||
                verified.FanFirmwareRecovery?.Scope == scope && !ControlExecutionJournal.FanFirmwareComplete(verified.FanFirmwareRecovery))
                return false;
            var priorState = controlExecutionState;
            var priorFields = ownedFields.Where(pair => pair.Key.StartsWith("fan.")).ToDictionary(pair => pair.Key, pair => pair.Value);
            try
            {
                var observed = ControlPlanCompiler.Read(ControlPageId.Fan, controls);
                foreach (var pair in priorFields)
                    ownedFields[pair.Key] = pair.Value with { State = ApplyState.Confirmed,
                        LastWritten = observed.GetValueOrDefault(pair.Key[4..]) };
                Volatile.Write(ref controlExecutionState, controlExecutionState! with { Fields = SnapshotFields(), Pages = [],
                    NativeMode = controls.PerformanceMode ?? controlExecutionState!.NativeMode, NativeModeApplyState = ApplyState.Confirmed, PendingStep = null });
                // Resolve the original command only after native restoration; never relabel it as End.
                await PersistExecutionAsync(pending.OperationId, false, token, commandKind: pending.CommandKind);
                Volatile.Write(ref state, State with { Controls = controls });
                return true;
            }
            catch
            {
                foreach (var pair in priorFields) ownedFields[pair.Key] = pair.Value;
                Volatile.Write(ref controlExecutionState, priorState);
                throw;
            }
        });
    }
}
