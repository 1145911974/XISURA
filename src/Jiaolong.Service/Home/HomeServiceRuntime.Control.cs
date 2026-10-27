using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private readonly Dictionary<string, OwnedField> ownedFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> priorSessionFields = new(StringComparer.OrdinalIgnoreCase);
    private bool priorSessionFieldsLoaded;
    private ControlExecutionState? controlExecutionState;
    private PerformanceMode? lastSubmittedMode;
    private Guid? lastSubmittedModeClientSessionId;
    private long lastModeEpoch;
    private long lastModeConfirmationTimestamp;
    private PerformanceMode? externalModeCandidate;
    private PerformanceMode? lastExternalMode;

    private async Task<CommandResult> ExecuteControlRequestAsync(ApplyControlRequestCommand command, CancellationToken token, Guid connectionId,
        CompiledControlPlan? transient = null, bool applyNativeMode = true)
    {
        var admission = Execution.CurrentAdmission!;
        long lifecycleVersion = powerLifecycle.Version;
        string catalogOwner = admission.Automatic ? AutomaticCatalogOwner(admission.Owner) : admission.Owner;
        var request = command.Request;
        bool modeFollow = request?.Origin == ControlOrigin.ModeFollow;
        if (modeFollow && (admission.Automatic || request!.Pages is not
            [{ Follow: true, Page: ControlPageId.Performance or ControlPageId.Gpu or ControlPageId.Fan }]))
            return Rejected(command, ErrorCode.ValidationFailed);
        applyNativeMode &= !modeFollow;
        var lightingFollowProvider = provider as AdaptiveAutomationHardwareProvider;
        bool lightingFollow = lightingFollowProvider?.IsModeLightingFollowOperation(command.OperationId) == true;
        if (lightingFollow && (applyNativeMode || lightingFollowProvider?.MatchesModeLightingFollowRequest(command) != true))
            return Rejected(command, ErrorCode.UnauthorizedClient);
        if (request is null || request.OperationId != command.OperationId ||
            request.Origin == ControlOrigin.TurboBranch && (transient is null || admission.CommandKind is not (nameof(ApplyTurboBranchCommand) or nameof(ReleaseTurboBranchCommand))) ||
            !admission.Automatic && request.Origin is ControlOrigin.Adaptive or ControlOrigin.StartupRestore)
            return Rejected(command, ErrorCode.UnauthorizedClient);
        request = request with { Generation = admission.Generation };
        long? automaticRevision = admission.Automatic && provider is AdaptiveAutomationHardwareProvider automation
            ? await automation.ReadAutomaticConfigurationRevisionAsync(command.OperationId, token) : null;
        if (admission.Automatic && automaticRevision is null) return Rejected(command, ErrorCode.CancelledBeforeApply);
        CompiledControlPlan plan;
        HomeControlState controls;
        try
        {
            var hardware = await GetStateAsync(token);
            await LoadPriorSessionFieldsAsync(hardware.Identity, token);
            if (applyNativeMode && !CanExecute(new SetPerformanceModeCommand(command.OperationId, request.NativeMode), hardware))
                return Rejected(command, ErrorFor(hardware));
            plan = transient ?? ControlPlanCompiler.Compile(request, Execution.Catalog,
                catalogOwner, hardware.Capabilities, Execution.HardwareEpoch);
            if (lightingFollow)
                plan = plan with { Pages = plan.Pages.Select(page => page with
                { Settings = page.Settings.Where(pair => !pair.Key.Equals("logo", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) }).ToArray() };
            controls = await ReadControlBaselineForExecutionAsync(token, RequiresAdvanced(plan));
            // Validate every enabled page before the first mode/EC write.
            foreach (var page in plan.Pages)
            {
                ValidateRestorableTargets(page, controls);
                foreach (var next in ControlPlanCompiler.Commands(page.Selection.Page, NormalValues(page,
                    ResolveSettings(page, controls, command.OperationId, false).Values), controls))
                    if (next is (SetFanControlCommand or SetStrongCoolingCommand) && !CanWriteTuningFields(next, hardware))
                        throw new InvalidDataException("compiledCommandFieldNotWritable");
            }
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            var rejected = Rejected(command, ErrorCode.ValidationFailed);
            rejected.Error!.Details["stage"] = "Preflight";
            rejected.Error.Details["cause"] = error is InvalidDataException ? error.Message : error.GetType().Name;
            return rejected;
        }

        bool Fresh() => !IsSuspended && lifecycleVersion == powerLifecycle.Version &&
            Execution.IsCurrent(admission.Generation) && Execution.Catalog.Revision(catalogOwner) == plan.CatalogRevision;
        async Task<bool> FreshAsync() => Fresh() && (!admission.Automatic || provider is AdaptiveAutomationHardwareProvider automation &&
            await automation.ReadAutomaticConfigurationRevisionAsync(command.OperationId, token) == automaticRevision);
        var pages = new List<PageApplyResult>();
        PendingControlStep? pendingStep = null;
        CommandResult? modeResult = null;
        bool changed = false;
        if (!await FreshAsync()) return Rejected(command, ErrorCode.CancelledBeforeApply);
        bool nativeConfirmed = !modeFollow && ((!applyNativeMode && controls.PerformanceMode == request.NativeMode &&
            controls.ModeEvidence?.ParsedMode == request.NativeMode) || CanReuseNativeConfirmation(request.NativeMode, controls));
        bool presetModeAlreadySelected = false;
        if (applyNativeMode && !nativeConfirmed && request.Origin == ControlOrigin.PresetUse &&
            plan.Pages is [{ Selection.Page: ControlPageId.Gpu or ControlPageId.Performance }])
        {
            var observed = await provider.ReadNativeModeEvidenceAsync(token);
            var age = observed is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - observed.CapturedAtUtc;
            // Applying a single CPU/GPU page must not reset an already selected OEM mode.
            // Request-state readback still does not confirm effective performance settings.
            presetModeAlreadySelected = controls.PerformanceMode == request.NativeMode && observed?.ParsedMode == request.NativeMode &&
                observed.RawValue.HasValue && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(1);
            if (!await FreshAsync()) return Rejected(command, ErrorCode.CancelledBeforeApply);
        }
        if (applyNativeMode && !nativeConfirmed && !presetModeAlreadySelected)
        {
            var modeBefore = await ReadControlBaselineForExecutionAsync(token, true);
            Volatile.Write(ref controlExecutionState, new(admission.Generation, Execution.HardwareEpoch, plan.CatalogRevision,
                request.LogicalMode, request.NativeMode, [], SnapshotFields()) { NativeModeApplyState = ApplyState.Unknown });
            await PersistExecutionAsync(command.OperationId, true, token);
            modeResult = await ExecuteHardwareDirectAsync(new SetPerformanceModeCommand(command.OperationId, request.NativeMode), token, connectionId);
            nativeConfirmed = modeResult.VerifiedPerformanceMode == request.NativeMode;
            lastModeConfirmationTimestamp = nativeConfirmed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            changed |= modeResult.Mutation != HardwareMutation.NoMutation;
            if (modeResult.State != CommandState.Applied || modeResult.Error is not null)
            {
                var failed = Volatile.Read(ref controlExecutionState)! with
                { NativeModeApplyState = modeResult.Mutation == HardwareMutation.NoMutation ? ApplyState.Failed : ApplyState.Unknown };
                Volatile.Write(ref controlExecutionState, failed);
                return modeResult with { OperationId = command.OperationId, ExecutionState = failed };
            }
            lastSubmittedMode = request.NativeMode; lastModeEpoch = Execution.HardwareEpoch;
            lastSubmittedModeClientSessionId = AdmittedClientSessionId;
            await RecordNativeModeEffectsAsync(command.OperationId, request.NativeMode, modeBefore, modeResult, token);
        }
        controls = await ReadControlBaselineForExecutionAsync(token, RequiresAdvanced(plan));
        if (modeFollow || presetModeAlreadySelected)
        {
            var observed = await provider.ReadNativeModeEvidenceAsync(token);
            var age = observed is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - observed.CapturedAtUtc;
            // A page may follow or retain a selected mode, never apply after that mode has changed.
            if (controls.PerformanceMode != request.NativeMode || observed?.ParsedMode != request.NativeMode ||
                observed.RawValue is null || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1) || !await FreshAsync())
                return Rejected(command, ErrorCode.CancelledBeforeApply);
        }
        foreach (var page in plan.Pages)
        {
            if (!await FreshAsync())
            { pages.Add(new(page.Selection.Page, page.Selection.Key, page.Selection.Revision, ApplyState.Superseded, [], [], page.Settings.Keys.ToArray(), null)); continue; }
            var resolved = ResolveSettings(page, controls, command.OperationId, true);
            var confirmed = new List<string>(); var unverified = new List<string>();
            var skipped = page.Skipped.Concat(resolved.Skipped).Distinct().ToList();
            CommandResult? failure = null;
            try
            {
                foreach (var field in SpecialRestores(page, resolved.Values))
                {
                    if (!await FreshAsync()) break;
                    string key = FieldKey(page.Selection.Page, field);
                    var owner = ownedFields[key];
                    bool directScalar = IsDirectCpuScalar(field);
                    if (directScalar && !CanRestoreCpuScalar(field)) { skipped.Add(field); continue; }
                    ownedFields[key] = owner with { OperationId = command.OperationId, State = ApplyState.Unknown };
                    if (directScalar)
                    {
                        var marker = new SetCpuTuningCommand(Guid.NewGuid(), ExitCpuPlan(field, owner.Baseline, controls), true)
                            { ClientSessionId = AdmittedClientSessionId };
                        pendingStep = new(marker, [key]);
                        ownedFields[key] = ownedFields[key] with { LastWritten = owner.Baseline, WriteBefore = owner.LastWritten };
                    }
                    Volatile.Write(ref controlExecutionState, new(admission.Generation, Execution.HardwareEpoch, plan.CatalogRevision,
                        request.LogicalMode, request.NativeMode, directScalar ? [new(page.Selection.Page, page.Selection.Key, page.Selection.Revision,
                            ApplyState.Unknown, [], [], [], null)] : [], SnapshotFields())
                    { NativeModeApplyState = nativeConfirmed ? ApplyState.Confirmed : ApplyState.SubmittedUnverified,
                        PendingStep = directScalar ? pendingStep : null,
                        CpuWriteBatch = directScalar ? CpuBatchForStep(pendingStep!.Command, command.OperationId) : null });
                    await PersistPendingOwnershipAsync(command.OperationId, new Dictionary<string, OwnedField?> { [key] = owner }, token);
                    var restored = await TrackHardwareDispatchAsync(() => IsDirectCpuScalar(field)
                        ? provider.RestoreCpuScalarAsync(command.OperationId, field, owner.Baseline!, owner.LastWritten!, token)
                        : provider.RestoreCpuFieldAsync(command.OperationId, field, owner.BaselineDetails!, owner.LastDetails!, token));
                    changed |= restored.Mutation != HardwareMutation.NoMutation;
                    if (restored.State == CommandState.Applied && restored.Error is null)
                    { ownedFields.Remove(key); confirmed.Add(field); }
                    else
                    {
                        ownedFields[key] = restored.Mutation == HardwareMutation.NoMutation ? owner : ownedFields[key];
                        failure = restored; break;
                    }
                    controls = await ReadControlBaselineForExecutionAsync(token, RequiresAdvanced(plan));
                    if (Volatile.Read(ref state) is { } currentHardware)
                        Volatile.Write(ref state, currentHardware with { Controls = controls });
                }
                var commands = failure is null ? ControlPlanCompiler.Commands(page.Selection.Page, NormalValues(page, resolved.Values), controls) : [];
                if (lightingFollow)
                    commands = commands.Select(next => next is SetKeyboardLightingCommand light
                        ? light with { Plan = light.Plan with { LogoEnabled = null } } : next).ToArray();
                foreach (var next in commands)
                {
                    if (!await FreshAsync()) break;
                    var fields = TrackedCommandFields(next);
                    var commandValues = (next is SetGpuCoreOffsetCommand or SetFanControlCommand or SetCpuTuningCommand or SetCpuTuningBatchCommand or SetKeyboardLightingCommand ? IntendedValues(next, controls) : resolved.Values)
                        .ToDictionary(pair => pair.Key, pair => (SettingRequest?)pair.Value, StringComparer.OrdinalIgnoreCase);
                    if (next is SetFanControlCommand)
                        foreach (string field in fields) commandValues.TryAdd(field, null);
                    var before = ControlPlanCompiler.Read(page.Selection.Page, controls);
                    var beforeCpu = controls.CpuTuning;
                    Guid? baselineScheme = controls.CpuTuning?.WindowsPowerSchemeId;
                    var previousOwnership = new Dictionary<string, OwnedField?>();
                    foreach (string field in fields.Where(commandValues.ContainsKey))
                    {
                        string key = FieldKey(page.Selection.Page, field);
                        ownedFields.TryGetValue(key, out var previous);
                        previousOwnership[key] = previous;
                        bool retainBaseline = request.Origin != ControlOrigin.TurboBranch || previous?.Origin == ControlOrigin.TurboBranch;
                        var baseline = retainBaseline && SameClientSession(previous) && previous?.OwnerSid == admission.Owner && previous.Epoch == Execution.HardwareEpoch && (IsDirectCpuScalar(field) || previous.Scheme == baselineScheme) &&
                            MatchesOwnedValue(previous, before.GetValueOrDefault(field), controls.CpuTuning) ? previous.Baseline : before.GetValueOrDefault(field);
                        ownedFields[key] = new(command.OperationId, request.Origin, baseline, commandValues[field], ApplyState.Unknown,
                            Execution.HardwareEpoch, baselineScheme, admission.Generation,
                            retainBaseline && SameClientSession(previous) && previous?.OwnerSid == admission.Owner && previous.Epoch == Execution.HardwareEpoch && previous.Scheme == baselineScheme && MatchesDetails(previous, controls.CpuTuning)
                                ? previous.BaselineDetails : CaptureCpuRestoration(field, controls.CpuTuning))
                        {
                            OwnerSid = admission.Owner, ClientSessionId = AdmittedClientSessionId,
                            WriteBefore = before.GetValueOrDefault(field),
                            WriteBeforeDetails = page.Selection.Page == ControlPageId.Performance ? CaptureCpuRestoration(field, beforeCpu) : null,
                            WriteTargetDetails = page.Selection.Page == ControlPageId.Performance ? CpuWriteTarget(next, field, beforeCpu) : null
                        };
                    }
                    pendingStep = new(next with { ClientSessionId = AdmittedClientSessionId }, previousOwnership.Keys.ToArray());
                    Volatile.Write(ref controlExecutionState, new(admission.Generation, Execution.HardwareEpoch, plan.CatalogRevision,
                        request.LogicalMode, request.NativeMode, pages.Append(new(page.Selection.Page, page.Selection.Key, page.Selection.Revision,
                            ApplyState.Unknown, confirmed.ToArray(), unverified.ToArray(), skipped.ToArray(), null)).ToArray(), SnapshotFields())
                        { NativeModeApplyState = nativeConfirmed ? ApplyState.Confirmed : ApplyState.SubmittedUnverified,
                          PendingStep = pendingStep, CpuWriteBatch = CpuBatchForStep(next, command.OperationId) });
                    await PersistPendingOwnershipAsync(command.OperationId, previousOwnership, token);
                    var result = await ExecuteHardwareDirectAsync(next, token, connectionId, previousOwnership);
                    changed |= result.Mutation != HardwareMutation.NoMutation;
                    controls = await ReadControlBaselineForExecutionAsync(token, RequiresAdvanced(plan) || HasAdvanced(next));
                    var after = ControlPlanCompiler.Read(page.Selection.Page, controls);
                    foreach (var field in fields.Where(commandValues.ContainsKey))
                    {
                        var intended = commandValues[field];
                        bool readback = FieldReadable(field, controls) && (intended is null ? !after.ContainsKey(field) :
                            FieldMatches(field, after.GetValueOrDefault(field), intended, controls.CpuTuning));
                        var writeState = result.State == CommandState.RecoveryRequired || result.Mutation == HardwareMutation.Unknown && result.Error is not null
                            ? ApplyState.Unknown : result.State != CommandState.Applied || result.Error is not null ? ApplyState.Failed
                            : readback ? ApplyState.Confirmed : ApplyState.SubmittedUnverified;
                        string key = FieldKey(page.Selection.Page, field);
                        var previous = previousOwnership.GetValueOrDefault(key);
                        if (result.Mutation == HardwareMutation.NoMutation || result.State == CommandState.RolledBack)
                        { if (previous is null) ownedFields.Remove(key); else ownedFields[key] = previous; }
                        if (result.Mutation != HardwareMutation.NoMutation && writeState != ApplyState.Failed)
                        {
                            bool retainBaseline = request.Origin != ControlOrigin.TurboBranch || previous?.Origin == ControlOrigin.TurboBranch;
                            var baseline = retainBaseline && SameClientSession(previous) && previous?.OwnerSid == admission.Owner && previous.Epoch == Execution.HardwareEpoch && (IsDirectCpuScalar(field) || previous.Scheme == baselineScheme) &&
                                MatchesOwnedValue(previous, before.GetValueOrDefault(field), beforeCpu)
                                ? previous.Baseline : before.GetValueOrDefault(field);
                            ownedFields[key] = ownedFields[key] with { Baseline = baseline, LastWritten = intended, State = writeState,
                                LastDetails = CaptureCpuRestoration(field, controls.CpuTuning) };
                        }
                        if (readback && page.Settings.GetValueOrDefault(field)?.Intent == SettingIntent.Official && request.Origin != ControlOrigin.TurboBranch) ownedFields.Remove(key);
                        if (page.Settings.ContainsKey(field))
                        {
                            if (writeState == ApplyState.Confirmed) confirmed.Add(field);
                            else if (writeState is ApplyState.SubmittedUnverified or ApplyState.Unknown) unverified.Add(field);
                        }
                    }
                    if (result.State != CommandState.Applied || result.Error is not null) { failure = result; break; }
                }
            }
            catch (InvalidDataException) { failure = Rejected(command, ErrorCode.ValidationFailed); }
            foreach (var field in resolved.Unchanged)
                if (FieldReadable(field)) confirmed.Add(field);
                else unverified.Add(field);
            ApplyState status = !await FreshAsync() ? ApplyState.Superseded : failure?.State == CommandState.RecoveryRequired ||
                failure is { Mutation: HardwareMutation.Unknown, Error: not null } && failure.State != CommandState.RolledBack ? ApplyState.Unknown
                : failure is not null ? confirmed.Count + unverified.Count > 0 ? ApplyState.Partial : ApplyState.Failed
                : skipped.Count > 0 ? ApplyState.Partial : unverified.Count > 0 ? ApplyState.SubmittedUnverified : ApplyState.Confirmed;
            pages.Add(new(page.Selection.Page, page.Selection.Key, page.Selection.Revision, status,
                confirmed.Distinct().ToArray(), unverified.Distinct().ToArray(), skipped.ToArray(), failure?.Error?.Code.ToString()));
            if (status == ApplyState.Unknown) break;
        }
        var executionState = new ControlExecutionState(admission.Generation, Execution.HardwareEpoch, plan.CatalogRevision,
            request.LogicalMode, request.NativeMode, pages.ToArray(), SnapshotFields())
        { NativeModeApplyState = nativeConfirmed ? ApplyState.Confirmed : ApplyState.SubmittedUnverified,
          PendingStep = pages.Any(page => page.State == ApplyState.Unknown) ? pendingStep : null,
          CpuWriteBatch = controlExecutionState?.CpuWriteBatch, WindowsWriteProofHash = controlExecutionState?.WindowsWriteProofHash };
        Volatile.Write(ref controlExecutionState, executionState);
        return new(command.OperationId, pages.Any(page => page.State == ApplyState.Unknown) ? CommandState.RecoveryRequired : CommandState.Applied,
            null, RequiredUserAction.None, null, false)
        { Mutation = changed ? HardwareMutation.Changed : HardwareMutation.NoMutation, ExecutionState = executionState,
          ModeEvidence = modeResult?.ModeEvidence ?? controls.ModeEvidence, VerifiedPerformanceMode = nativeConfirmed ? request.NativeMode : null };
    }

    private bool CanReuseNativeConfirmation(PerformanceMode mode, HomeControlState controls) =>
        lastSubmittedMode == mode && lastModeEpoch == Execution.HardwareEpoch && controls.PerformanceMode == mode &&
        lastModeConfirmationTimestamp > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastModeConfirmationTimestamp) <= TimeSpan.FromSeconds(3) &&
        Volatile.Read(ref controlExecutionState)?.NativeModeApplyState == ApplyState.Confirmed;

    private async Task LoadPriorSessionFieldsAsync(HardwareIdentity identity, CancellationToken token)
    {
        if (priorSessionFieldsLoaded || journal is null) return;
        var saved = await journal.ReadAsync(token);
        if (saved.Identity == identity && saved.Current is { } prior && prior.HardwareEpoch != Execution.HardwareEpoch)
            foreach (var (key, field) in prior.Fields)
                if (field.Baseline is not null || field.BaselineDetails is not null)
                    priorSessionFields.Add(key);
        priorSessionFieldsLoaded = true;
    }

    private (Dictionary<string, SettingRequest> Values, string[] Skipped, string[] Unchanged) ResolveSettings(
        CompiledControlPage page, HomeControlState controls, Guid operationId, bool retireExternal)
    {
        var current = ControlPlanCompiler.Read(page.Selection.Page, controls);
        var values = new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>(); var unchanged = new List<string>();
        foreach (var (field, setting) in page.Settings)
        {
            if (page.Selection.Page == ControlPageId.Gpu && field == "coreFrequencyLimitMhz" &&
                setting.Intent == SettingIntent.Set &&
                State.Capabilities.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase))
                    is not { Readable: true, Writable: true })
            { skipped.Add(field); continue; }
            string key = FieldKey(page.Selection.Page, field);
            ownedFields.TryGetValue(key, out var owned);
            if (owned?.OwnerSid != Execution.CurrentAdmission?.Owner) owned = null;
            bool matches = owned is not null && owned.Epoch == Execution.HardwareEpoch && (IsDirectCpuScalar(field) || owned.Scheme == controls.CpuTuning?.WindowsPowerSchemeId) &&
                owned.State == ApplyState.Confirmed && (owned.LastDetails is not null ? MatchesDetails(owned, controls.CpuTuning) :
                    current.TryGetValue(field, out var observed) && ControlPlanCompiler.Same(observed, owned.LastWritten));
            if (owned is { State: ApplyState.Confirmed } && !matches && current.ContainsKey(field) && retireExternal) ownedFields.Remove(key);
            if (setting.Intent == SettingIntent.Official)
            {
                if (owned is null)
                { if (priorSessionFields.Contains(key) || !FieldReadable(field)) skipped.Add(field); else unchanged.Add(field); continue; }
                var capability = State.Capabilities.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (!matches || owned.Baseline is null && owned.BaselineDetails is null || capability is not { Writable: true, Readable: true } ||
                    field is "temperatureLimitC" or "splWatts" or "spptWatts" && capability.NativeModeSideEffect != "PreservesSelectedMode")
                { skipped.Add(field); continue; }
                var baseline = owned.Baseline ?? owned.LastWritten!;
                if (page.Selection.Page == ControlPageId.Performance &&
                    (field is "acMaxFrequencyMhz" or "dcMaxFrequencyMhz") &&
                    current.TryGetValue(field, out var currentFrequency) && ControlPlanCompiler.Same(currentFrequency, baseline))
                {
                    // An owned Windows cap already at baseline needs ownership release, not another power write.
                    if (retireExternal) ownedFields.Remove(key);
                    unchanged.Add(field);
                    continue;
                }
                values[field] = baseline;
            }
            else if (current.TryGetValue(field, out var value) && FieldMatches(field, value, setting, controls.CpuTuning) ||
                owned is { State: ApplyState.SubmittedUnverified } && !FieldReadable(field) && owned.Epoch == Execution.HardwareEpoch &&
                owned.Scheme == controls.CpuTuning?.WindowsPowerSchemeId && ControlPlanCompiler.Same(owned.LastWritten, setting)) unchanged.Add(field);
            else values[field] = setting;
        }
        return (values, skipped.ToArray(), unchanged.ToArray());
    }

    private async Task<CommandResult> ExecuteTrackedCommandAsync(HardwareCommand command, CancellationToken token, Guid connectionId)
    {
        if (command is SetGpuFrequencyLimitCommand clock)
            return await ExecuteClockSubmissionAsync(clock, token, connectionId);
        if (command is SetPerformanceModeCommand modeCommand)
        {
            var modeBefore = await ReadControlBaselineForExecutionAsync(token, true);
            SetPendingLegacy(command, modeCommand.Mode);
            await PersistExecutionAsync(command.OperationId, true, token);
            var modeResult = await ExecuteHardwareDirectAsync(command, token, connectionId);
            EnsureControlHardwareAccess();
            var rolledBackMode = modeResult.State == CommandState.RolledBack &&
                modeResult.ModeEvidence?.ParsedMode is PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo
                    ? modeResult.ModeEvidence.ParsedMode : null;
            lastModeConfirmationTimestamp = modeResult.VerifiedPerformanceMode == modeCommand.Mode ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (modeResult is { State: CommandState.Applied, Error: null })
            { lastSubmittedMode = modeCommand.Mode; lastModeEpoch = Execution.HardwareEpoch; lastSubmittedModeClientSessionId = AdmittedClientSessionId; }
            Volatile.Write(ref controlExecutionState, controlExecutionState! with
            { NativeMode = rolledBackMode ?? controlExecutionState.NativeMode,
              NativeModeApplyState = modeResult.State == CommandState.RecoveryRequired ? ApplyState.Unknown :
                modeResult.State == CommandState.Applied && modeResult.Error is null && modeResult.VerifiedPerformanceMode == modeCommand.Mode ? ApplyState.Confirmed :
                rolledBackMode is not null ? ApplyState.Failed :
                modeResult.Mutation == HardwareMutation.NoMutation && modeResult.Error is not null ? ApplyState.Failed :
                modeResult.Error is not null ? ApplyState.Unknown : ApplyState.SubmittedUnverified });
            await RecordNativeModeEffectsAsync(command.OperationId, modeCommand.Mode, modeBefore, modeResult, token);
            return modeResult;
        }
        if (!CanWriteTuningFields(command, await GetStateAsync(token)))
            return Rejected(command, ErrorCode.CapabilityUnavailable);
        var page = CommandPage(command);
        var beforeControls = await ReadControlBaselineForExecutionAsync(token, HasAdvanced(command));
        var before = ControlPlanCompiler.Read(page, beforeControls);
        var intended = IntendedValues(command, beforeControls);
        foreach (string field in intended.Keys)
        {
            if (IsDirectCpuScalar(field) && before.GetValueOrDefault(field) is null)
                return Rejected(command, ErrorCode.HardwareReadFailed);
            if (field is "isBoostEnabled" or "negativeCurveOptimizer" && CaptureCpuRestoration(field, beforeControls.CpuTuning) is null)
                return Rejected(command, ErrorCode.HardwareReadFailed);
        }
        var previousOwnership = new Dictionary<string, OwnedField?>();
        foreach (string field in TrackedCommandFields(command))
        {
            string key = FieldKey(page, field);
            ownedFields.TryGetValue(key, out var previous);
            previousOwnership[key] = previous;
            var baseline = SameClientSession(previous) && previous?.OwnerSid == Execution.CurrentAdmission!.Owner && previous.Epoch == Execution.HardwareEpoch && (IsDirectCpuScalar(field) || previous.Scheme == beforeControls.CpuTuning?.WindowsPowerSchemeId)
                && MatchesOwnedValue(previous, before.GetValueOrDefault(field), beforeControls.CpuTuning) ? previous.Baseline : before.GetValueOrDefault(field);
            ownedFields[key] = new(command.OperationId, ControlOrigin.ManualLive, baseline, intended.GetValueOrDefault(field),
                ApplyState.Unknown, Execution.HardwareEpoch, beforeControls.CpuTuning?.WindowsPowerSchemeId, Execution.CurrentAdmission!.Generation,
                SameClientSession(previous) && previous?.OwnerSid == Execution.CurrentAdmission!.Owner && previous.Epoch == Execution.HardwareEpoch && MatchesDetails(previous, beforeControls.CpuTuning)
                    ? previous.BaselineDetails : CaptureCpuRestoration(field, beforeControls.CpuTuning))
            {
                OwnerSid = Execution.CurrentAdmission!.Owner, ClientSessionId = AdmittedClientSessionId,
                WriteBefore = before.GetValueOrDefault(field),
                WriteBeforeDetails = page == ControlPageId.Performance ? CaptureCpuRestoration(field, beforeControls.CpuTuning) : null,
                WriteTargetDetails = page == ControlPageId.Performance ? CpuWriteTarget(command, field, beforeControls.CpuTuning) : null
            };
        }
        SetPendingLegacy(command, beforeControls.PerformanceMode);
        if (page == ControlPageId.Lighting)
            Volatile.Write(ref controlExecutionState, controlExecutionState! with
            { PendingStep = new(command with { ClientSessionId = AdmittedClientSessionId }, previousOwnership.Keys.ToArray()) });
        if (command is SetCpuTuningBatchCommand batch)
            Volatile.Write(ref controlExecutionState, controlExecutionState! with { CpuWriteBatch = batch });
        else if (command is SetCpuTuningCommand cpu)
            Volatile.Write(ref controlExecutionState, controlExecutionState! with { CpuWriteBatch = new(command.OperationId, [cpu.Plan], cpu.RiskConfirmed) });
        await PersistExecutionAsync(command.OperationId, true, token);
        var dispatchStartedUtc = DateTimeOffset.UtcNow;
        var result = await ExecuteHardwareDirectAsync(command, token, connectionId, previousOwnership);
        EnsureControlHardwareAccess();
        var afterControls = State.Controls;
        var after = ControlPlanCompiler.Read(page, afterControls);
        bool mappingLeaseHeld = afterControls.FanEcMapping is { Restored: false };
        if (command is ReleaseFanControlCommand && journal is not null &&
            (await journal.ReadAsync(token)).FanEcRecovery is { Checkpoint.MappingLease: not null } mappingRecovery &&
            mappingRecovery.Scope.ClientSessionId == AdmittedClientSessionId)
            mappingLeaseHeld |= !ControlExecutionJournal.FanCheckpointComplete(mappingRecovery.Checkpoint);
        // A target-only release retains the authority needed to restore mapping at session end.
        bool fanTargetsReleased = command is ReleaseFanControlCommand && result is { State: CommandState.Applied, Error: null } &&
            afterControls.ActiveFanControlPlan is null && afterControls.FanEcControl is { } ec &&
            ec.CapturedAtUtc >= dispatchStartedUtc && (ec.Control & 0x0A) == 0 && ec.CpuTarget == 0 && ec.GpuTarget == 0 &&
            (mappingLeaseHeld ? (ec.Initialization & 0x80) != 0 :
                beforeControls.FanEcControl?.ExpectedInitialization is not byte expected ||
                    (ec.Initialization & 0x80) == (expected & 0x80));
        bool fanReleased = fanTargetsReleased && !mappingLeaseHeld;
        foreach (string field in TrackedCommandFields(command))
        {
            string key = FieldKey(page, field);
            if (result.Mutation == HardwareMutation.NoMutation || result.State == CommandState.RolledBack)
            {
                if (previousOwnership[key] is { } prior) ownedFields[key] = prior; else ownedFields.Remove(key);
                continue;
            }
            ownedFields[key] = ownedFields[key] with { LastDetails = CaptureCpuRestoration(field, afterControls.CpuTuning), State =
                result.State == CommandState.RecoveryRequired ? ApplyState.Unknown : FieldReadable(field, afterControls) && result.Error is null && result.State == CommandState.Applied &&
                intended.TryGetValue(field, out var desired) && FieldMatches(field, after.GetValueOrDefault(field), desired, afterControls.CpuTuning)
                ? ApplyState.Confirmed : result.Error is not null ? ApplyState.Unknown : ApplyState.SubmittedUnverified };
            if ((page == ControlPageId.Lighting || page == ControlPageId.Gpu && field is ("coreOffsetKhz" or "memoryOffsetKhz" or "vfOffsetsKhz") ||
                 page == ControlPageId.Performance && field is ("acMaxFrequencyMhz" or "dcMaxFrequencyMhz" or
                     "acMinActiveCoresPercent" or "dcMinActiveCoresPercent")) &&
                ownedFields[key] is { State: ApplyState.Confirmed, Baseline: { } baseline } owner &&
                (page != ControlPageId.Performance || owner.Scheme == afterControls.CpuTuning?.WindowsPowerSchemeId) &&
                after.TryGetValue(field, out var observed) && FieldMatches(field, observed, baseline, afterControls.CpuTuning))
                ownedFields.Remove(key);
            // Boost's bool can match while the original AC/DC mode indices still differ.
            if (page == ControlPageId.Performance && field == "isBoostEnabled" &&
                ownedFields[key] is { State: ApplyState.Confirmed, BaselineDetails: { } boostBaseline } &&
                SameRestoration(boostBaseline, CaptureCpuRestoration(field, afterControls.CpuTuning)))
                ownedFields.Remove(key);
            if (fanReleased) ownedFields.Remove(key);
        }
        if (fanTargetsReleased && mappingLeaseHeld && afterControls.StrongCooling is bool cooling &&
            ownedFields.TryGetValue(FieldKey(ControlPageId.Fan, "strongCooling"), out var coolingOwner))
            ownedFields[FieldKey(ControlPageId.Fan, "strongCooling")] = coolingOwner with
                { LastWritten = SettingRequest.Set(cooling), State = ApplyState.Confirmed };
        if (fanReleased && afterControls.StrongCooling is false)
            ownedFields.Remove(FieldKey(ControlPageId.Fan, "strongCooling"));
        Volatile.Write(ref controlExecutionState, controlExecutionState! with { Fields = SnapshotFields(),
            PendingStep = page == ControlPageId.Lighting && result is { State: CommandState.Applied, Error: null,
                Mutation: HardwareMutation.Changed or HardwareMutation.NoMutation } &&
                controlExecutionState!.PendingStep?.Command.OperationId == command.OperationId &&
                !ownedFields.Values.Any(owner => owner.State == ApplyState.Unknown) ? null : controlExecutionState!.PendingStep,
            Pages = [new(page, null, 0, result.State == CommandState.RecoveryRequired ? ApplyState.Unknown :
                result.Error is not null ? ApplyState.Failed : ApplyState.SubmittedUnverified, [], [], [], result.Error?.Code.ToString())] });
        return result with { ExecutionState = controlExecutionState };
    }

    private void SetPendingLegacy(HardwareCommand command, PerformanceMode? mode)
    {
        var admission = Execution.CurrentAdmission!;
        var native = mode ?? controlExecutionState?.NativeMode ?? PerformanceMode.Balanced;
        var logical = native switch { PerformanceMode.Quiet => ControlModeId.Office, PerformanceMode.Turbo => ControlModeId.Turbo, _ => ControlModeId.Gaming };
        Volatile.Write(ref controlExecutionState, new(admission.Generation, Execution.HardwareEpoch, Execution.Catalog.Revision(admission.Owner),
            logical, native, [], SnapshotFields()) { NativeModeApplyState = command is SetPerformanceModeCommand ? ApplyState.Unknown :
                controlExecutionState?.NativeModeApplyState ?? ApplyState.SubmittedUnverified });
    }

    private async Task PersistPendingOwnershipAsync(Guid operationId, Dictionary<string, OwnedField?> previousOwnership, CancellationToken token)
    {
        try { await PersistExecutionAsync(operationId, true, token); }
        catch
        {
            foreach (var item in previousOwnership)
                if (item.Value is null) ownedFields.Remove(item.Key); else ownedFields[item.Key] = item.Value;
            if (Volatile.Read(ref controlExecutionState) is { } current)
                Volatile.Write(ref controlExecutionState, current with { Fields = SnapshotFields() });
            throw;
        }
    }

    private Dictionary<string, FieldExecutionState> SnapshotFields() => ownedFields.ToDictionary(pair => pair.Key,
        pair => new FieldExecutionState(pair.Value.OperationId, pair.Value.Origin, pair.Value.Baseline, pair.Value.LastWritten, pair.Value.State)
        {
            BaselineDetails = pair.Value.BaselineDetails, LastWrittenDetails = pair.Value.LastDetails, ClientSessionId = pair.Value.ClientSessionId,
            WriteBefore = pair.Value.WriteBefore, WriteBeforeDetails = pair.Value.WriteBeforeDetails, WriteTargetDetails = pair.Value.WriteTargetDetails
        });
    private static string FieldKey(ControlPageId page, string field) => page.ToString().ToLowerInvariant() + "." + field;
    private async Task ObserveExternalModeAsync(HomeControlState controls, CancellationToken token)
    {
        if (IsSuspended) return;
        if (!Execution.IsIdle || lastSubmittedMode is null || controls.ModeEvidence?.ParsedMode is not { } observed ||
            observed == lastSubmittedMode || observed == lastExternalMode)
        { externalModeCandidate = null; return; }
        if (externalModeCandidate != observed) { externalModeCandidate = observed; return; }
        await Execution.WithHardwareGateAsync(async () =>
        {
            // Admission can occur between polls; confirm under the same hardware gate.
            if (IsSuspended) return false;
            var confirmed = await provider.ReadControlBaselineAsync(token);
            if (IsSuspended) return false;
            if (confirmed.ModeEvidence?.ParsedMode != observed || observed == lastSubmittedMode || observed == lastExternalMode) return false;
            if (provider is AdaptiveAutomationHardwareProvider adaptive)
                await adaptive.PrepareManualCommandAsync(new SetPerformanceModeCommand(Guid.NewGuid(), observed), token);
            Execution.InvalidateIntent();
            foreach (var page in Enum.GetValues<ControlPageId>())
            {
                var current = ControlPlanCompiler.Read(page, confirmed);
                foreach (string field in current.Keys)
                {
                    string key = FieldKey(page, field);
                    if (ownedFields.TryGetValue(key, out var owner) && owner.State == ApplyState.Confirmed &&
                        !MatchesOwnedValue(owner, current[field], confirmed.CpuTuning)) ownedFields.Remove(key);
                }
            }
            lastExternalMode = observed; externalModeCandidate = null;
            if (controlExecutionState is { } state) Volatile.Write(ref controlExecutionState, state with
            { Generation = Execution.Generation, NativeModeApplyState = ApplyState.Superseded, Fields = SnapshotFields() });
            return true;
        }, token);
    }
    private static ControlPageId CommandPage(HardwareCommand command) => command switch
    { SetCpuTuningCommand or SetCpuTuningBatchCommand => ControlPageId.Performance,
      SetFanControlCommand or ReleaseFanControlCommand or SetStrongCoolingCommand => ControlPageId.Fan,
      SetKeyboardLightingCommand or RestoreKeyboardLightingPreviewCommand or SetLidLogoCommand => ControlPageId.Lighting, _ => ControlPageId.Gpu };
    private static string[] CommandFields(HardwareCommand command) => command switch
    {
        SetCpuTuningCommand cpu => CpuFields(cpu.Plan), SetCpuTuningBatchCommand batch => batch.Plans.SelectMany(CpuFields).Distinct().ToArray(),
        SetGpuFrequencyLimitCommand => ["coreFrequencyLimitMhz"], SetGpuCoreOffsetCommand => ["coreOffsetKhz"],
        SetGpuMemoryOffsetCommand => ["memoryOffsetKhz"], SetGpuVfCurveCommand => ["vfOffsetsKhz"],
        SetStrongCoolingCommand => ["strongCooling"],
        SetFanControlCommand => ["strategy", "maximumRpm", "fixedRpm", "cpu", "gpu", "strongCooling"],
        ReleaseFanControlCommand => ["strategy", "maximumRpm", "fixedRpm", "cpu", "gpu"],
        SetKeyboardLightingCommand => ["effect", "brightness", "hex", "speed", "logo"], SetLidLogoCommand => ["logo"], _ => []
    };
    // Core P0 writes also move every editable V/F node; retain that dependency for exit CAS.
    private static string[] TrackedCommandFields(HardwareCommand command) => command is SetGpuCoreOffsetCommand
        ? ["coreOffsetKhz", "vfOffsetsKhz"] : command is SetKeyboardLightingCommand { Plan.LogoEnabled: null }
            ? CommandFields(command).Where(field => field != "logo").ToArray() : CommandFields(command);
    private static string[] CpuFields(CpuTuningPlan plan)
    {
        var values = new Dictionary<string, object?> { ["temperatureLimitC"] = plan.TemperatureLimitC, ["splWatts"] = plan.SplWatts,
            ["spptWatts"] = plan.SpptWatts, ["acMaxFrequencyMhz"] = plan.AcMaxFrequencyMhz ?? plan.MaxFrequencyMhz,
            ["dcMaxFrequencyMhz"] = plan.DcMaxFrequencyMhz ?? plan.MaxFrequencyMhz, ["isBoostEnabled"] = (object?)plan.BoostPolicy ?? plan.BoostEnabled,
            ["windowsPowerSchemeId"] = plan.WindowsPowerSchemeId, ["acMinActiveCoresPercent"] = plan.AcMinActiveCoresPercent,
            ["dcMinActiveCoresPercent"] = plan.DcMinActiveCoresPercent,
            ["negativeCurveOptimizer"] = (object?)plan.NegativeCurveOptimizer ?? (object?)plan.Advanced?.CurveOptimizerAll ?? plan.Advanced?.PerCoreCurveOptimizer,
            ["pptWatts"] = plan.Advanced?.PptWatts, ["mp1TemperatureC"] = plan.Advanced?.Mp1TemperatureC, ["tdcCurrentMilliamps"] = plan.Advanced?.TdcCurrentMilliamps, ["edcCurrentMilliamps"] = plan.Advanced?.EdcCurrentMilliamps,
            ["advancedCpuTuning"] = ControlPlanCompiler.ResidualSmu(plan.Advanced), ["pboScalar"] = plan.Advanced?.PboScalar };
        return values.Where(pair => pair.Value is not null).Select(pair => pair.Key).ToArray();
    }
    private bool CanWriteTuningFields(HardwareCommand command, HomeHardwareState current)
    {
        // Handing control back to firmware must remain possible after write capabilities disappear.
        if (command is ReleaseFanControlCommand) return true;
        if (command is not (SetCpuTuningCommand or SetCpuTuningBatchCommand or SetGpuFrequencyLimitCommand or
            SetGpuCoreOffsetCommand or SetGpuMemoryOffsetCommand or SetGpuVfCurveCommand or SetFanControlCommand or SetStrongCoolingCommand)) return true;
        if (command is SetCpuTuningBatchCommand batch)
            return batch.Plans.All(plan => CanWriteTuningFields(new SetCpuTuningCommand(command.OperationId, plan, batch.RiskConfirmed), current));
        var intended = IntendedValues(command);
        var fields = command is SetFanControlCommand fan ? fan.Plan.Strategy switch
        {
            "Fixed" => new[] { "strategy", "fixedRpm" },
            "Auto" => ["strategy", "maximumRpm"],
            _ => fan.Plan.GpuPoints is null ? ["strategy", "cpu"] : ["strategy", "cpu", "gpu"]
        } : CommandFields(command);
        if (command is SetFanControlCommand { Plan.MaximumRpm: not null, Plan.Strategy: not "Auto" })
            fields = [.. fields, "maximumRpm"];
        foreach (string field in fields)
        {
            var capability = current.Capabilities.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
            // Legacy IPC must honor the same device evidence as compiled page requests.
            if (!PresetValidation.IsVerifiedWritable(capability, Execution.HardwareEpoch)) return false;
            if (field is "temperatureLimitC" or "splWatts" or "spptWatts" &&
                (capability.NativeModeSideEffect != "PreservesSelectedMode" || current.Capabilities.IsOnAcPower != true ||
                 capability.Minimum is null || capability.Maximum is null)) return false;
            if (intended.TryGetValue(field, out var request) && request.Value is { ValueKind: System.Text.Json.JsonValueKind.Number } value &&
                (!value.TryGetDouble(out double number) || !PresetValidation.WithinRange(number, capability))) return false;
        }
        return true;
    }
    private bool FieldReadable(string field, HomeControlState? controls = null)
    {
        controls ??= State.Controls;
        var capability = State.Capabilities.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (capability is null) return false;
        if (field is "effect" or "speed" && capability.Source.Contains("SoftwareEffectReadback", StringComparison.Ordinal))
        {
            if (capability.Availability != CapabilityState.Available || capability.HardwareEpoch != Execution.HardwareEpoch ||
                controls.KeyboardLighting is not { } light || !light.IsValid()) return false;
            // Elapsed exists only while the provider still owns a Gradient whose live frame matched its last write.
            if (light.Effect == "Gradient") return controls.KeyboardLightingElapsedSeconds is >= 0;
            return field == "speed" ? light.Effect is "Static" or "Fixed" : capability.Readable;
        }
        return capability.Readable;
    }
    private bool RequiresAdvanced(CompiledControlPlan plan) => plan.Pages.Any(page => page.Selection.Page == ControlPageId.Performance &&
        page.Settings.Any(pair => pair.Key is "temperatureLimitC" or "splWatts" or "spptWatts" or "pptWatts" or "mp1TemperatureC" or "tdcCurrentMilliamps" or "edcCurrentMilliamps" or "negativeCurveOptimizer" or "advancedCpuTuning" or "pboScalar" &&
            (pair.Value.Intent == SettingIntent.Set || ownedFields.TryGetValue(FieldKey(page.Selection.Page, pair.Key), out var owner) &&
                owner.Epoch == Execution.HardwareEpoch && owner.State == ApplyState.Confirmed && (owner.Baseline is not null || owner.BaselineDetails is not null))));
    private static bool HasAdvanced(HardwareCommand command) => command switch
    { SetCpuTuningCommand cpu => CpuFields(cpu.Plan).Any(field => field is "temperatureLimitC" or "splWatts" or "spptWatts" or "pptWatts" or "mp1TemperatureC" or "tdcCurrentMilliamps" or "edcCurrentMilliamps" or "negativeCurveOptimizer" or "advancedCpuTuning" or "pboScalar"),
      SetCpuTuningBatchCommand batch => batch.Plans.Any(plan => HasAdvanced(new SetCpuTuningCommand(Guid.Empty, plan, true))), _ => false };
    private static Dictionary<string, SettingRequest> IntendedValues(HardwareCommand command, HomeControlState? controls = null)
    {
        var values = new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase);
        void Add(string field, object? value) { if (value is not null) values[field] = new(SettingIntent.Set, System.Text.Json.JsonSerializer.SerializeToElement(value, ControlPlanCompiler.Json)); }
        if (command is SetCpuTuningCommand cpu) command = new SetCpuTuningBatchCommand(command.OperationId, [cpu.Plan], true);
        if (command is SetCpuTuningBatchCommand batch) foreach (var plan in batch.Plans)
        { Add("temperatureLimitC", plan.TemperatureLimitC); Add("splWatts", plan.SplWatts); Add("spptWatts", plan.SpptWatts);
          Add("acMaxFrequencyMhz", plan.AcMaxFrequencyMhz ?? plan.MaxFrequencyMhz); Add("dcMaxFrequencyMhz", plan.DcMaxFrequencyMhz ?? plan.MaxFrequencyMhz);
          Add("isBoostEnabled", (object?)plan.BoostPolicy ?? plan.BoostEnabled); Add("windowsPowerSchemeId", plan.WindowsPowerSchemeId);
          Add("acMinActiveCoresPercent", plan.AcMinActiveCoresPercent); Add("dcMinActiveCoresPercent", plan.DcMinActiveCoresPercent);
          Add("negativeCurveOptimizer", (object?)plan.NegativeCurveOptimizer ?? (object?)plan.Advanced?.CurveOptimizerAll ?? plan.Advanced?.PerCoreCurveOptimizer);
          Add("pptWatts", plan.Advanced?.PptWatts); Add("mp1TemperatureC", plan.Advanced?.Mp1TemperatureC); Add("tdcCurrentMilliamps", plan.Advanced?.TdcCurrentMilliamps); Add("edcCurrentMilliamps", plan.Advanced?.EdcCurrentMilliamps);
          Add("advancedCpuTuning", ControlPlanCompiler.ResidualSmu(plan.Advanced)); Add("pboScalar", plan.Advanced?.PboScalar); }
        switch (command)
        { case SetGpuFrequencyLimitCommand limit: Add("coreFrequencyLimitMhz", limit.Megahertz); break;
          case SetGpuCoreOffsetCommand core:
              Add("coreOffsetKhz", core.OffsetKhz);
              var expectedVfTable = core.ExpectedVfOffsetsKhz ?? controls?.GpuVf?.Nodes.Select(node => node.OffsetKhz).ToArray();
              if (expectedVfTable is { Length: 127 }) Add("vfOffsetsKhz", expectedVfTable.Select((value, index) => index == 0 ? value : checked(value + core.OffsetKhz - core.ExpectedOffsetKhz)).ToArray());
              break;
          case SetGpuMemoryOffsetCommand memory: Add("memoryOffsetKhz", memory.OffsetKhz); break;
          case SetGpuVfCurveCommand curve: Add("vfOffsetsKhz", curve.OffsetsKhz); break;
          case SetStrongCoolingCommand cooling: Add("strongCooling", cooling.Enabled); break;
          case SetFanControlCommand fan:
              Add("strategy", fan.Plan.Strategy); Add("maximumRpm", fan.Plan.MaximumRpm); Add("fixedRpm", fan.Plan.FixedRpm);
              Add("strongCooling", false);
              Add("cpu", fan.Plan.Points.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray());
              if (fan.Plan.GpuPoints is { } gpuPoints)
                  Add("gpu", gpuPoints.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray());
              break;
          case ReleaseFanControlCommand: Add("strategy", "Auto"); break;
          case SetKeyboardLightingCommand light:
              var canonicalLight = ControlPlanCompiler.CanonicalLighting(light.Plan);
              Add("effect", canonicalLight.Effect); Add("brightness", light.Plan.BrightnessLevel); Add("speed", canonicalLight.Speed); Add("logo", light.Plan.LogoEnabled);
              if (light.Plan.Red is not null && light.Plan.Green is not null && light.Plan.Blue is not null)
                  Add("hex", $"#{light.Plan.Red:X2}{light.Plan.Green:X2}{light.Plan.Blue:X2}");
              break;
          case SetLidLogoCommand logo: Add("logo", logo.Enabled); break; }
        return values;
    }
    private sealed record OwnedField(Guid OperationId, ControlOrigin Origin, SettingRequest? Baseline, SettingRequest? LastWritten,
        ApplyState State, long Epoch, Guid? Scheme, long Generation,
        CpuFieldRestorationValue? BaselineDetails = null, CpuFieldRestorationValue? LastDetails = null)
    {
        public string? OwnerSid { get; init; }
        public Guid? ClientSessionId { get; init; }
        public SettingRequest? WriteBefore { get; init; }
        public CpuFieldRestorationValue? WriteBeforeDetails { get; init; }
        public CpuFieldRestorationValue? WriteTargetDetails { get; init; }
    }

    internal static CpuFieldRestorationValue? CaptureCpuRestoration(string field, CpuTuningState? cpu) => field switch
    {
        "isBoostEnabled" when cpu?.AcBoostMode is >= 0 and <= 6 && cpu.DcBoostMode is >= 0 and <= 6 => new(cpu.AcBoostMode, cpu.DcBoostMode, null),
        "negativeCurveOptimizer" when cpu?.CurveOptimizerVerification == "hardwareReadback" && cpu.HasCompleteCurveValues() =>
            new(null, null, new(cpu.PerCoreCurveOptimizer!)),
        _ => null
    };
    internal static bool SameRestoration(CpuFieldRestorationValue? left, CpuFieldRestorationValue? right) => left is not null && right is not null &&
        left.AcBoostMode == right.AcBoostMode && left.DcBoostMode == right.DcBoostMode &&
        (left.PerCoreCurveOptimizer is null ? right.PerCoreCurveOptimizer is null : right.PerCoreCurveOptimizer is { } cores &&
            cores.Count == left.PerCoreCurveOptimizer.Count && left.PerCoreCurveOptimizer.All(pair => cores.TryGetValue(pair.Key, out int value) && value == pair.Value));
    private static bool MatchesDetails(OwnedField owner, CpuTuningState? cpu) => owner.LastDetails is null ||
        SameRestoration(owner.LastDetails, CaptureCpuRestoration(owner.LastDetails.PerCoreCurveOptimizer is null ? "isBoostEnabled" : "negativeCurveOptimizer", cpu));
    private static bool MatchesOwnedValue(OwnedField owner, SettingRequest? current, CpuTuningState? cpu) =>
        owner.LastDetails is not null ? MatchesDetails(owner, cpu) : ControlPlanCompiler.Same(current, owner.LastWritten);
    private static bool FieldMatches(string field, SettingRequest? current, SettingRequest intended, CpuTuningState? cpu)
    {
        if (field == "isBoostEnabled" && intended.Value is { ValueKind: System.Text.Json.JsonValueKind.Object } policy)
            return CpuBoostPolicy.TryRead(policy, out var target) && CpuBoostPolicy.FromState(cpu) == target;
        if (field == "isBoostEnabled" && intended.Value is { ValueKind: System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False } enabled &&
            (cpu?.AcBoostMode is not null || cpu?.DcBoostMode is not null))
            return CpuBoostPolicy.FromState(cpu)?.MatchesToggle(enabled.GetBoolean()) == true;
        if (field != "negativeCurveOptimizer" || intended.Value?.ValueKind != System.Text.Json.JsonValueKind.Object)
            return current is not null && ControlPlanCompiler.Same(current, intended);
        var values = intended.Value.Value.Deserialize<Dictionary<int, int>>(ControlPlanCompiler.Json);
        return values is { Count: > 0 } && cpu?.HasCompleteCurveValues() == true &&
            values.All(pair => cpu.PerCoreCurveOptimizer!.TryGetValue(pair.Key, out int value) && value == pair.Value);
    }
    private static IEnumerable<string> SpecialRestores(CompiledControlPage page, Dictionary<string, SettingRequest> values) =>
        page.Selection.Page == ControlPageId.Performance ? values.Keys.Where(field => (IsDirectCpuScalar(field) || field is "isBoostEnabled" or "negativeCurveOptimizer") &&
            page.Settings[field].Intent == SettingIntent.Official) : [];
    private static Dictionary<string, SettingRequest> NormalValues(CompiledControlPage page, Dictionary<string, SettingRequest> values) =>
        values.Where(pair => !SpecialRestores(page, values).Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    private static void ValidateRestorableTargets(CompiledControlPage page, HomeControlState controls)
    {
        if (page.Selection.Page == ControlPageId.Fan &&
            page.Settings.TryGetValue("strongCooling", out var cooling) && cooling.Intent == SettingIntent.Set &&
            cooling.Value?.ValueKind == System.Text.Json.JsonValueKind.True &&
            page.Settings.Any(pair => !pair.Key.Equals("strongCooling", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals("profile", StringComparison.OrdinalIgnoreCase) && pair.Value.Intent != SettingIntent.Keep))
            throw new InvalidDataException("strongCoolingConflictsWithFanPlan");
        if (page.Selection.Page != ControlPageId.Performance) return;
        foreach (var (field, setting) in page.Settings)
        {
            if (setting.Intent == SettingIntent.Set && field is "isBoostEnabled" or "negativeCurveOptimizer" && CaptureCpuRestoration(field, controls.CpuTuning) is null)
                throw new InvalidDataException("cpuExactBaselineUnreadable:" + field);
            if (field == "negativeCurveOptimizer" && setting.Intent == SettingIntent.Set && setting.Value?.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var values = setting.Value.Value.Deserialize<Dictionary<int, int>>(ControlPlanCompiler.Json);
                if (values is not { Count: > 0 } || controls.CpuTuning?.EnabledCoreCount is not int count ||
                    values.Any(pair => pair.Key < 0 || pair.Key >= count || pair.Value is < -50 or > 0))
                    throw new InvalidDataException("perCoreTopologyOrValueInvalid");
            }
        }
    }
}
