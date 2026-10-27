using System.Security.Cryptography;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    // Historical empty journals never grant write authority. Keep their bytes in the completion proof for later archives.
    private sealed record LegacyEmptyClosureProof(int SchemaVersion, Guid PreviousSessionId, string CaptureSha256,
        string JournalSha256, byte[] JournalBytes);

    private static string LegacyCaptureHash(ClientControlSessionSnapshot session) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(session with { Phase = ClientControlSessionPhase.Active })));

    private static bool EmptyLegacyExecution(ControlExecutionState? state) => state is { Fields.Count: 0, Pages.Length: 0,
        NativeModeOwnerClientSessionId: null, WindowsScopeSessionId: null, CpuWriteBatch: null, WindowsWriteProofHash: null, PendingStep: null };

    private bool LegacyRuntimeEmpty() => ownedFields.Count == 0 && lastSubmittedModeClientSessionId is null &&
        windowsScopeSessionId is null && fanOwnerClientSessionId is null && fanOwner is null &&
        (controlExecutionState is null || EmptyLegacyExecution(controlExecutionState));

    private static ExitStep[] LegacyEmptyTargets(ClientControlSessionSnapshot session)
    {
        var curve = CaptureCpuRestoration("negativeCurveOptimizer", session.Baseline.CpuTuning);
        if (curve?.PerCoreCurveOptimizer is not { Count: > 0 } cores || session.Baseline.KeyboardLighting is not { } light ||
            session.Baseline.GpuVf is not { Error: null, Nodes.Length: 127, CoreOffsetKhz: not null, MemoryOffsetKhz: not null })
            throw new InvalidDataException("legacyEmptyTargetsUnreadable");
        if (session.ExitDefaults?.CurveOptimizerAll is int value)
            curve = new(null, null, cores.Keys.ToDictionary(key => key, _ => value));
        return
        [
            new("performance.negativeCurveOptimizer", null, null, curve, curve) { Status = "Verified" },
            new("gpu.coreOffsetKhz", SettingRequest.Set(0), SettingRequest.Set(0)) { Status = "Verified" },
            new("gpu.memoryOffsetKhz", SettingRequest.Set(0), SettingRequest.Set(0)) { Status = "Verified" },
            new("gpu.vfOffsetsKhz", SettingRequest.Set(new int[127]), SettingRequest.Set(new int[127])) { Status = "Verified" },
            new("lighting", SettingRequest.Set(light), SettingRequest.Set(light)) { Status = "Verified" }
        ];
    }

    private async Task<CompletedClientSession> ReadLegacySourceArchiveAsync(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) throw new InvalidDataException("legacyEmptySourceMissing");
        var archive = new AtomicJsonStore(Path.Combine(journal!.RootDirectory, "completed-client-sessions"), id.ToString("N") + ".json");
        var bytes = await ReadCompletedArchiveAsync(archive, token);
        var record = JsonSerializer.Deserialize<CompletedClientSession>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (record?.Session is not { SchemaVersion: 1, Phase: ClientControlSessionPhase.Completed, Handle: not null } prior ||
            prior.Handle.SessionId != id || !ValidExitPlan(record.Recovery) || record.Recovery.SessionId != id ||
            !record.Recovery.GpuRecoveryReady || record.Recovery.Steps.Any(step => step.Status != "Verified"))
            throw new InvalidDataException("legacyEmptySourceInvalid");
        return record;
    }

    private async Task VerifyLegacyEmptyPlanAsync(ClientControlSessionSnapshot session, ExitPlan plan, CancellationToken token)
    {
        if (plan.LegacyEmptyProof is not { SchemaVersion: 1, JournalBytes.Length: > 0 and <= 524288 } proof ||
            !ValidExitPlan(plan, allowLegacyEmpty: true) || plan.SessionId != session.Handle.SessionId ||
            plan.WindowsScopeSessionId is not null || !plan.GpuRecoveryReady || proof.CaptureSha256 != LegacyCaptureHash(session) ||
            proof.JournalSha256 != Convert.ToHexString(SHA256.HashData(proof.JournalBytes)) ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(plan.Steps), JsonSerializer.SerializeToElement(LegacyEmptyTargets(session))))
            throw new InvalidDataException("legacyEmptyProofInvalid");
        var prior = await ReadLegacySourceArchiveAsync(proof.PreviousSessionId, token);
        await VerifyArchivedPriorExitPlanAsync(session, prior.Recovery, token);
        if (session.PreviousCompletedArchiveSha256 is null || prior.Session.Handle.SessionId == session.Handle.SessionId)
            throw new InvalidDataException("legacyEmptySourceUnbound");
        var saved = JsonSerializer.Deserialize<ControlJournalSnapshot>(proof.JournalBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (saved is not { SchemaVersion: 1, Unresolved.Length: 0, RecoveryScope: { Fields.Count: 0, NativeModeTarget: null,
                NativeModeEpoch: 0, FanOwnerClientSessionId: null } scope } || !EmptyLegacyExecution(saved.Current) ||
            saved.Identity != prior.Session.Identity || saved.CurrentOwnerSid != prior.Session.OwnerSid ||
            scope.ServiceSessionId != prior.Session.ServiceSessionId || saved.Current!.HardwareEpoch != prior.Session.Handle.HardwareEpoch ||
            saved.Current.NativeModeApplyState != ApplyState.Confirmed || saved.LastConfirmedMode != saved.Current.NativeMode)
            throw new InvalidDataException("legacyEmptySourceScopeInvalid");
        if (saved.FanEcRecovery is { } fan)
        {
            var source = await ReadLegacySourceArchiveAsync(fan.Scope.ClientSessionId, token);
            if (!ControlExecutionJournal.FanCheckpointComplete(fan.Checkpoint) || !LegacyFanScopeMatches(source.Session, fan.Scope) ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source.FanEcRecovery), JsonSerializer.SerializeToElement(fan)))
                throw new InvalidDataException("legacyEmptyFanHistoryInvalid");
        }
        if (saved.FanFirmwareRecovery is { } firmware)
        {
            var source = await ReadLegacySourceArchiveAsync(firmware.Scope.ClientSessionId, token);
            if (!ControlExecutionJournal.FanFirmwareComplete(firmware) || !LegacyFanScopeMatches(source.Session, firmware.Scope) ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source.FanFirmwareRecovery), JsonSerializer.SerializeToElement(firmware)))
                throw new InvalidDataException("legacyEmptyFirmwareHistoryInvalid");
        }
    }

    private static bool LegacyFanScopeMatches(ClientControlSessionSnapshot session, FanEcRecoveryScope scope) =>
        scope == new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch, session.Handle.SessionId, session.OwnerSid, session.Identity);

    private async Task VerifyCompletedLegacyEmptyPlanAsync(ClientControlSessionSnapshot session, CancellationToken token)
    {
        if (journal is null) return;
        var store = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
        if (!File.Exists(store.FilePath) || new FileInfo(store.FilePath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("completedSessionRecoveryMissing");
        var plan = await store.ReadAsync<ExitPlan>(token);
        if (plan?.KeepChanges == true) await VerifyRetainedExitEvidenceAsync(session, plan, token);
        if (plan?.LegacyEmptyProof is not null)
        {
            await VerifyLegacyEmptyPlanAsync(session, plan, token);
            await VerifyLegacyEmptyJournalAsync(plan, token);
        }
    }

    private async Task VerifyLegacyEmptyJournalAsync(ExitPlan plan, CancellationToken token)
    {
        var bytes = await ReadCompletedArchiveAsync(new AtomicJsonStore(journal!.RootDirectory, "control-execution.json"), token);
        if (!bytes.AsSpan().SequenceEqual(plan.LegacyEmptyProof!.JournalBytes))
            throw new InvalidDataException("legacyEmptyOriginalJournalChanged");
    }

    private async Task<CommandResult> CompleteLegacyEmptySessionAsync(Guid operationId, ClientControlSessionSnapshot session,
        ControlJournalSnapshot saved, IClientProcessProbe probe, PriorClientClose? explicitClose, CancellationToken token)
    {
        if (!LegacyRuntimeEmpty() || session.Phase is not (ClientControlSessionPhase.Active or ClientControlSessionPhase.Closing))
            throw new InvalidDataException("legacyEmptyRuntimeNotEmpty");
        long epoch = Execution.HardwareEpoch, lifecycle = powerLifecycle.Version;
        var store = new AtomicJsonStore(journal!.RootDirectory, "client-session-recovery.json");
        if (!File.Exists(store.FilePath) || new FileInfo(store.FilePath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("legacyEmptyPlanMissing");
        var previous = await store.ReadAsync<ExitPlan>(token) ?? throw new InvalidDataException("legacyEmptyPlanMissing");
        var journalStore = new AtomicJsonStore(journal.RootDirectory, "control-execution.json");
        var bytes = await ReadCompletedArchiveAsync(journalStore, token);
        ExitPlan plan;
        if (previous.LegacyEmptyProof is not null) plan = previous;
        else
        {
            if (session.Phase != ClientControlSessionPhase.Active || previous.SessionId == session.Handle.SessionId)
                throw new InvalidDataException("legacyEmptyOriginalPlanInvalid");
            plan = new(1, session.Handle.SessionId, operationId, LegacyEmptyTargets(session))
            {
                LegacyEmptyProof = new(1, previous.SessionId, LegacyCaptureHash(session), Convert.ToHexString(SHA256.HashData(bytes)), bytes)
            };
            await VerifyArchivedPriorExitPlanAsync(session, previous, token);
        }
        await VerifyLegacyEmptyPlanAsync(session, plan, token);
        var source = JsonSerializer.Deserialize<ControlJournalSnapshot>(plan.LegacyEmptyProof!.JournalBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (!SamePriorJournal(saved, source)) throw new InvalidDataException("legacyEmptyOriginalJournalChanged");
        await VerifyLiveAsync();
        await VerifyAuthorityAsync(session, previous);
        if (previous.LegacyEmptyProof is null) await store.WriteAsync(plan, token);
        clientSessions ??= new(journal.RootDirectory);
        var closing = await clientSessions.WriteAsync(session with { Phase = ClientControlSessionPhase.Closing }, token, requireExisting: true);
        Interlocked.Exchange(ref clientSessionClosing, 1);
        Volatile.Write(ref activeClientSession, closing);
        await VerifyLiveAsync();
        await VerifyAuthorityAsync(closing, plan);
        var completed = await clientSessions.WriteAsync(closing with { Phase = ClientControlSessionPhase.Completed }, token, requireExisting: true);
        Volatile.Write(ref activeClientSession, completed);
        return SessionCaptured(operationId, completed.Handle, false);

        async Task VerifyLiveAsync()
        {
            string? issue = await VerifyPriorLiveStateAsync(session, source, null, token, ignoreArchivedPlan: true, satisfiedPlan: plan);
            if (issue is not null) throw new InvalidDataException(issue);
        }

        async Task VerifyAuthorityAsync(ClientControlSessionSnapshot expected, ExitPlan expectedPlan)
        {
            token.ThrowIfCancellationRequested();
            if (IsSuspended || epoch != Execution.HardwareEpoch || lifecycle != powerLifecycle.Version || !LegacyRuntimeEmpty() ||
                !PriorProcessMatches(session, probe.Observe(session.ProcessId, session.ProcessCreationFileTimeUtc), explicitClose) ||
                readSystemBootTime is null || await readSystemBootTime(token) != session.BootStartedAtUtc ||
                readSystemPowerContinuity is null || await readSystemPowerContinuity(token) != session.PowerContinuity)
                throw new InvalidDataException("legacyEmptyContinuityChanged");
            var hardware = await provider.DiagnoseAsync(token);
            if (hardware.SupportState != DeviceSupportState.Ready || hardware.Identity != session.Identity ||
                await provider.ReadDeviceRecoveryKeyAsync(hardware.Identity, token) != session.Handle.DeviceKey)
                throw new InvalidDataException("legacyEmptyDeviceChanged");
            var current = await new ClientControlSessionStore(journal.RootDirectory).ReadAsync(token);
            var currentJournalBytes = await ReadCompletedArchiveAsync(journalStore, token);
            if (current is null || current.Phase != expected.Phase || !ClientControlSessionStore.SameCapture(session, current) ||
                !plan.LegacyEmptyProof.JournalBytes.AsSpan().SequenceEqual(currentJournalBytes) ||
                !SamePriorJournal(saved, await journal.ReadAsync(token)) ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expectedPlan), JsonSerializer.SerializeToElement(await store.ReadAsync<ExitPlan>(token))))
                throw new InvalidDataException("legacyEmptyOriginalRecordsChanged");
            await VerifyLegacyEmptyPlanAsync(session, plan, token);
            if (IsSuspended || epoch != Execution.HardwareEpoch || lifecycle != powerLifecycle.Version ||
                !PriorProcessMatches(session, probe.Observe(session.ProcessId, session.ProcessCreationFileTimeUtc), explicitClose))
                throw new InvalidDataException("legacyEmptyContinuityChanged");
        }
    }
}
