using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;
using System.Text.Json;

namespace Jiaolong.Service.Home;

internal sealed record GpuClockSubmission(Guid OperationId, HardwareIdentity Identity, string OwnerSid,
    Guid? ClientSessionId, long HardwareEpoch, int? MaximumMhz, DateTimeOffset StartedAtUtc,
    string Status = "Pending", DateTimeOffset? CompletedAtUtc = null, string? Error = null);

public sealed partial class HomeServiceRuntime
{
    private async Task<CommandResult> ExecuteClockSubmissionAsync(SetGpuFrequencyLimitCommand command,
        CancellationToken token, Guid connectionId)
    {
        var admission = Execution.CurrentAdmission;
        if (journal is null || admission is null || admission.Automatic || !command.RiskConfirmed ||
            !CanWriteTuningFields(command, await GetStateAsync(token)))
            return Rejected(command, ErrorCode.CapabilityUnavailable);
        var submission = new GpuClockSubmission(command.OperationId, State.Identity, admission.Owner,
            AdmittedClientSessionId, Execution.HardwareEpoch, command.Megahertz, DateTimeOffset.UtcNow);
        // This persistent driver request has no getter or restorable baseline. Keep it out of session ownership.
        return await SubmitClockWithAuditAsync(journal.RootDirectory, submission,
            () => ExecuteHardwareDirectAsync(command, token, connectionId), token);
    }

    internal static async Task<CommandResult> SubmitClockWithAuditAsync(string root, GpuClockSubmission submission,
        Func<Task<CommandResult>> dispatch, CancellationToken token)
    {
        var store = new AtomicJsonStore(Path.Combine(root, "gpu-clock-submissions"), submission.OperationId.ToString("N") + ".json");
        // Never replay a prior operation after restart, including an interrupted request with an unknown outcome.
        if (File.Exists(store.FilePath))
            return ClockSubmissionUnknown(submission.OperationId, "gpuClockSubmissionAlreadyRecorded");
        await store.WriteAsync(submission, token);
        CommandResult result;
        try { result = await dispatch(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        { result = ClockSubmissionUnknown(submission.OperationId, error.Message); }
        string status = result.Mutation == HardwareMutation.NoMutation ? "NoMutation" :
            result is { State: CommandState.Applied, Error: null } ? "SubmittedUnverified" : "Unknown";
        try
        {
            await store.WriteAsync(submission with { Status = status, CompletedAtUtc = DateTimeOffset.UtcNow,
                Error = result.Error?.Code.ToString() }, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ClockSubmissionUnknown(submission.OperationId, "gpuClockSubmissionAuditIncomplete:" + error.Message); }
        return result;
    }

    private static CommandResult ClockSubmissionUnknown(Guid operationId, string reason) =>
        new(operationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
            ServiceError.Create(ErrorCode.HardwareWriteFailed, operationId, false) with
            { Details = new() { ["reason"] = reason, ["submissionStatus"] = "Unknown" } }, false)
        { Mutation = HardwareMutation.Unknown };
}
