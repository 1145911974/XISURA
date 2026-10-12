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
            controls = await ReadControlBaselineForExecutionAsync(token, RequiresAdvanced(plan));
            // Validate every enabled page before the first mode/EC write.
            foreach (var page in plan.Pages)
            {
                ValidateRestorableTargets(page, controls);
                ControlPlanCompiler.Commands(page.Selection.Page, NormalValues(page,
                    ResolveSettings(page, controls, command.OperationId, false).Values), controls);
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
        CommandResult? modeResult = null;
        bool changed = false;
        if (!await FreshAsync()) return Rejected(command, ErrorCode.CancelledBeforeApply);
        bool nativeConfirmed = CanReuseNativeConfirmation(request.NativeMode, controls);
        if (applyNativeMode && !nativeConfirmed)
        {
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
        }
        controls = await ReadControlBaselineForExecutionAsync(token, RequiresAdvanced(plan));
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
                    ownedFields[key] = owner with { OperationId = command.OperationId, State = ApplyState.Unknown };
                    Volatile.Write(ref controlExecutionState, new(admission.Generation, Execution.HardwareEpoch, plan.CatalogRevision,
                        request.LogicalMode, request.NativeMode, [], SnapshotFields()) { NativeModeApplyState = nativeConfirmed ? ApplyState.Confirmed : ApplyState.SubmittedUnverified });
                    await PersistPendingOwnershipAsync(command.OperationId, new Dictionary<string, OwnedField?> { [key] = owner }, token);
                    var restored = await TrackHardwareDispatchAsync(() =>
                        provider.RestoreCpuFieldAsync(command.OperationId, field, owner.BaselineDetails!, owner.LastDetails!, token));
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
                foreach (var next in commands)
                {
                    if (!await FreshAsync()) break;
                    var fields = CommandFields(next);
                    var before = ControlPlanCompiler.Read(page.Selection.Page, controls);
                    var beforeCpu = controls.CpuTuning;
                    Guid? baselineScheme = controls.CpuTuning?.WindowsPowerSchemeId;
                    var previousOwnership = new Dictionary<string, OwnedField?>();
                    foreach (string field in fields.Where(resolved.Values.ContainsKey))
                    {
                        string key = FieldKey(page.Selection.Page, field);
                        ownedFields.TryGetValue(key, out var previous);
                        previousOwnership[key] = previous;
                        bool retainBaseline = request.Origin != ControlOrigin.TurboBranch || previous?.Origin == ControlOrigin.TurboBranch;
                        var baseline = retainBaseline && previous?.OwnerSid == admission.Owner && previous.Epoch == Execution.HardwareEpoch && previous.Scheme == baselineScheme &&
                            MatchesOwnedValue(previous, before.GetValueOrDefault(field), controls.CpuTuning) ? previous.Baseline : before.GetValueOrDefault(field);
                        ownedFields[key] = new(command.OperationId, request.Origin, baseline, resolved.Values[field], ApplyState.Unknown,
                            Execution.HardwareEpoch, baselineScheme, admission.Generation,
                            retainBaseline && previous?.OwnerSid == admission.Owner && previous.Epoch == Execution.HardwareEpoch && previous.Scheme == baselineScheme && MatchesDetails(previous, controls.CpuTuning)
                                ? previous.BaselineDetails : CaptureCpuRestoration(field, controls.CpuTuning)) { OwnerSid = admission.Owner };
                    }
                    Volatile.Write(ref controlExecutionState, new(admission.Generation, Execution.HardwareEpoch, plan.CatalogRevision,
                        request.LogicalMode, request.NativeMode, pages.Append(new(page.Selection.Page, page.Selection.Key, page.Selection.Revision,
                            ApplyState.Unknown, confirmed.ToArray(), unverified.ToArray(), skipped.ToArray(), null)).ToArray(), SnapshotFields())
                        { NativeModeApplyState = nativeConfirmed ? ApplyState.Confirmed : ApplyState.SubmittedUnverified });
                    await PersistPendingOwnershipAsync(command.OperationId, previousOwnership, token);
                    var result = await ExecuteHardwareDirectAsync(next, token, connectionId);
                    changed |= result.Mutation != HardwareMutation.NoMutation;
                    controls = await ReadControlBaselineForExecutionAsync(token, RequiresAdvanced(plan));
                    var after = ControlPlanCompiler.Read(page.Selection.Page, controls);
                    foreach (var field in fields.Where(resolved.Values.ContainsKey))
                    {
                        var intended = resolved.Values[field];
                        bool readback = FieldReadable(field) && FieldMatches(field, after.GetValueOrDefault(field), intended, controls.CpuTuning);
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
                            var baseline = retainBaseline && previous?.OwnerSid == admission.Owner && previous.Epoch == Execution.HardwareEpoch && previous.Scheme == baselineScheme &&
                                MatchesOwnedValue(previous, before.GetValueOrDefault(field), beforeCpu)
                                ? previous.Baseline : before.GetValueOrDefault(field);
                            ownedFields[key] = new(command.OperationId, request.Origin, baseline, intended, writeState,
                                Execution.HardwareEpoch, baselineScheme, admission.Generation,
                                ownedFields.GetValueOrDefault(key)?.BaselineDetails, CaptureCpuRestoration(field, controls.CpuTuning)) { OwnerSid = admission.Owner };
                        }
                        if (readback && page.Settings[field].Intent == SettingIntent.Official && request.Origin != ControlOrigin.TurboBranch) ownedFields.Remove(key);
                        if (writeState == ApplyState.Confirmed) confirmed.Add(field);
                        else if (writeState is ApplyState.SubmittedUnverified or ApplyState.Unknown) unverified.Add(field);
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
        }
        var executionState = new ControlExecutionState(admission.Generation, Execution.HardwareEpoch, plan.CatalogRevision,
            request.LogicalMode, request.NativeMode, pages.ToArray(), SnapshotFields())
        { NativeModeApplyState = nativeConfirmed ? ApplyState.Confirmed : ApplyState.SubmittedUnverified };
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
            bool matches = owned is not null && owned.Epoch == Execution.HardwareEpoch && owned.Scheme == controls.CpuTuning?.WindowsPowerSchemeId &&
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
                values[field] = owned.Baseline ?? owned.LastWritten!;
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
        if (command is SetPerformanceModeCommand modeCommand)
        {
            SetPendingLegacy(command, modeCommand.Mode);
            await PersistExecutionAsync(command.OperationId, true, token);
            var modeResult = await ExecuteHardwareDirectAsync(command, token, connectionId);
            EnsureControlHardwareAccess();
            lastModeConfirmationTimestamp = modeResult.VerifiedPerformanceMode == modeCommand.Mode ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (modeResult is { State: CommandState.Applied, Error: null })
            { lastSubmittedMode = modeCommand.Mode; lastModeEpoch = Execution.HardwareEpoch; }
            Volatile.Write(ref controlExecutionState, controlExecutionState! with
            { NativeModeApplyState = modeResult.VerifiedPerformanceMode == modeCommand.Mode ? ApplyState.Confirmed :
                modeResult.Mutation == HardwareMutation.NoMutation && modeResult.Error is not null ? ApplyState.Failed :
                modeResult.Error is not null ? ApplyState.Unknown : ApplyState.SubmittedUnverified });
            return modeResult;
        }
        var page = CommandPage(command);
        var beforeControls = await ReadControlBaselineForExecutionAsync(token, HasAdvanced(command));
        var before = ControlPlanCompiler.Read(page, beforeControls);
        var intended = IntendedValues(command);
        foreach (string field in intended.Keys)
            if (field is "isBoostEnabled" or "negativeCurveOptimizer" && CaptureCpuRestoration(field, beforeControls.CpuTuning) is null)
                return Rejected(command, ErrorCode.HardwareReadFailed);
        var previousOwnership = new Dictionary<string, OwnedField?>();
        foreach (string field in CommandFields(command))
        {
            string key = FieldKey(page, field);
            ownedFields.TryGetValue(key, out var previous);
            previousOwnership[key] = previous;
            var baseline = previous?.OwnerSid == Execution.CurrentAdmission!.Owner && previous.Epoch == Execution.HardwareEpoch && previous.Scheme == beforeControls.CpuTuning?.WindowsPowerSchemeId
                && MatchesOwnedValue(previous, before.GetValueOrDefault(field), beforeControls.CpuTuning) ? previous.Baseline : before.GetValueOrDefault(field);
            ownedFields[key] = new(command.OperationId, ControlOrigin.ManualLive, baseline, intended.GetValueOrDefault(field),
                ApplyState.Unknown, Execution.HardwareEpoch, beforeControls.CpuTuning?.WindowsPowerSchemeId, Execution.CurrentAdmission!.Generation,
                previous?.OwnerSid == Execution.CurrentAdmission!.Owner && previous.Epoch == Execution.HardwareEpoch && MatchesDetails(previous, beforeControls.CpuTuning)
                    ? previous.BaselineDetails : CaptureCpuRestoration(field, beforeControls.CpuTuning)) { OwnerSid = Execution.CurrentAdmission!.Owner };
        }
        SetPendingLegacy(command, beforeControls.PerformanceMode);
        await PersistExecutionAsync(command.OperationId, true, token);
        var result = await ExecuteHardwareDirectAsync(command, token, connectionId);
        EnsureControlHardwareAccess();
        var afterControls = State.Controls;
        var after = ControlPlanCompiler.Read(page, afterControls);
        foreach (string field in CommandFields(command))
        {
            string key = FieldKey(page, field);
            if (result.Mutation == HardwareMutation.NoMutation || result.State == CommandState.RolledBack)
            {
                if (previousOwnership[key] is { } prior) ownedFields[key] = prior; else ownedFields.Remove(key);
                continue;
            }
            ownedFields[key] = ownedFields[key] with { LastDetails = CaptureCpuRestoration(field, afterControls.CpuTuning), State =
                result.State == CommandState.RecoveryRequired ? ApplyState.Unknown : FieldReadable(field) && result.Error is null && result.State == CommandState.Applied &&
                intended.TryGetValue(field, out var desired) && FieldMatches(field, after.GetValueOrDefault(field), desired, afterControls.CpuTuning)
                ? ApplyState.Confirmed : result.Error is not null ? ApplyState.Unknown : ApplyState.SubmittedUnverified };
        }
        Volatile.Write(ref controlExecutionState, controlExecutionState! with { Fields = SnapshotFields(),
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
        { BaselineDetails = pair.Value.BaselineDetails, LastWrittenDetails = pair.Value.LastDetails });
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
            if (confirmed.ModeEvidence?.ParsedMode != observed || observed == lastSubmittedMode) return false;
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
                        !ControlPlanCompiler.Same(current[field], owner.LastWritten)) ownedFields.Remove(key);
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
        SetStrongCoolingCommand => ["strongCooling"], SetFanControlCommand or ReleaseFanControlCommand => ["strategy", "maximumRpm", "fixedRpm", "cpu", "gpu"],
        SetKeyboardLightingCommand => ["effect", "brightness", "hex", "speed", "logo"], SetLidLogoCommand => ["logo"], _ => []
    };
    private static string[] CpuFields(CpuTuningPlan plan)
    {
        var values = new Dictionary<string, object?> { ["temperatureLimitC"] = plan.TemperatureLimitC, ["splWatts"] = plan.SplWatts,
            ["spptWatts"] = plan.SpptWatts, ["acMaxFrequencyMhz"] = plan.AcMaxFrequencyMhz ?? plan.MaxFrequencyMhz,
            ["dcMaxFrequencyMhz"] = plan.DcMaxFrequencyMhz ?? plan.MaxFrequencyMhz, ["isBoostEnabled"] = plan.BoostEnabled,
            ["windowsPowerSchemeId"] = plan.WindowsPowerSchemeId, ["acMinActiveCoresPercent"] = plan.AcMinActiveCoresPercent,
            ["dcMinActiveCoresPercent"] = plan.DcMinActiveCoresPercent,
            ["negativeCurveOptimizer"] = (object?)plan.NegativeCurveOptimizer ?? (object?)plan.Advanced?.CurveOptimizerAll ?? plan.Advanced?.PerCoreCurveOptimizer,
            ["advancedCpuTuning"] = ControlPlanCompiler.SmuOnly(plan.Advanced), ["pboScalar"] = plan.Advanced?.PboScalar };
        return values.Where(pair => pair.Value is not null).Select(pair => pair.Key).ToArray();
    }
    private bool FieldReadable(string field) => State.Capabilities.Fields.Any(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase) && item.Readable);
    private bool RequiresAdvanced(CompiledControlPlan plan) => plan.Pages.Any(page => page.Selection.Page == ControlPageId.Performance &&
        page.Settings.Any(pair => pair.Key is "temperatureLimitC" or "splWatts" or "spptWatts" or "negativeCurveOptimizer" or "advancedCpuTuning" or "pboScalar" &&
            (pair.Value.Intent == SettingIntent.Set || ownedFields.TryGetValue(FieldKey(page.Selection.Page, pair.Key), out var owner) &&
                owner.Epoch == Execution.HardwareEpoch && owner.State == ApplyState.Confirmed && (owner.Baseline is not null || owner.BaselineDetails is not null))));
    private static bool HasAdvanced(HardwareCommand command) => command switch
    { SetCpuTuningCommand cpu => CpuFields(cpu.Plan).Any(field => field is "temperatureLimitC" or "splWatts" or "spptWatts" or "negativeCurveOptimizer" or "advancedCpuTuning" or "pboScalar"),
      SetCpuTuningBatchCommand batch => batch.Plans.Any(plan => HasAdvanced(new SetCpuTuningCommand(Guid.Empty, plan, true))), _ => false };
    private static Dictionary<string, SettingRequest> IntendedValues(HardwareCommand command)
    {
        var values = new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase);
        void Add(string field, object? value) { if (value is not null) values[field] = new(SettingIntent.Set, System.Text.Json.JsonSerializer.SerializeToElement(value, ControlPlanCompiler.Json)); }
        if (command is SetCpuTuningCommand cpu) command = new SetCpuTuningBatchCommand(command.OperationId, [cpu.Plan], true);
        if (command is SetCpuTuningBatchCommand batch) foreach (var plan in batch.Plans)
        { Add("temperatureLimitC", plan.TemperatureLimitC); Add("splWatts", plan.SplWatts); Add("spptWatts", plan.SpptWatts);
          Add("acMaxFrequencyMhz", plan.AcMaxFrequencyMhz ?? plan.MaxFrequencyMhz); Add("dcMaxFrequencyMhz", plan.DcMaxFrequencyMhz ?? plan.MaxFrequencyMhz);
          Add("isBoostEnabled", plan.BoostEnabled); Add("windowsPowerSchemeId", plan.WindowsPowerSchemeId);
          Add("acMinActiveCoresPercent", plan.AcMinActiveCoresPercent); Add("dcMinActiveCoresPercent", plan.DcMinActiveCoresPercent);
          Add("negativeCurveOptimizer", (object?)plan.NegativeCurveOptimizer ?? (object?)plan.Advanced?.CurveOptimizerAll ?? plan.Advanced?.PerCoreCurveOptimizer);
          Add("advancedCpuTuning", ControlPlanCompiler.SmuOnly(plan.Advanced)); Add("pboScalar", plan.Advanced?.PboScalar); }
        switch (command)
        { case SetGpuFrequencyLimitCommand limit: Add("coreFrequencyLimitMhz", limit.Megahertz); break;
          case SetGpuCoreOffsetCommand core: Add("coreOffsetKhz", core.OffsetKhz); break;
          case SetGpuMemoryOffsetCommand memory: Add("memoryOffsetKhz", memory.OffsetKhz); break;
          case SetGpuVfCurveCommand curve: Add("vfOffsetsKhz", curve.OffsetsKhz); break;
          case SetStrongCoolingCommand cooling: Add("strongCooling", cooling.Enabled); break;
          case SetFanControlCommand fan: Add("strategy", fan.Plan.Strategy); Add("maximumRpm", fan.Plan.MaximumRpm); Add("fixedRpm", fan.Plan.FixedRpm); break;
          case ReleaseFanControlCommand: Add("strategy", "Auto"); break;
          case SetKeyboardLightingCommand light: Add("effect", light.Plan.Effect); Add("brightness", light.Plan.BrightnessLevel); Add("speed", light.Plan.Speed); Add("logo", light.Plan.LogoEnabled); break;
          case SetLidLogoCommand logo: Add("logo", logo.Enabled); break; }
        return values;
    }
    private sealed record OwnedField(Guid OperationId, ControlOrigin Origin, SettingRequest? Baseline, SettingRequest? LastWritten,
        ApplyState State, long Epoch, Guid? Scheme, long Generation,
        CpuFieldRestorationValue? BaselineDetails = null, CpuFieldRestorationValue? LastDetails = null)
    {
        public string? OwnerSid { get; init; }
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
        if (field != "negativeCurveOptimizer" || intended.Value?.ValueKind != System.Text.Json.JsonValueKind.Object)
            return current is not null && ControlPlanCompiler.Same(current, intended);
        var values = intended.Value.Value.Deserialize<Dictionary<int, int>>(ControlPlanCompiler.Json);
        return values is { Count: > 0 } && cpu?.HasCompleteCurveValues() == true &&
            values.All(pair => cpu.PerCoreCurveOptimizer!.TryGetValue(pair.Key, out int value) && value == pair.Value);
    }
    private static IEnumerable<string> SpecialRestores(CompiledControlPage page, Dictionary<string, SettingRequest> values) =>
        page.Selection.Page == ControlPageId.Performance ? values.Keys.Where(field => field is "isBoostEnabled" or "negativeCurveOptimizer" &&
            page.Settings[field].Intent == SettingIntent.Official) : [];
    private static Dictionary<string, SettingRequest> NormalValues(CompiledControlPage page, Dictionary<string, SettingRequest> values) =>
        values.Where(pair => !SpecialRestores(page, values).Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    private static void ValidateRestorableTargets(CompiledControlPage page, HomeControlState controls)
    {
        if (page.Selection.Page != ControlPageId.Performance) return;
        foreach (var (field, setting) in page.Settings)
        {
            if (setting.Intent == SettingIntent.Set && field is "isBoostEnabled" or "negativeCurveOptimizer" && CaptureCpuRestoration(field, controls.CpuTuning) is null)
                throw new InvalidDataException("cpuExactBaselineUnreadable:" + field);
            if (field == "negativeCurveOptimizer" && setting.Intent == SettingIntent.Set && setting.Value?.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var values = setting.Value.Value.Deserialize<Dictionary<int, int>>(ControlPlanCompiler.Json);
                if (values is not { Count: > 0 } || controls.CpuTuning?.EnabledCoreCount is not int count ||
                    values.Any(pair => pair.Key < 0 || pair.Key >= count || pair.Value is < -30 or > 0))
                    throw new InvalidDataException("perCoreTopologyOrValueInvalid");
            }
        }
    }
}
