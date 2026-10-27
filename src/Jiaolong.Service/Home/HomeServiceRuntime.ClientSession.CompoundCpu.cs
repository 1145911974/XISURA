using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static SetCpuTuningBatchCommand? CpuBatchForStep(HardwareCommand command, Guid parent) => command switch
    {
        SetCpuTuningCommand cpu => new(parent, [cpu.Plan], cpu.RiskConfirmed),
        SetCpuTuningBatchCommand batch => new(parent, batch.Plans, batch.RiskConfirmed) { NativeMode = batch.NativeMode },
        _ => null
    };

    private static PendingControlStep? CompoundStep(ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending] || pending.CommandKind is not
                (nameof(ApplyControlRequestCommand) or nameof(SetKeyboardLightingCommand) or nameof(SetLidLogoCommand)) ||
            saved.Current?.PendingStep is not { Command: SetCpuTuningCommand or SetCpuTuningBatchCommand or
                SetGpuCoreOffsetCommand or SetGpuMemoryOffsetCommand or SetGpuVfCurveCommand or
                SetFanControlCommand or SetStrongCoolingCommand or ReleaseFanControlCommand or
                SetKeyboardLightingCommand or SetLidLogoCommand, Fields: { Length: > 0 } } step ||
            step.Command.OperationId == Guid.Empty || step.Command.ClientSessionId is null ||
            pending.CommandKind != nameof(ApplyControlRequestCommand) &&
                (step.Command.OperationId != pending.OperationId || step.Command.GetType().Name != pending.CommandKind) ||
            CommandValidation.Validate(step.Command) is not null ||
            !SameRollbackEvidence(saved.Current, pending.State) ||
            !SameRollbackEvidence(saved.Current.CpuWriteBatch, CpuBatchForStep(step.Command, pending.OperationId)) ||
            !step.Fields.Order().SequenceEqual(TrackedCommandFields(step.Command).Select(field => FieldKey(CommandPage(step.Command), field)).Order()) ||
            step.Fields.Any(key => !saved.Current.Fields.TryGetValue(key, out var field) || field.OwnerOperationId != pending.OperationId ||
                field.ClientSessionId != step.Command.ClientSessionId) ||
            saved.Current.Fields.Any(pair => !step.Fields.Contains(pair.Key) &&
                pair.Value.WriteState is not (ApplyState.Confirmed or ApplyState.SubmittedUnverified)) ||
            saved.Current.Pages.Count(page => page.Page == CommandPage(step.Command)) != 1 ||
            saved.Current.Pages.Any(page => page.Page != CommandPage(step.Command) &&
                page.State is not (ApplyState.Confirmed or ApplyState.SubmittedUnverified))) return null;
        var targets = IntendedValues(step.Command);
        return step.Fields.All(key => ExitSame(saved.Current.Fields[key].LastWritten, targets.GetValueOrDefault(key[(key.IndexOf('.') + 1)..]))) ? step : null;
    }

    private static PendingControlStep? CompoundCpuStep(ControlJournalSnapshot saved) =>
        CompoundStep(saved) is { Command: SetCpuTuningCommand or SetCpuTuningBatchCommand } step ? step : null;

    private static PendingControlStep? CompoundPageStep(ControlJournalSnapshot saved, ControlPageId page) =>
        CompoundStep(saved) is { } step && CommandPage(step.Command) == page ? step : null;

    private static string? PendingCpuCommandKind(ControlJournalSnapshot saved) => saved.Unresolved is [var pending]
        ? pending.CommandKind == nameof(ApplyControlRequestCommand) ? CompoundCpuStep(saved)?.Command.GetType().Name : pending.CommandKind : null;

    private static KeyValuePair<string, FieldExecutionState>[] PendingCpuOwnedFields(ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending] || saved.Current is not { } current) return [];
        var step = pending.CommandKind == nameof(ApplyControlRequestCommand) ? CompoundCpuStep(saved) : null;
        return current.Fields.Where(pair => pending.CommandKind == nameof(ApplyControlRequestCommand)
            ? step?.Fields.Contains(pair.Key) == true : pair.Value.OwnerOperationId == pending.OperationId).ToArray();
    }

    private static bool CompletedCompoundMode(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending] || pending.NativeModeAttempt is not { } mode) return true;
        return CompoundStep(saved) is not null && mode.StepId == pending.OperationId &&
            mode.ServiceSessionId == session.ServiceSessionId && mode.HardwareEpoch == session.Handle.HardwareEpoch &&
            mode.ClientSessionId == session.Handle.SessionId && mode.OwnerSid == session.OwnerSid && mode.Identity == session.Identity &&
            mode.Observed == mode.Target && mode.Target == saved.Current!.NativeMode &&
            saved.Current.NativeModeApplyState == ApplyState.Confirmed && saved.Current.NativeModeOwnerClientSessionId == session.Handle.SessionId;
    }
}
