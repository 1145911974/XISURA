using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private async Task<CommandResult?> CheckPersistedAdmissionAsync(HardwareCommand command, CancellationToken token,
        bool blockAnyPending)
    {
        if (ControlExecutionCoordinator.IsManaged(command) && provider is AdaptiveAutomationHardwareProvider adaptive &&
            await adaptive.HasRetainedLegacyRecoveryAsync(token))
            return RecoveryRefusal(command, "adaptivePersistedRecoveryRequired");
        if ((!ControlExecutionCoordinator.IsManaged(command) && command is not SetMuxModeCommand) || journal is null) return null;
        try
        {
            var saved = await journal.ReadAsync(token);
            if (saved.Unresolved.Any(item => item.OperationId == command.OperationId))
                return Rejected(command, ErrorCode.IdempotencyConflict) with
                    { State = CommandState.RecoveryRequired, Mutation = HardwareMutation.NoMutation };
            if (ControlExecutionJournal.RequiresGpuRecoveryCheck(command) &&
                saved.Unresolved.Any(ControlExecutionJournal.HasPendingGpuRecovery))
                return RecoveryRefusal(command, "gpuPersistedRecoveryRequired");
            return blockAnyPending && saved.Unresolved.Length > 0
                ? RecoveryRefusal(command, "persistedRecoveryRequired") : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Rejected(command, ErrorCode.CancelledBeforeApply) with { Mutation = HardwareMutation.NoMutation };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Rejected(command, ErrorCode.InternalFailure) with
            {
                Mutation = HardwareMutation.NoMutation,
                Error = ServiceError.Create(ErrorCode.InternalFailure, command.OperationId, false) with
                    { Details = new() { ["reason"] = "controlJournalUnreadable", ["errorType"] = error.GetType().Name,
                        ["writeAttempted"] = bool.FalseString } }
            };
        }
    }

    private static CommandResult RecoveryRefusal(HardwareCommand command, string reason) =>
        Rejected(command, ErrorCode.RollbackFailed) with
        {
            State = CommandState.RecoveryRequired,
            Mutation = HardwareMutation.NoMutation,
            Error = ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false) with
                { Details = new() { ["reason"] = reason, ["writeAttempted"] = bool.FalseString } }
        };
}
