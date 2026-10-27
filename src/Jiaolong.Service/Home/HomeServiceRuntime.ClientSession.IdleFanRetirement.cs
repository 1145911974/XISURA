using System.Security.Cryptography;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Ipc;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private sealed record RestartSource(byte[] Bytes, string Sha256);
    private sealed record IdleFanRestartAcknowledgement(int SchemaVersion, string Decision,
        DateTimeOffset AcknowledgedAtUtc, DateTimeOffset RestartBoot, RestartSource Capture, RestartSource Journal, RestartSource ExitPlan);

    private static bool ValidIdleFanRestartPlan(ExitPlan plan)
    {
        try
        {
            if (plan is not { SchemaVersion: 1, KeepChanges: true, LegacyEmptyProof: null, WindowsScopeSessionId: null,
                GpuRecoveryReady: true, RetainedWindows: null, RetainedSession: { Phase: ClientControlSessionPhase.Closing } session,
                RetainedJournal: { } saved, IdleFanRestart: { SchemaVersion: 1, Decision: "KeepCurrentAfterUserRestart" } proof } ||
                plan.OperationId == Guid.Empty || plan.SessionId != session.Handle.SessionId ||
                plan.Steps is not [{ Key: "retainedChanges", Status: "Verified", Expected: null, Target: null }] ||
                proof.AcknowledgedAtUtc > DateTimeOffset.UtcNow.AddSeconds(1) || proof.AcknowledgedAtUtc < proof.RestartBoot ||
                session.BootStartedAtUtc is not { } oldBoot || proof.RestartBoot <= oldBoot || proof.RestartBoot <= session.Handle.CapturedAtUtc)
                return false;
            bool ValidSource(RestartSource source) => source is { Bytes.Length: > 0 and <= 2 * 1024 * 1024, Sha256.Length: 64 } &&
                source.Sha256 == Convert.ToHexString(SHA256.HashData(source.Bytes));
            if (!ValidSource(proof.Capture) || !ValidSource(proof.Journal) || !ValidSource(proof.ExitPlan)) return false;
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var originalSession = JsonSerializer.Deserialize<ClientControlSessionSnapshot>(proof.Capture.Bytes, json);
            var originalJournal = JsonSerializer.Deserialize<ControlJournalSnapshot>(proof.Journal.Bytes, json);
            var originalPlan = JsonSerializer.Deserialize<ExitPlan>(proof.ExitPlan.Bytes, json);
            return originalSession is { Phase: ClientControlSessionPhase.Closing } &&
                ClientControlSessionStore.SameCapture(session, originalSession) && originalJournal is not null &&
                SamePriorJournal(saved, originalJournal) && ValidExitPlan(originalPlan) && !originalPlan!.KeepChanges &&
                originalPlan.SessionId == session.Handle.SessionId && originalPlan.WindowsScopeSessionId is null && originalPlan.GpuRecoveryReady &&
                PriorExitTargetsMatch(session, originalPlan) && originalPlan.Steps.Count(step => step.Key == "fan") == 1 &&
                originalPlan.Steps.All(step => step.Key == "fan"
                    ? step is { Status: "Failed", FanFirmwareOnly: false, FanMappingOriginalInitialization: null }
                    : step.Status == "Verified") &&
                IdleFanReleaseShape(session, saved, originalPlan.OperationId) && saved.Unresolved.Length == 2 &&
                saved.Unresolved.Single(item => item.OperationId == originalPlan.OperationId).State.HardwareEpoch == session.Handle.HardwareEpoch &&
                saved.Unresolved.Single(item => item.OperationId != originalPlan.OperationId).State.HardwareEpoch == session.Handle.HardwareEpoch + 1 &&
                saved.FanEcRecovery?.Scope.ClientSessionId != session.Handle.SessionId &&
                saved.FanFirmwareRecovery?.Scope.ClientSessionId != session.Handle.SessionId;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException or ArgumentException)
        { return false; }
    }

    // Maintenance-only: the caller authenticates the administrator and explicit decision; ordinary recovery never invokes this.
    internal Task<CommandResult> AcknowledgeIdleFanRestartAsync(Guid originalSessionId, CancellationToken token) =>
        Execution.WithHardwareGateAsync(async () =>
        {
            Guid operation = Guid.NewGuid();
            CommandResult Refuse(string reason) => SessionRefused(operation, ErrorCode.RollbackFailed, true) with
            { Mutation = HardwareMutation.NoMutation, Error = ServiceError.Create(ErrorCode.RollbackFailed, operation, true) with
                { Details = new() { ["clientSessionRecovery"] = reason } } };
            try
            {
                if (journal is null || originalSessionId == Guid.Empty || !LegacyRuntimeEmpty() || IsSuspended)
                    return Refuse("IdleRestartRuntimeNotEmpty");
                clientSessions ??= new(journal.RootDirectory);
                var session = await clientSessions.ReadAsync(token);
                if (session is null || session.Handle.SessionId != originalSessionId || session.ServiceSessionId == SessionId ||
                    session.Phase is not (ClientControlSessionPhase.Closing or ClientControlSessionPhase.Completed))
                    return Refuse("IdleRestartCaptureMismatch");
                var planStore = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
                var captureStore = new AtomicJsonStore(journal.RootDirectory, "client-control-session.json");
                var journalStore = new AtomicJsonStore(journal.RootDirectory, "control-execution.json");
                var planBytes = await ReadCompletedArchiveAsync(planStore, token);
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var prior = JsonSerializer.Deserialize<ExitPlan>(planBytes, json);
                var saved = await journal.ReadAsync(token);
                ExitPlan plan;
                if (prior?.IdleFanRestart is not null)
                {
                    if (!ValidIdleFanRestartPlan(prior) || !ClientControlSessionStore.SameCapture(session, prior.RetainedSession!))
                        return Refuse("IdleRestartDecisionInvalid");
                    plan = prior;
                    if (!SamePriorJournal(saved, plan.RetainedJournal!) && !SamePriorJournal(saved, ReleasedRetainedJournal(plan)))
                        return Refuse("IdleRestartJournalChanged");
                }
                else
                {
                    if (session.Phase != ClientControlSessionPhase.Closing || prior is null) return Refuse("IdleRestartPlanMissing");
                    var boot = readSystemBootTime is null ? null : await readSystemBootTime(token);
                    if (boot is null || session.BootStartedAtUtc is not { } oldBoot || boot <= oldBoot || boot <= session.Handle.CapturedAtUtc)
                        return Refuse("IdleRestartBootNotChanged");
                    var captureBytes = await ReadCompletedArchiveAsync(captureStore, token);
                    var journalBytes = await ReadCompletedArchiveAsync(journalStore, token);
                    RestartSource Source(byte[] bytes) => new(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
                    plan = new(1, originalSessionId, operation, [new("retainedChanges", null, null) { Status = "Verified" }])
                    {
                        KeepChanges = true, RetainedSession = session, RetainedJournal = saved,
                        IdleFanRestart = new(1, "KeepCurrentAfterUserRestart", DateTimeOffset.UtcNow, boot.Value,
                            Source(captureBytes), Source(journalBytes), Source(planBytes))
                    };
                    if (!ValidIdleFanRestartPlan(plan)) return Refuse("IdleRestartOriginalEvidenceInvalid");
                    await journal.VerifyIdleFanReleaseHistoryAsync(session, saved, token);
                    await VerifyLiveAsync();
                    if (!(await ReadCompletedArchiveAsync(captureStore, token)).AsSpan().SequenceEqual(captureBytes) ||
                        !(await ReadCompletedArchiveAsync(journalStore, token)).AsSpan().SequenceEqual(journalBytes) ||
                        !(await ReadCompletedArchiveAsync(planStore, token)).AsSpan().SequenceEqual(planBytes) ||
                        readSystemBootTime is null || await readSystemBootTime(token) != boot)
                        return Refuse("IdleRestartOriginalRecordsChanged");
                    if (JsonSerializer.SerializeToUtf8Bytes(plan, json).Length > 2 * 1024 * 1024)
                        return Refuse("IdleRestartDecisionTooLarge");
                    await planStore.WriteAsync(plan, token);
                }
                if (session.Phase != ClientControlSessionPhase.Completed)
                {
                    await VerifyLiveAsync();
                    var completed = await CompleteRetainedExitAsync(operation, session, plan, token);
                    if (completed.State != CommandState.Applied) return completed;
                    session = (await clientSessions.ReadAsync(token))!;
                }
                await VerifyRetainedExitEvidenceAsync(session, plan, token);
                await ArchiveCompletedClientSessionAsync(session, token);
                return SessionCaptured(operation, session.Handle, false) with { Mutation = HardwareMutation.NoMutation };

                async Task VerifyLiveAsync()
                {
                    for (int observation = 0; observation < 2; observation++)
                    {
                        if (IsSuspended || sessionProcessProbe.Observe(session.ProcessId, session.ProcessCreationFileTimeUtc) != ClientProcessState.Exited)
                            throw new InvalidDataException("idleRestartOriginalProcessNotExited");
                        var hardware = await provider.DiagnoseAsync(token);
                        if (hardware.SupportState != DeviceSupportState.Ready || hardware.Identity != session.Identity ||
                            await provider.ReadDeviceRecoveryKeyAsync(hardware.Identity, token) != session.Handle.DeviceKey ||
                            !IdleFanReleaseLive(await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true)))
                            throw new InvalidDataException("idleRestartLiveEvidenceUnverified");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return Refuse("IdleRestartCancelled"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { return Refuse(error is InvalidDataException ? error.Message : "IdleRestartOriginalRecordsInvalid"); }
        }, token);
}
