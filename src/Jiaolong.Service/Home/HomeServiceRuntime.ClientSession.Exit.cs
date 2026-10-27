using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Ipc;
using Jiaolong.Service.Storage;
using System.Text.Json;
using System.Security.Cryptography;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private sealed record ExitStep(string Key, SettingRequest? Expected, SettingRequest? Target,
        CpuFieldRestorationValue? ExpectedDetails = null, CpuFieldRestorationValue? TargetDetails = null)
    {
        public string Status { get; set; } = "Pending";
        public SettingRequest? IntermediateVf { get; init; }
        public bool? FanStrongCoolingTarget { get; init; }
        public bool FanFirmwareOnly { get; init; }
        public byte? FanMappingOriginalInitialization { get; init; }
        public bool? FanMappingTargetWritesStarted { get; init; }
        public DateTimeOffset? FanMappingObservedAfterUtc { get; init; }
    }
    private sealed record ExitPlan(int SchemaVersion, Guid SessionId, Guid OperationId, ExitStep[] Steps)
    {
        public Guid? WindowsScopeSessionId { get; init; }
        public bool GpuRecoveryReady { get; set; } = true;
        public LegacyEmptyClosureProof? LegacyEmptyProof { get; init; }
        public bool KeepChanges { get; init; }
        public ClientControlSessionSnapshot? RetainedSession { get; init; }
        public ControlJournalSnapshot? RetainedJournal { get; init; }
        public WindowsScopeSession? RetainedWindows { get; init; }
        public IdleFanRestartAcknowledgement? IdleFanRestart { get; init; }
    }
    private sealed record CompletedClientSession(ClientControlSessionSnapshot Session, ExitPlan Recovery)
    {
        public FanEcRecoverySnapshot? FanEcRecovery { get; init; }
        public FanFirmwareRecoverySnapshot? FanFirmwareRecovery { get; init; }
    }

    private static bool ValidExitPlan(ExitPlan? plan, bool allowLegacyEmpty = false) => plan is { SchemaVersion: 1, Steps.Length: > 0 and <= 64 } &&
        (plan.KeepChanges ? ValidRetainedExitPlan(plan) :
            plan.RetainedSession is null && plan.RetainedJournal is null && plan.RetainedWindows is null && plan.IdleFanRestart is null) &&
        (allowLegacyEmpty || plan.LegacyEmptyProof is null) &&
        plan.SessionId != Guid.Empty && plan.OperationId != Guid.Empty &&
        (plan.WindowsScopeSessionId is null || plan.WindowsScopeSessionId == plan.SessionId) && plan.Steps.All(step => step is not null) &&
        plan.Steps.Select(step => step.Key).Distinct().Count() == plan.Steps.Length &&
        plan.Steps.All(step => (!step.FanFirmwareOnly || step.Key == "fan") &&
            (step.FanMappingOriginalInitialization is null
                ? step.FanMappingTargetWritesStarted is null && step.FanMappingObservedAfterUtc is null
                : step.Key == "fan" && !step.FanFirmwareOnly && (step.FanMappingOriginalInitialization.Value & 0x80) == 0 &&
                    step.FanMappingTargetWritesStarted is not null && step.FanMappingObservedAfterUtc is { } observedAfter && observedAfter != default));

    private static FanEcMappingLease? SessionFanMapping(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        if (saved.FanEcRecovery is not { Checkpoint.MappingLease: { } mapping } record ||
            record.Scope.ClientSessionId != session.Handle.SessionId) return null;
        if (record.Scope != new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch,
            session.Handle.SessionId, session.OwnerSid, session.Identity))
            throw new InvalidDataException("exitFanMappingScopeChanged");
        return mapping;
    }

    private async Task VerifyArchivedPriorExitPlanAsync(ClientControlSessionSnapshot session, ExitPlan plan, CancellationToken token)
    {
        var archive = new AtomicJsonStore(Path.Combine(journal!.RootDirectory, "completed-client-sessions"), plan.SessionId.ToString("N") + ".json");
        var bytes = await ReadCompletedArchiveAsync(archive, token);
        if (session.PreviousCompletedArchiveSha256 is { } hash &&
            !string.Equals(hash, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("previousExitRecoveryArchiveChanged");
        var completed = JsonSerializer.Deserialize<CompletedClientSession>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (completed is not { Session: { Phase: ClientControlSessionPhase.Completed, Handle: not null }, Recovery: not null } ||
            completed.Session.Handle.SessionId != plan.SessionId ||
            session.PreviousCompletedArchiveSha256 is null && (completed.Session.Handle.DeviceKey != session.Handle.DeviceKey ||
                completed.Session.Identity != session.Identity) || !ValidExitPlan(plan, allowLegacyEmpty: true) || !plan.GpuRecoveryReady ||
            plan.Steps.Any(step => step.Status != "Verified") ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(completed.Recovery), JsonSerializer.SerializeToElement(plan)))
            throw new InvalidDataException("previousExitRecoveryArchiveMismatch");
        if (plan.LegacyEmptyProof is not null) await VerifyLegacyEmptyPlanAsync(completed.Session, plan, token);
        if (plan.KeepChanges) VerifyRetainedSession(completed.Session, plan);
    }

    private static async Task<byte[]> ReadCompletedArchiveAsync(AtomicJsonStore archive, CancellationToken token)
    {
        MachinePaths.EnsureSafeDirectory(Path.GetDirectoryName(archive.FilePath)!);
        MachinePaths.EnsureSafeFile(archive.FilePath);
        await using var stream = new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("previousExitRecoveryArchiveTooLarge");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private async Task<string> ArchiveCompletedClientSessionAsync(ClientControlSessionSnapshot session, CancellationToken token)
    {
        if (journal is null) throw new InvalidDataException("completedSessionJournalMissing");
        var saved = await journal.ReadAsync(token);
        if (saved.Unresolved.Length != 0 || saved.Current?.Fields.Count > 0 || saved.Current?.NativeModeOwnerClientSessionId is not null ||
            saved.Current?.WindowsScopeSessionId is not null)
            throw new InvalidDataException("completedSessionJournalNotResolved");
        var recoveryStore = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
        if (!File.Exists(recoveryStore.FilePath) || new FileInfo(recoveryStore.FilePath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("completedSessionRecoveryMissing");
        var plan = await recoveryStore.ReadAsync<ExitPlan>(token);
        if (!ValidExitPlan(plan, allowLegacyEmpty: true) || plan!.SessionId != session.Handle.SessionId || !plan.GpuRecoveryReady || plan.Steps.Any(step => step.Status != "Verified"))
            throw new InvalidDataException("completedSessionRecoveryNotVerified");
        if (plan.LegacyEmptyProof is not null)
        {
            await VerifyLegacyEmptyPlanAsync(session, plan, token);
            await VerifyLegacyEmptyJournalAsync(plan, token);
        }
        if (plan.KeepChanges) await VerifyRetainedExitEvidenceAsync(session, plan, token);
        if (!plan.KeepChanges && plan.WindowsScopeSessionId is not null && (await ReadWindowsScopesAsync(session, true, token))!.Schemes.Values
            .Any(scheme => scheme.Writes.Values.Any(field => !field.Restored)))
            throw new InvalidDataException("completedWindowsScopeNotVerified");
        var archive = new AtomicJsonStore(Path.Combine(journal.RootDirectory, "completed-client-sessions"), session.Handle.SessionId.ToString("N") + ".json");
        var fan = saved.FanEcRecovery?.Scope.ClientSessionId == session.Handle.SessionId ? saved.FanEcRecovery : null;
        if (!plan.KeepChanges && fan is not null && (!ControlExecutionJournal.FanCheckpointComplete(fan.Checkpoint) ||
            fan.Scope != new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch, session.Handle.SessionId, session.OwnerSid, session.Identity)))
            throw new InvalidDataException("completedFanCheckpointNotVerified");
        var firmware = saved.FanFirmwareRecovery?.Scope.ClientSessionId == session.Handle.SessionId ? saved.FanFirmwareRecovery : null;
        if (!plan.KeepChanges && firmware is not null && (!ControlExecutionJournal.FanFirmwareComplete(firmware) ||
            firmware.Scope != new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch, session.Handle.SessionId, session.OwnerSid, session.Identity)))
            throw new InvalidDataException("completedFanFirmwareNotVerified");
        var record = new CompletedClientSession(session, plan) { FanEcRecovery = fan, FanFirmwareRecovery = firmware };
        if (!File.Exists(archive.FilePath)) await archive.WriteAsync(record, token);
        var bytes = await ReadCompletedArchiveAsync(archive, token);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(
                JsonSerializer.Deserialize<CompletedClientSession>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
                JsonSerializer.SerializeToElement(record)))
            throw new InvalidDataException("completedSessionArchiveChanged");
        // Bind the next capture to the exact archive verified above, including its old identity.
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public async Task<CommandResult> EndClientControlSessionAsync(Guid operationId, Guid clientSessionId, ClientIdentity client,
        CancellationToken token, bool keepChanges = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await EndClientControlSessionCoreAsync(operationId, clientSessionId, client, deadline.Token, keepChanges); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return SessionRefused(operationId, ErrorCode.RollbackFailed, true) with
            { Error = ServiceError.Create(ErrorCode.RollbackFailed, operationId, true) with
                { Details = new() { ["clientSessionExit"] = token.IsCancellationRequested ? "ExitCancelled" : "ExitDeadlineExceeded" } } };
        }
    }

    private async Task<CommandResult> EndClientControlSessionCoreAsync(Guid operationId, Guid clientSessionId, ClientIdentity client,
        CancellationToken token, bool keepChanges)
    {
        var session = Volatile.Read(ref activeClientSession);
        if (operationId == Guid.Empty) return SessionRefused(operationId, ErrorCode.ValidationFailed);
        if (session is null || !SessionEpochMatches(session))
            return await Execution.WithHardwareGateAsync(async () =>
                await InspectPriorClientSessionAsync(sessionProcessProbe, token, restore: true,
                    explicitClose: new(operationId, clientSessionId, client) { KeepChanges = keepChanges })
                ?? SessionRefused(operationId, ErrorCode.UnauthorizedClient), token);
        if (session is null || session.Handle.SessionId != clientSessionId || !MatchesSessionProcess(session, client))
            return SessionRefused(operationId, ErrorCode.UnauthorizedClient);
        if (session.Phase == ClientControlSessionPhase.Completed)
        {
            try
            {
                var completed = await clientSessions!.ReadAsync(token);
                return completed?.Phase == ClientControlSessionPhase.Completed && ClientControlSessionStore.SameCapture(session, completed)
                    ? SessionCaptured(operationId, session.Handle, true) : SessionRefused(operationId, ErrorCode.RollbackFailed, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { return SessionRefused(operationId, ErrorCode.RollbackFailed, true); }
        }
        var frozen = await FreezeClientSessionWritesAsync(operationId, clientSessionId, client, token, keepChanges);
        if (frozen.State != CommandState.Applied) return frozen;
        return await Execution.WithHardwareGateAsync(async () =>
        {
            session = Volatile.Read(ref activeClientSession)!;
            if (!SessionEpochMatches(session)) return SessionRefused(operationId, ErrorCode.RollbackFailed, true);
            hardwareDispatched.Value = new(powerLifecycle.Version);
            try
            {
                if (journal is null) throw new InvalidDataException("exitJournalMissing");
                var disk = await clientSessions!.ReadAsync(token);
                if (disk is null || !ClientControlSessionStore.SameCapture(session, disk) || disk.Phase != session.Phase)
                    throw new InvalidDataException("exitSessionChanged");
                var store = new AtomicJsonStore(journal.RootDirectory, "client-session-recovery.json");
                if (File.Exists(store.FilePath) && new FileInfo(store.FilePath).Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("exitRecoveryTooLarge");
                var plan = File.Exists(store.FilePath) ? await store.ReadAsync<ExitPlan>(token) : null;
                if (plan is not null && plan.SessionId != clientSessionId)
                {
                    await VerifyArchivedPriorExitPlanAsync(session, plan, token);
                    plan = null;
                }
                if (plan is not null && !ValidExitPlan(plan))
                    throw new InvalidDataException("exitRecoveryInvalid");
                var saved = await journal.ReadAsync(token);
                if (keepChanges || plan?.KeepChanges == true)
                {
                    if (plan is not null && !plan.KeepChanges)
                        throw new InvalidDataException("exitRecoveryAlreadyStarted");
                    if (plan is null) throw new InvalidDataException("exitRetentionDecisionMissing");
                    return await CompleteRetainedExitAsync(operationId, session, plan, token);
                }
                if (saved.Current is not null || saved.Unresolved.Length > 0)
                {
                    var fresh = await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token);
                    if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved), JsonSerializer.SerializeToElement(fresh)))
                        throw new InvalidDataException("exitJournalChangedOrMissing");
                }
                if (saved.Unresolved.Any(item => item.OperationId != plan?.OperationId))
                {
                    if (plan is not null || !(PriorPendingLighting(session, saved) is not null &&
                        await ReconcilePriorPendingLightingAsync(session, saved, token)) && !(PriorPendingFan(session, saved) is not null &&
                        await RestorePriorPendingFanAsync(session, saved, token)) && !(PriorPendingCpuFields(session, saved).Length > 0 &&
                        await RestorePriorPendingCpuAsync(session, saved, await ReadWindowsScopesAsync(session,
                            saved.Current?.WindowsScopeSessionId is not null, token), token)) &&
                        !await RecoverPriorManualGpuAsync(session, saved, token) &&
                        !await ReconcilePriorNativeModeAsync(session, saved, token))
                        throw new InvalidDataException("exitUnresolvedPriorOperation");
                    saved = await journal.ReadAsync(token);
                    if (saved.Unresolved.Length != 0) throw new InvalidDataException("exitPriorGpuRecoveryNotResolved");
                }
                if (saved.Current?.WindowsScopeSessionId is { } savedScope && savedScope != clientSessionId)
                    throw new InvalidDataException("exitWindowsScopeSessionMismatch");
                var fanMapping = SessionFanMapping(session, saved);
                await journal.VerifyFanFirmwareOnlyHistoryAsync(session, saved, token);
                var windows = await ReadWindowsScopesAsync(session, saved.Current?.WindowsScopeSessionId is not null ||
                    windowsScopeSessionId == clientSessionId || plan?.WindowsScopeSessionId is not null, token);
                if (plan is not null && (plan.WindowsScopeSessionId is not null) != (windows is not null))
                    throw new InvalidDataException("exitWindowsScopePlanMismatch");
                if (plan is null)
                {
                    plan = BuildExitPlan(session, operationId, windows, ControlExecutionJournal.IsFanFirmwareOnlyRecovery(session, saved), fanMapping);
                    await store.WriteAsync(plan, token);
                }
                return await Execution.WithRecoveryAdmissionAsync(plan.OperationId, session.OwnerSid,
                    () => RunExitPlanAsync(operationId, session, plan, store, windows, token));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
            { return SessionRefused(operationId, ErrorCode.RollbackFailed, true) with
                { Mutation = hardwareDispatched.Value?.Started == true ? HardwareMutation.Unknown : HardwareMutation.NoMutation,
                  Error = ServiceError.Create(ErrorCode.RollbackFailed, operationId, true) with
                  { Details = new() { ["clientSessionExit"] = error is InvalidDataException ? error.Message : error.GetType().Name } } }; }
            finally { hardwareDispatched.Value = null; }
        }, token);
    }

    private ExitPlan BuildExitPlan(ClientControlSessionSnapshot session, Guid operationId, WindowsScopeSession? windows, bool fanFirmwareOnly,
        FanEcMappingLease? fanMapping)
    {
        var baseline = new HomeControlState(session.Baseline.PerformanceMode, session.Baseline.StrongCooling, session.Baseline.QuickSettings)
        { CpuTuning = session.Baseline.CpuTuning, GpuVf = session.Baseline.GpuVf,
          KeyboardLighting = session.Baseline.KeyboardLighting, ActiveFanControlPlan = session.Baseline.ActiveFanControlPlan };
        var steps = new List<ExitStep>();
        SettingRequest? Expected(ControlPageId page, string field) =>
            ownedFields.TryGetValue(FieldKey(page, field), out var owner) && owner.ClientSessionId == session.Handle.SessionId
                ? owner.LastWritten : ControlPlanCompiler.Read(page, baseline).GetValueOrDefault(field);
        if (lastSubmittedModeClientSessionId == session.Handle.SessionId)
            steps.Add(new("mode", SettingRequest.Set(lastSubmittedMode), SettingRequest.Set(session.Baseline.PerformanceMode)));
        foreach (var pair in ownedFields.Where(pair => pair.Value.ClientSessionId == session.Handle.SessionId && pair.Key.StartsWith("performance.")))
        {
            string field = pair.Key[12..];
            if (windows is not null && IsWindowsIndexField(field)) continue;
            steps.Add(new(pair.Key, pair.Value.LastWritten, ControlPlanCompiler.Read(ControlPageId.Performance, baseline).GetValueOrDefault(field),
                pair.Value.LastDetails, CaptureCpuRestoration(field, baseline.CpuTuning)));
        }
        if (lastSubmittedModeClientSessionId == session.Handle.SessionId)
        {
            foreach (var pair in ControlPlanCompiler.Read(ControlPageId.Performance, baseline))
            {
                string key = "performance." + pair.Key;
                if (windows is not null && IsWindowsIndexField(pair.Key)) continue;
                if (steps.Any(step => step.Key == key)) continue;
                var details = CaptureCpuRestoration(pair.Key, baseline.CpuTuning);
                steps.Add(new(key, pair.Value, pair.Value, details, details));
            }
        }
        const string curveKey = "performance.negativeCurveOptimizer";
        if (steps.All(step => step.Key != curveKey) && CaptureCpuRestoration("negativeCurveOptimizer", baseline.CpuTuning) is { } curve)
            steps.Add(new(curveKey, null, null, curve, curve));
        if (session.ExitDefaults?.CurveOptimizerAll is int co)
        {
            int index = steps.FindIndex(step => step.Key == curveKey);
            if (index < 0 || steps[index].TargetDetails?.PerCoreCurveOptimizer is not { } cores)
                throw new InvalidDataException("exitCurveBaselineUnreadable");
            steps[index] = steps[index] with { TargetDetails = new(null, null, cores.Keys.ToDictionary(key => key, _ => co)) };
        }
        foreach (string field in new[] { "coreOffsetKhz", "vfOffsetsKhz", "memoryOffsetKhz" })
        {
            var expected = Expected(ControlPageId.Gpu, field);
            if (expected is not null) steps.Add(new("gpu." + field, expected,
                field == "vfOffsetsKhz" ? SettingRequest.Set(new int[127]) : SettingRequest.Set(0)));
        }
        if (baseline.KeyboardLighting is { } light)
        {
            var expected = ControlPlanCompiler.Read(ControlPageId.Lighting, baseline);
            foreach (var pair in ownedFields.Where(pair => pair.Key.StartsWith("lighting.") && pair.Value.ClientSessionId == session.Handle.SessionId))
                if (pair.Value.LastWritten is { } value) expected[pair.Key[9..]] = value;
            var expectedLight = ControlPlanCompiler.Commands(ControlPageId.Lighting, expected, baseline).OfType<SetKeyboardLightingCommand>().Single().Plan;
            steps.Add(new("lighting", SettingRequest.Set(expectedLight), SettingRequest.Set(light)));
        }
        var fanExpected = ControlPlanCompiler.Read(ControlPageId.Fan, baseline);
        foreach (var pair in ownedFields.Where(pair => pair.Key.StartsWith("fan.") && pair.Value.ClientSessionId == session.Handle.SessionId))
            if (pair.Value.LastWritten is { } value) fanExpected[pair.Key[4..]] = value;
        if (fanOwnerClientSessionId == session.Handle.SessionId || ownedFields.Any(pair => pair.Key.StartsWith("fan.") &&
            pair.Value.ClientSessionId == session.Handle.SessionId))
            steps.Add(new("fan", SettingRequest.Set(fanExpected), SettingRequest.Set("Auto"))
                {
                    FanStrongCoolingTarget = session.Baseline.StrongCooling, FanFirmwareOnly = fanFirmwareOnly,
                    FanMappingOriginalInitialization = fanMapping?.Original,
                    FanMappingTargetWritesStarted = fanMapping?.TargetWritesStarted,
                    FanMappingObservedAfterUtc = fanMapping is null ? null : DateTimeOffset.UtcNow
                });
        return new(1, session.Handle.SessionId, operationId, steps.ToArray())
            { WindowsScopeSessionId = windows?.SessionId };
    }

    private async Task<CommandResult> RunExitPlanAsync(Guid requestId, ClientControlSessionSnapshot session,
        ExitPlan plan, AtomicJsonStore store, WindowsScopeSession? windows, CancellationToken token)
    {
        if (plan.LegacyEmptyProof is not null) throw new InvalidDataException("legacyEmptyPlanCannotDispatch");
        var fanMapping = SessionFanMapping(session, await journal!.ReadAsync(token));
        var fanStep = plan.Steps.FirstOrDefault(step => step.Key == "fan");
        if (fanStep?.FanMappingOriginalInitialization != fanMapping?.Original ||
            fanStep?.FanMappingTargetWritesStarted != fanMapping?.TargetWritesStarted)
            throw new InvalidDataException("exitFanMappingPlanChanged");
        bool completedPriorPlan = CheckedPriorRestore() is not null && plan.GpuRecoveryReady &&
            plan.Steps.All(step => step.Status == "Verified") &&
            (windows is null || windows.Schemes.Values.All(scheme => scheme.Writes.Values.All(field => field.Restored)));
        if (completedPriorPlan && !await provider.ReconcileGpuRecoveryAsync(token))
            return SessionRefused(requestId, ErrorCode.RollbackFailed, true);
        var controls = await ReadControlBaselineForExecutionAsync(token, true);
        controlExecutionState ??= new(Execution.Generation, Execution.HardwareEpoch, 0, default,
            controls.PerformanceMode ?? PerformanceMode.Balanced, [], SnapshotFields());
        await PersistExecutionAsync(plan.OperationId, true, token);
        if (!await ReconcileExitGpuAsync() && CheckedPriorRestore() is not null)
            return SessionRefused(requestId, ErrorCode.RollbackFailed, true) with
            { Mutation = hardwareDispatched.Value?.Started == true ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
        for (int stepIndex = 0; stepIndex < plan.Steps.Length; stepIndex++)
        {
            var step = plan.Steps[stepIndex];
            controls = await ReadControlBaselineForExecutionAsync(token, true);
            if (step.Key == "fan" && step.FanStrongCoolingTarget is null)
            { step.Status = "BaselineUnreadable"; await store.WriteAsync(plan, token); continue; }
            if (step.Key == "fan" && await VerifyIdleFanReleaseAsync(session, plan.OperationId, controls, token))
            {
                // WAL and the original release fields prove there is no EC mutation to restore.
                step.Status = "Verified";
                await store.WriteAsync(plan, token);
                continue;
            }
            if (step.Key == "fan" && CheckedPriorRestore() is not null && step.Status != "Verified")
            {
                step.Status = "Applying";
                await store.WriteAsync(plan, token);
                var scope = new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch,
                    session.Handle.SessionId, session.OwnerSid, session.Identity);
                using var admission = journal!.BindFanRecovery(new(plan.OperationId, scope));
                var restored = await TrackHardwareDispatchAsync(() => provider.RecoverSessionFanAsync(plan.OperationId, scope, token));
                controls = await ReadControlBaselineForExecutionAsync(token, true);
                step.Status = restored is { State: CommandState.Applied, Error: null } && ExitMatches(step, controls, true) ? "Verified" : "Failed";
                await store.WriteAsync(plan, token);
                continue;
            }
            if (step.Key == "gpu.vfOffsetsKhz" && plan.Steps.FirstOrDefault(item => item.Key == "gpu.coreOffsetKhz") is { } coreStep &&
                (coreStep.Status != "Verified" || !ExitMatches(coreStep, controls, true)))
            { step.Status = "Deferred"; await store.WriteAsync(plan, token); continue; }
            if (ExitMatches(step, controls, true))
            {
                if (step.Key == "gpu.coreOffsetKhz" && step.Status != "Verified" && step.IntermediateVf is { } intermediate)
                {
                    if (!ExitSame(SettingRequest.Set(controls.GpuVf!.Nodes.Select(node => node.OffsetKhz).ToArray()), intermediate))
                    { step.Status = "Conflict"; await store.WriteAsync(plan, token); continue; }
                    int vfIndex = Array.FindIndex(plan.Steps, item => item.Key == "gpu.vfOffsetsKhz");
                    if (vfIndex >= 0) plan.Steps[vfIndex] = plan.Steps[vfIndex] with { Expected = intermediate };
                }
                step.Status = "Verified"; await store.WriteAsync(plan, token); continue;
            }
            if (step.Status == "Verified" || !ExitMatches(step, controls, false) && !ExitCpuPartialMatches(step, controls))
            { step.Status = "Conflict"; await store.WriteAsync(plan, token); continue; }
            if (step.Key.StartsWith("performance.") && !IsDirectCpuScalar(step.Key[12..]) && step.Key is not "performance.negativeCurveOptimizer" and not "performance.pboScalar" &&
                !(windows is not null && step.Key == "performance.windowsPowerSchemeId") &&
                (controls.CpuTuning?.WindowsPowerSchemeId != session.Baseline.CpuTuning?.WindowsPowerSchemeId ||
                 ownedFields.TryGetValue(step.Key, out var scopedOwner) && scopedOwner.Scheme != session.Baseline.CpuTuning?.WindowsPowerSchemeId))
            { step.Status = "SchemeRecoveryRequired"; await store.WriteAsync(plan, token); continue; }
            if (step.Key == "gpu.coreOffsetKhz" && plan.Steps.FirstOrDefault(item => item.Key == "gpu.vfOffsetsKhz") is { } vf &&
                !ExitMatches(vf, controls, false))
            { step.Status = "Conflict"; await store.WriteAsync(plan, token); continue; }
            if (step.Key == "mode" && (plan.Steps.Where(item => item.Key.StartsWith("performance.") || item.Key == "lighting")
                .Any(item => !ExitMatches(item, controls, true) && !ExitMatches(item, controls, false)) ||
                !await WindowsScopesMatchAsync(windows, false, token)))
            { step.Status = "Conflict"; await store.WriteAsync(plan, token); continue; }
            if (step.Key.StartsWith("performance.") && (step.Target is null && step.TargetDetails is null ||
                step.Key is "performance.isBoostEnabled" or "performance.negativeCurveOptimizer" && step.TargetDetails is null))
            { step.Status = "BaselineUnreadable"; await store.WriteAsync(plan, token); continue; }
            HardwareCommand[] commands;
            bool directScalar = step.Key.StartsWith("performance.") && IsDirectCpuScalar(step.Key[12..]);
            if (directScalar && !CanRestoreCpuScalar(step.Key[12..]))
            { step.Status = "Unavailable"; await store.WriteAsync(plan, token); continue; }
            try { commands = directScalar ? [] : ExitCommands(step, controls); }
            catch (InvalidDataException) { step.Status = "Unsupported"; await store.WriteAsync(plan, token); continue; }
            if (commands.Any(command => !CanExecute(command, State) || !CanWriteTuningFields(command, State)))
            { step.Status = "Unavailable"; await store.WriteAsync(plan, token); continue; }
            step.Status = "Applying";
            if (step.Key == "gpu.coreOffsetKhz")
            {
                int delta = -controls.GpuVf!.CoreOffsetKhz!.Value;
                var prepared = step with { IntermediateVf = SettingRequest.Set(controls.GpuVf.Nodes.Select((node, index) =>
                    index == 0 ? node.OffsetKhz : checked(node.OffsetKhz + delta)).ToArray()) };
                step = prepared;
                plan.Steps[stepIndex] = step;
            }
            await store.WriteAsync(plan, token);
            CommandResult? result = null;
            if (directScalar)
                result = await TrackHardwareDispatchAsync(() => provider.RestoreCpuScalarAsync(Guid.NewGuid(), step.Key[12..], step.Target!,
                    ControlPlanCompiler.Read(ControlPageId.Performance, controls)[step.Key[12..]], token));
            else if (step.TargetDetails is { } details)
            {
                string field = step.Key[12..];
                var checkPlan = field == "negativeCurveOptimizer"
                    ? new CpuTuningPlan(null, null, null, null, null, null, null, null)
                        { Advanced = new(PerCoreCurveOptimizer: details.PerCoreCurveOptimizer) }
                    : ExitCpuPlan(field, step.Target, controls);
                var checkCommand = new SetCpuTuningCommand(Guid.NewGuid(), checkPlan, true);
                if (!CanExecute(checkCommand, State) || !CanWriteTuningFields(checkCommand, State))
                { step.Status = "Unavailable"; await store.WriteAsync(plan, token); continue; }
                result = await TrackHardwareDispatchAsync(() => provider.RestoreCpuFieldAsync(Guid.NewGuid(), field, details,
                    CaptureCpuRestoration(field, controls.CpuTuning)!, token));
            }
            else foreach (var command in commands)
            {
                result = await ExecuteProviderAsync(command, token);
                if (result.State != CommandState.Applied || result.Error is not null) break;
            }
            controls = await ReadControlBaselineForExecutionAsync(token, true);
            step.Status = result is { State: CommandState.Applied, Error: null } && ExitMatches(step, controls, true) ? "Verified" : "Failed";
            if (step.Status == "Verified" && step.Key == "gpu.coreOffsetKhz" && step.IntermediateVf is { } predicted &&
                !ExitSame(SettingRequest.Set(controls.GpuVf!.Nodes.Select(node => node.OffsetKhz).ToArray()), predicted))
                step.Status = "Failed";
            if (step.Status == "Verified" && step.Key == "gpu.coreOffsetKhz")
            {
                int index = Array.FindIndex(plan.Steps, item => item.Key == "gpu.vfOffsetsKhz");
                if (index >= 0) plan.Steps[index] = plan.Steps[index] with
                    { Expected = SettingRequest.Set(controls.GpuVf!.Nodes.Select(node => node.OffsetKhz).ToArray()) };
            }
            if (step.Status == "Verified" && step.Key == "mode")
            {
                for (int index = 0; index < plan.Steps.Length; index++)
                {
                    var next = plan.Steps[index];
                    if (next.Key.StartsWith("performance.") && !IsWindowsIndexField(next.Key[12..]) &&
                        next.Key != "performance.windowsPowerSchemeId") plan.Steps[index] = next with
                    { Expected = ControlPlanCompiler.Read(ControlPageId.Performance, controls).GetValueOrDefault(next.Key[12..]),
                      ExpectedDetails = next.TargetDetails is null ? null : CaptureCpuRestoration(next.Key[12..], controls.CpuTuning) };
                    else if (next.Key == "lighting") plan.Steps[index] = next with { Expected = SettingRequest.Set(controls.KeyboardLighting) };
                }
            }
            await store.WriteAsync(plan, token);
        }
        bool windowsComplete = await RestoreWindowsScopesAsync(session, windows, token);
        controls = await ReadControlBaselineForExecutionAsync(token, true);
        bool gpuReady = await ReconcileExitGpuAsync();
        bool fanIdleNoMutation = await VerifyIdleFanReleaseAsync(session, plan.OperationId, controls, token);
        bool complete = gpuReady && windowsComplete && plan.Steps.All(step => step.Status == "Verified" && ExitMatches(step, controls, true, fanIdleNoMutation));
        foreach (var key in ownedFields.Where(pair => pair.Value.ClientSessionId == session.Handle.SessionId).Select(pair => pair.Key).ToArray())
        {
            if (windows is not null && key.StartsWith("performance.") && IsWindowsIndexField(key[12..]))
            { if (!windowsComplete) complete = false; continue; }
            var step = plan.Steps.FirstOrDefault(item => item.Key == key || key.StartsWith("lighting.") && item.Key == "lighting" || key.StartsWith("fan.") && item.Key == "fan");
            if (step is not { Status: "Verified" } || !ExitMatches(step, controls, true, fanIdleNoMutation)) complete = false;
        }
        var fanJournal = await journal!.ReadAsync(token);
        if (fanIdleNoMutation && !await VerifyIdleFanReleaseAsync(session, plan.OperationId, controls, token, fanJournal)) complete = false;
        var fanCheckpoint = fanJournal.FanEcRecovery;
        if (fanCheckpoint?.Scope.ClientSessionId == session.Handle.SessionId &&
            (!ControlExecutionJournal.FanCheckpointComplete(fanCheckpoint.Checkpoint) ||
             fanCheckpoint.Scope != new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch, session.Handle.SessionId, session.OwnerSid, session.Identity)))
            complete = false;
        var firmwareCheckpoint = fanJournal.FanFirmwareRecovery;
        if (firmwareCheckpoint?.Scope.ClientSessionId == session.Handle.SessionId &&
            (!ControlExecutionJournal.FanFirmwareComplete(firmwareCheckpoint) ||
             firmwareCheckpoint.Scope != new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch, session.Handle.SessionId, session.OwnerSid, session.Identity)))
            complete = false;
        if (complete)
        {
            foreach (var key in ownedFields.Where(pair => pair.Value.ClientSessionId == session.Handle.SessionId).Select(pair => pair.Key).ToArray())
                ownedFields.Remove(key);
            lastSubmittedModeClientSessionId = null; lastSubmittedMode = null; fanOwner = null;
            fanOwnerClientSessionId = null;
            windowsScopeSessionId = null;
        }
        Volatile.Write(ref controlExecutionState, controlExecutionState with { Fields = SnapshotFields(), Pages = [],
            NativeMode = controls.PerformanceMode ?? controlExecutionState.NativeMode, NativeModeApplyState = ApplyState.Confirmed });
        Volatile.Write(ref state, State with { Controls = controls });
        await PersistExecutionAsync(plan.OperationId, !complete, token);
        if (!complete) return SessionRefused(requestId, ErrorCode.RollbackFailed, true) with
            { Mutation = hardwareDispatched.Value?.Started == true ? HardwareMutation.Changed : HardwareMutation.NoMutation };
        var completed = await clientSessions!.WriteAsync(session with { Phase = ClientControlSessionPhase.Completed }, token, requireExisting: true);
        Volatile.Write(ref activeClientSession, completed);
        return SessionCaptured(requestId, completed.Handle, false) with
            { Mutation = hardwareDispatched.Value?.Started == true ? HardwareMutation.Changed : HardwareMutation.NoMutation };

        async Task<bool> ReconcileExitGpuAsync()
        {
            if (completedPriorPlan)
                return plan.GpuRecoveryReady = await provider.ReconcileGpuRecoveryAsync(token);
            using var scope = journal!.BindGpuOperation(new(plan.OperationId, JournalServiceSessionId, JournalHardwareEpoch, session.OwnerSid));
            var pendingNative = CheckedPriorRestore() is null ? null : PriorExitGpuAttempt(session, plan, await journal.ReadAsync(token));
            if (pendingNative is { Step.Status: "Verified" })
                plan.GpuRecoveryReady = await provider.VerifyPendingGpuTargetAsync(plan.OperationId, token);
            else plan.GpuRecoveryReady = pendingNative is null && await provider.ReconcileGpuRecoveryAsync(token);
            if (!plan.GpuRecoveryReady && pendingNative is not { Step.Status: "Verified" })
            {
                bool previouslyDispatched = hardwareDispatched.Value?.Started == true;
                var recovered = await TrackHardwareDispatchAsync(() => provider.RecoverPendingGpuAsync(plan.OperationId, token));
                if (!previouslyDispatched && recovered.Mutation == HardwareMutation.NoMutation && hardwareDispatched.Value is { } attempt)
                    attempt.Started = false;
                plan.GpuRecoveryReady = await provider.ReconcileGpuRecoveryAsync(token);
            }
            EnsureControlHardwareAccess();
            await store.WriteAsync(plan, token);
            return plan.GpuRecoveryReady;
        }
    }

    private static bool ExitSame(SettingRequest? left, SettingRequest? right) => left?.Intent == right?.Intent &&
        (left?.Value is { } a && right?.Value is { } b ? JsonElement.DeepEquals(a, b) : left?.Value is null && right?.Value is null);

    private static bool ExitCpuPartialMatches(ExitStep step, HomeControlState controls) =>
        step.Key is "performance.negativeCurveOptimizer" or "performance.isBoostEnabled" &&
        step.Status is "Applying" or "Failed" && ExactCpuExitCheckpoint(step) &&
        KnownCpuDetails(CaptureCpuRestoration(step.Key[12..], controls.CpuTuning),
            step.ExpectedDetails, step.TargetDetails, step.TargetDetails);

    private static bool ExitMatches(ExitStep step, HomeControlState controls, bool target, bool fanIdleNoMutation = false)
    {
        if (step.Key.StartsWith("gpu.") && controls.GpuVf is not { Error: null, Nodes.Length: 127 }) return false;
        var expected = target ? step.Target : step.Expected;
        if ((target ? step.TargetDetails : step.ExpectedDetails) is { } details)
            return SameRestoration(details, CaptureCpuRestoration(step.Key[12..], controls.CpuTuning));
        if (step.Key == "mode") return ExitSame(SettingRequest.Set(controls.PerformanceMode), expected);
        if (step.Key == "lighting") return (!target || !controls.KeyboardLightingPreviewActive) &&
            controls.KeyboardLighting is { } actualLight && actualLight.IsValid() &&
            expected is { Intent: SettingIntent.Set, Value: { ValueKind: JsonValueKind.Object } lightValue } &&
            lightValue.Deserialize<KeyboardLightingPlan>(ControlPlanCompiler.Json) is { } expectedLight && expectedLight.IsValid() &&
            ControlPlanCompiler.CanonicalLighting(actualLight) == ControlPlanCompiler.CanonicalLighting(expectedLight);
        if (step.Key == "fan")
        {
            if (target && fanIdleNoMutation) return step.FanStrongCoolingTarget == false && IdleFanReleaseLive(controls);
            // No EC write was recorded for this branch; never invent or clear EC ownership.
            if (target && step.FanFirmwareOnly) return controls.ActiveFanControlPlan is null &&
                step.FanStrongCoolingTarget == true && controls.StrongCooling == true;
            if (target && step.FanMappingOriginalInitialization is byte original)
                return controls.ActiveFanControlPlan is null && step.FanStrongCoolingTarget is bool firmwareTarget &&
                    controls.StrongCooling == firmwareTarget && step.FanMappingTargetWritesStarted is bool started &&
                    step.FanMappingObservedAfterUtc is { } observedAfter && controls.FanEcMapping is { Restored: true } mapping &&
                    mapping.CapturedAtUtc >= observedAfter && mapping.CapturedAtUtc <= DateTimeOffset.UtcNow &&
                    mapping.OriginalInitialization == original && (mapping.Initialization & 0x80) == (original & 0x80) &&
                    mapping.TargetWritesStarted == started && (!started || mapping.TargetReleaseVerified);
            if (target) return controls.ActiveFanControlPlan is null && step.FanStrongCoolingTarget is bool firmware &&
                controls.StrongCooling == firmware && controls.FanEcControl is { } ec &&
                (ec.Control & 0x0A) == 0 && ec.CpuTarget == 0 && ec.GpuTarget == 0 &&
                (ec.ExpectedInitialization is null || (ec.Initialization & 0x80) == (ec.ExpectedInitialization.Value & 0x80));
            var values = expected?.Value?.Deserialize<Dictionary<string, SettingRequest>>(ControlPlanCompiler.Json);
            var current = ControlPlanCompiler.Read(ControlPageId.Fan, controls);
            return values is not null && values.All(pair => ExitSame(current.GetValueOrDefault(pair.Key), pair.Value));
        }
        int dot = step.Key.IndexOf('.');
        var page = step.Key.StartsWith("performance.") ? ControlPageId.Performance : ControlPageId.Gpu;
        return expected is not null && ExitSame(ControlPlanCompiler.Read(page, controls).GetValueOrDefault(step.Key[(dot + 1)..]), expected);
    }

    private static HardwareCommand[] ExitCommands(ExitStep step, HomeControlState controls)
    {
        Guid id = Guid.NewGuid();
        if (step.TargetDetails is not null) return [];
        return step.Key switch
        {
            "mode" => [new SetPerformanceModeCommand(id, step.Target!.Value!.Value.Deserialize<PerformanceMode>(ControlPlanCompiler.Json))],
            "lighting" => [new SetKeyboardLightingCommand(id, step.Target!.Value!.Value.Deserialize<KeyboardLightingPlan>(ControlPlanCompiler.Json)!)],
            "fan" => [new ReleaseFanControlCommand(id, ReleaseReason.SessionEnded)],
            "gpu.coreOffsetKhz" => [new SetGpuCoreOffsetCommand(id, controls.GpuVf!.CoreOffsetKhz!.Value, 0, true)
                { ExpectedVfOffsetsKhz = controls.GpuVf.Nodes.Select(node => node.OffsetKhz).ToArray() }],
            "gpu.memoryOffsetKhz" => [new SetGpuMemoryOffsetCommand(id, controls.GpuVf!.MemoryOffsetKhz!.Value, 0, true)],
            "gpu.vfOffsetsKhz" => [new SetGpuVfCurveCommand(id, controls.GpuVf!.Nodes.Select(node => node.OffsetKhz).ToArray(), new int[127], true)],
            _ when step.Key.StartsWith("performance.") => [new SetCpuTuningCommand(id, ExitCpuPlan(step.Key[12..], step.Target, controls), true)],
            _ => throw new InvalidDataException("exitRecoveryFieldUnsupported")
        };
    }

    private static CpuTuningPlan ExitCpuPlan(string field, SettingRequest? target, HomeControlState controls)
    {
        int? value = field != "pptWatts" && target?.Value?.ValueKind == JsonValueKind.Number ? target.Value.Value.GetInt32() : null;
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null);
        return field switch
        {
            "acMaxFrequencyMhz" => plan with { AcMaxFrequencyMhz = value },
            "dcMaxFrequencyMhz" => plan with { DcMaxFrequencyMhz = value },
            "acMinActiveCoresPercent" => plan with { AcMinActiveCoresPercent = value },
            "dcMinActiveCoresPercent" => plan with { DcMinActiveCoresPercent = value },
            "windowsPowerSchemeId" => plan with { WindowsPowerSchemeId = target!.Value!.Value.Deserialize<Guid>() },
            "negativeCurveOptimizer" => plan with { NegativeCurveOptimizer = value ?? controls.CpuTuning?.NegativeCurveOptimizer },
            "isBoostEnabled" => plan with { BoostPolicy = CpuBoostPolicy.FromState(controls.CpuTuning) },
            "pboScalar" => plan with { Advanced = new(PboScalar: value) },
            "pptWatts" => plan with { Advanced = new(PptWatts: target!.Value!.Value.GetDouble()) },
            "mp1TemperatureC" => plan with { Advanced = new(Mp1TemperatureC: value) },
            "tdcCurrentMilliamps" => plan with { Advanced = new(TdcCurrentMilliamps: value) },
            "edcCurrentMilliamps" => plan with { Advanced = new(EdcCurrentMilliamps: value) },
            _ => throw new InvalidDataException("exitCpuFieldUnsupported")
        };
    }
}
