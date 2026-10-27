using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private readonly Dictionary<Guid, TurboBranchLease> turboBranches = new();
    private readonly Queue<Guid> completedTurboBranches = new();
    private readonly Dictionary<Guid, string> completedTurboBranchesByOwner = new();
    private sealed record TurboBranchLease(string Owner, long Epoch, Guid? Scheme, long Generation,
        PerformanceMode? NativeBaseline, FanControlPlan? FanBaseline, bool? StrongBaseline,
        Dictionary<string, OwnedField> PreviousOwners, long? ReleaseGeneration = null,
        bool NativeRestoreRetryable = false, bool FieldRestoreRetryable = false,
        Dictionary<string, OwnedField>? UnverifiedRestores = null);

    private async Task<CommandResult> ApplyTurboBranchAsync(ApplyTurboBranchCommand command, CancellationToken token, Guid connectionId)
    {
        var admission = Execution.CurrentAdmission!;
        var hardware = await GetStateAsync(token);
        var controls = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
        var capabilities = hardware.Capabilities;
        bool supportedProfile = hardware.Identity.CpuModel.Contains("7745HX", StringComparison.OrdinalIgnoreCase);
        var performance = new Dictionary<string, SettingRequest>();
        var skipped = new List<string>();
        bool Writable(string field) => capabilities.Fields.Any(item => item.Key == field && item.Availability == CapabilityState.Available &&
            item.Writable && item.Source != "uiSuggestion" && item.HardwareEpoch == Execution.HardwareEpoch && item.VerifiedAtUtc is not null);
        void Oem(string field, int value)
        {
            var capability = capabilities.Fields.FirstOrDefault(item => item.Key == field);
            if (supportedProfile && capabilities.IsOnAcPower == true && Writable(field) &&
                capability is { Readable: true, NativeModeSideEffect: "PreservesSelectedMode", Minimum: not null, Maximum: not null } &&
                ControlPlanCompiler.Read(ControlPageId.Performance, controls).GetValueOrDefault(field)?.Value is { } baseline &&
                baseline.TryGetInt32(out int original) && PresetValidation.WithinRange(original, capability) &&
                PresetValidation.WithinRange(value, capability)) performance[field] = SettingRequest.Set(value);
            else skipped.Add(field);
        }
        bool quiet = command.Tier == TurboBranchTier.Quiet;
        Oem("temperatureLimitC", quiet ? 85 : 95); Oem("splWatts", quiet ? 45 : 75); Oem("spptWatts", quiet ? 55 : 75);
        performance["acMaxFrequencyMhz"] = supportedProfile && quiet ? SettingRequest.Set(4500) : SettingRequest.Official;
        performance["dcMaxFrequencyMhz"] = supportedProfile ? SettingRequest.Set(quiet ? 3300 : 3800) : SettingRequest.Official;
        performance["isBoostEnabled"] = SettingRequest.Set(true);
        performance["advancedCpuTuning"] = SettingRequest.Keep;
        foreach (string field in new[] { "acMinActiveCoresPercent", "dcMinActiveCoresPercent", "windowsPowerSchemeId", "pptWatts", "mp1TemperatureC" })
            performance[field] = SettingRequest.Keep;
        var fan = new Dictionary<string, SettingRequest>();
        var fanSkipped = new List<string>();
        string fanField = quiet ? "strategy" : "strongCooling";
        if (Writable(fanField)) fan[fanField] = quiet ? SettingRequest.Set("Auto") : SettingRequest.Set(true);
        else fanSkipped.Add(fanField);
        if (quiet && Writable("strongCooling")) fan["strongCooling"] = SettingRequest.Set(false);
        var pages = new[]
        {
            BranchPage(ControlPageId.Performance, performance, skipped.ToArray()),
            BranchPage(ControlPageId.Gpu, new() { ["coreFrequencyLimitMhz"] = SettingRequest.Official }),
            BranchPage(ControlPageId.Fan, fan, fanSkipped.ToArray())
        };
        CompiledControlPlan plan;
        try { plan = ControlPlanCompiler.CompileTransient(pages, Execution.Catalog.Revision(admission.Owner), capabilities, Execution.HardwareEpoch); }
        catch (InvalidDataException) { return Rejected(command, ErrorCode.ValidationFailed); }
        if (turboBranches.Count >= 256) return Rejected(command, ErrorCode.CommandInProgress);
        turboBranches[command.OperationId] = new(admission.Owner, Execution.HardwareEpoch, controls.CpuTuning?.WindowsPowerSchemeId,
            admission.Generation, controls.PerformanceMode is { } original && CanReuseNativeConfirmation(original, controls) ? original : null,
            controls.ActiveFanControlPlan, controls.StrongCooling, new(ownedFields, StringComparer.OrdinalIgnoreCase));
        var result = await ExecuteControlRequestAsync(BranchRequest(command.OperationId, PerformanceMode.Turbo, plan), token, connectionId, plan);
        if (result.Mutation == HardwareMutation.NoMutation && !ownedFields.Values.Any(field => field.OperationId == command.OperationId))
            turboBranches.Remove(command.OperationId);
        return result;
    }

    private async Task<CommandResult> ReleaseTurboBranchAsync(ReleaseTurboBranchCommand command, CancellationToken token, Guid connectionId)
    {
        var admission = Execution.CurrentAdmission!;
        if (!turboBranches.TryGetValue(command.BranchOperationId, out var lease))
        {
            if (completedTurboBranchesByOwner.TryGetValue(command.BranchOperationId, out var completedOwner))
                return completedOwner == admission.Owner
                    ? new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.NoMutation }
                    : Rejected(command, ErrorCode.UnauthorizedClient);
            return Rejected(command, ErrorCode.ConflictDetected) with { State = CommandState.RecoveryRequired, Mutation = HardwareMutation.NoMutation };
        }
        if (lease.Owner != admission.Owner) return Rejected(command, ErrorCode.UnauthorizedClient);
        var hardware = await GetStateAsync(token);
        var controls = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
        var beforeExecution = controlExecutionState;
        bool contextMatches = lease.Epoch == Execution.HardwareEpoch && lease.Scheme == controls.CpuTuning?.WindowsPowerSchemeId;
        bool retryRelease = lease.ReleaseGeneration is not null;
        if (retryRelease)
        {
            bool retryable = lease.FieldRestoreRetryable
                ? beforeExecution?.NativeModeApplyState != ApplyState.Unknown
                : lease.NativeRestoreRetryable && beforeExecution?.NativeModeApplyState == ApplyState.Failed &&
                    beforeExecution.Pages.All(page => page.State == ApplyState.Confirmed);
            if (!retryable || !contextMatches || beforeExecution is null || beforeExecution.Generation != lease.ReleaseGeneration ||
                admission.Generation != lease.ReleaseGeneration + 1 || !Execution.IsCurrent(admission.Generation) ||
                controls.PerformanceMode != PerformanceMode.Turbo)
                return Rejected(command, ErrorCode.ConflictDetected) with
                    { State = CommandState.RecoveryRequired, ExecutionState = beforeExecution };
            // A refused read-only retry still belongs to this lease; never change its original field generation/baselines.
            turboBranches[command.BranchOperationId] = lease = lease with { ReleaseGeneration = admission.Generation };
            Volatile.Write(ref controlExecutionState, beforeExecution = beforeExecution with { Generation = admission.Generation });
            bool observed;
            try
            {
                observed = await CanRetryTurboNativeRestoreAsync(lease, admission.Generation, token);
                if (observed && lease.UnverifiedRestores is { Count: > 0 })
                    observed = await ConfirmTurboRestoreReadbackAsync(lease, admission.Generation, token);
            }
            catch (Exception error) when (error is IOException or TimeoutException) { observed = false; }
            if (!observed)
                return Rejected(command, ErrorCode.ReadBackMismatch) with
                    { State = CommandState.RecoveryRequired, ExecutionState = beforeExecution };
            turboBranches[command.BranchOperationId] = lease = lease with { UnverifiedRestores = null };
        }
        bool restoreNative = contextMatches && beforeExecution?.Generation == lease.Generation &&
            beforeExecution.NativeModeApplyState == ApplyState.Confirmed && controls.PerformanceMode == PerformanceMode.Turbo &&
            CanReuseNativeConfirmation(PerformanceMode.Turbo, controls) && lease.NativeBaseline is not null;
        restoreNative |= retryRelease && lease.NativeRestoreRetryable;
        var pages = new List<CompiledControlPage>();
        bool unknown = beforeExecution?.Generation == lease.Generation && beforeExecution.NativeModeApplyState == ApplyState.Unknown;
        bool missingOwnedRead = false;
        foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Gpu, ControlPageId.Fan })
        {
            var current = ControlPlanCompiler.Read(page, controls);
            var settings = new Dictionary<string, SettingRequest>();
            var skipped = new List<string>();
            foreach (var pair in ownedFields.Where(pair => pair.Value.OperationId == command.BranchOperationId &&
                         pair.Value.Origin == ControlOrigin.TurboBranch && pair.Key.StartsWith(page.ToString() + ".", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var owner = pair.Value;
                string field = pair.Key[(pair.Key.IndexOf('.') + 1)..];
                var capability = hardware.Capabilities.Fields.FirstOrDefault(item => item.Key == field);
                if (!contextMatches || owner.OwnerSid != admission.Owner || owner.Generation != lease.Generation ||
                    owner.State != ApplyState.Confirmed || capability is not { Availability: CapabilityState.Available, Readable: true, Writable: true, VerifiedAtUtc: not null } ||
                    capability.Source == "uiSuggestion" || capability.HardwareEpoch != Execution.HardwareEpoch ||
                    !MatchesOwnedValue(owner, current.GetValueOrDefault(field), controls.CpuTuning))
                {
                    skipped.Add(field);
                    unknown |= owner.State == ApplyState.Unknown;
                    missingOwnedRead |= contextMatches && owner.OwnerSid == admission.Owner && owner.Generation == lease.Generation &&
                        owner.State == ApplyState.Confirmed && !current.ContainsKey(field);
                    continue;
                }
                if (page == ControlPageId.Fan && field == "strategy" && controls.ActiveFanControlPlan is not null)
                { skipped.Add(field); continue; }
                if (field is "temperatureLimitC" or "splWatts" or "spptWatts" &&
                    (capability.NativeModeSideEffect != "PreservesSelectedMode" || owner.Baseline?.Value is not { } baseline ||
                     !baseline.TryGetDouble(out double value) || !PresetValidation.WithinRange(value, capability)))
                { skipped.Add(field); continue; }
                settings[field] = SettingRequest.Official;
            }
            // Restoring a fan strategy also restores its confirmed curve, never a later curve edit.
            if (page == ControlPageId.Fan && settings.ContainsKey("strategy") && lease.FanBaseline is { } fan)
            {
                var restore = new Dictionary<string, SettingRequest> { ["strategy"] = SettingRequest.Set(fan.Strategy),
                    [fan.GpuPoints is null ? "shared" : "cpu"] = SettingRequest.Set(fan.Points.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray()) };
                if (fan.GpuPoints is { } gpu) restore["gpu"] = SettingRequest.Set(gpu.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray());
                if (fan.FixedRpm is { } fixedRpm) restore["fixedRpm"] = SettingRequest.Set(fixedRpm);
                if (fan.MaximumRpm is { } maximum) restore["maximumRpm"] = SettingRequest.Set(maximum);
                if (lease.StrongBaseline != true && restore.Keys.All(field => hardware.Capabilities.Fields.Any(item => item.Key == field &&
                        item.Availability == CapabilityState.Available && item.Writable && item.Readable && item.HardwareEpoch == Execution.HardwareEpoch)) &&
                    CommandValidation.Validate(new SetFanControlCommand(command.OperationId, fan, true)) is null)
                    foreach (var pair in restore) settings[pair.Key] = pair.Value;
                else { settings.Remove("strategy"); skipped.Add("strategy"); }
            }
            pages.Add(BranchPage(page, settings, skipped.ToArray()));
        }
        var plan = ControlPlanCompiler.CompileTransient(pages, Execution.Catalog.Revision(admission.Owner), hardware.Capabilities, Execution.HardwareEpoch);
        var restoreFields = ownedFields.Where(pair => pair.Value.OperationId == command.BranchOperationId &&
            pair.Value.Origin == ControlOrigin.TurboBranch && pair.Value.State == ApplyState.Confirmed).Select(pair => pair.Key).ToHashSet();
        // Preserve uncertainty even if a field dispatch throws before returning a page result.
        turboBranches[command.BranchOperationId] = lease with
            { ReleaseGeneration = admission.Generation, NativeRestoreRetryable = false, FieldRestoreRetryable = false };
        var result = await ExecuteControlRequestAsync(BranchRequest(command.OperationId, controls.PerformanceMode ?? PerformanceMode.Turbo, plan), token, connectionId, plan, false);
        var unverifiedRestores = ownedFields.Where(pair => restoreFields.Contains(pair.Key) &&
            pair.Value.OperationId == command.OperationId && pair.Value.Origin == ControlOrigin.TurboBranch &&
            pair.Value.State == ApplyState.SubmittedUnverified).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (string key in ownedFields.Where(pair => pair.Value.OperationId == command.OperationId &&
                     pair.Value.Origin == ControlOrigin.TurboBranch && pair.Value.State == ApplyState.Confirmed).Select(pair => pair.Key).ToArray())
            ownedFields.Remove(key);
        // An absent read is unresolved restoration, not evidence that an external edit superseded it.
        bool failedPages = missingOwnedRead || unverifiedRestores.Count > 0 || result.ExecutionState?.Pages.Any(page => page.ErrorCode is not null ||
            page.State is ApplyState.Failed or ApplyState.Unknown) == true;
        // A retry cannot finish while its original failed field is still owned, even if it is now skipped.
        failedPages |= lease.FieldRestoreRetryable && ownedFields.Values.Any(field =>
            field.OperationId == command.BranchOperationId && field.Origin == ControlOrigin.TurboBranch);
        if (failedPages || result.State != CommandState.Applied || result.Error is not null)
        {
            bool retryable = !unknown && result.State == CommandState.Applied && result.Error is null &&
                !ownedFields.Values.Any(field => (field.OperationId == command.OperationId || field.OperationId == command.BranchOperationId) &&
                    field.State == ApplyState.Unknown) &&
                result.ExecutionState?.Pages.All(page => page.State != ApplyState.Unknown &&
                    page.UnverifiedFields.All(field => unverifiedRestores.ContainsKey(FieldKey(page.Page, field)))) == true &&
                (unverifiedRestores.Count > 0 || ownedFields.Values.Any(field => field.OperationId == command.BranchOperationId && field.State == ApplyState.Confirmed));
            turboBranches[command.BranchOperationId] = lease with { ReleaseGeneration = admission.Generation,
                FieldRestoreRetryable = retryable, NativeRestoreRetryable = retryable && restoreNative,
                UnverifiedRestores = unverifiedRestores.Count > 0 ? unverifiedRestores : null };
            result = result with { State = CommandState.RecoveryRequired };
        }
        if (!failedPages && restoreNative && Execution.IsCurrent(admission.Generation) && result.State == CommandState.Applied && result.Error is null && !unknown)
        {
            // If dispatch throws or its reply is lost, the original branch must remain unresolved and non-retryable.
            turboBranches[command.BranchOperationId] = lease with
                { ReleaseGeneration = admission.Generation, NativeRestoreRetryable = false, FieldRestoreRetryable = false };
            var restored = await ExecuteTrackedCommandAsync(new SetPerformanceModeCommand(command.OperationId, lease.NativeBaseline!.Value), token, connectionId);
            if (restored.State != CommandState.Applied || restored.Error is not null || restored.VerifiedPerformanceMode != lease.NativeBaseline)
                turboBranches[command.BranchOperationId] = lease with { ReleaseGeneration = admission.Generation, FieldRestoreRetryable = false,
                    NativeRestoreRetryable = restored.State == CommandState.Rejected && restored.Error is not null &&
                        restored.Mutation == HardwareMutation.NoMutation && result.ExecutionState?.Pages.All(page => page.State == ApplyState.Confirmed) == true };
            if (controlExecutionState is { } restoredState && result.ExecutionState is { } pageState)
                Volatile.Write(ref controlExecutionState, restoredState with { Pages = pageState.Pages });
            result = restored with { Mutation = restored.Mutation == HardwareMutation.NoMutation ? result.Mutation : restored.Mutation };
            if (result.State == CommandState.Applied && result.VerifiedPerformanceMode != lease.NativeBaseline)
                result = result with { State = CommandState.RecoveryRequired };
        }
        else if (controlExecutionState is { } execution)
            Volatile.Write(ref controlExecutionState, execution with { Pages = execution.Pages.Append(new PageApplyResult(ControlPageId.Performance,
                null, 0, unknown ? ApplyState.Unknown : ApplyState.Partial, [], [], ["nativeMode"], null)).ToArray() });
        if (controlExecutionState is { } final)
        {
            var restoredControls = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
            foreach (var pair in lease.PreviousOwners)
            {
                var previous = pair.Value;
                int separator = pair.Key.IndexOf('.');
                if (!contextMatches || ownedFields.ContainsKey(pair.Key) || previous.OwnerSid != admission.Owner ||
                    previous.State != ApplyState.Confirmed || previous.Epoch != Execution.HardwareEpoch || separator < 0 ||
                    !Enum.TryParse<ControlPageId>(pair.Key[..separator], true, out var page)) continue;
                var actual = ControlPlanCompiler.Read(page, restoredControls).GetValueOrDefault(pair.Key[(separator + 1)..]);
                if (previous.Scheme == restoredControls.CpuTuning?.WindowsPowerSchemeId &&
                    MatchesOwnedValue(previous, actual, restoredControls.CpuTuning)) ownedFields[pair.Key] = previous;
            }
            Volatile.Write(ref controlExecutionState, final with { Fields = SnapshotFields() });
            result = result with { ExecutionState = controlExecutionState, State = unknown ? CommandState.RecoveryRequired : result.State };
        }
        if (!unknown && result.State == CommandState.Applied && result.Error is null &&
            (!restoreNative || result.VerifiedPerformanceMode == lease.NativeBaseline))
        {
            turboBranches.Remove(command.BranchOperationId);
            completedTurboBranchesByOwner[command.BranchOperationId] = admission.Owner;
            completedTurboBranches.Enqueue(command.BranchOperationId);
            if (completedTurboBranches.Count > 256) completedTurboBranchesByOwner.Remove(completedTurboBranches.Dequeue());
        }
        return result;
    }

    private async Task<bool> ConfirmTurboRestoreReadbackAsync(TurboBranchLease lease, long generation, CancellationToken token)
    {
        var controls = await ReadControlBaselineForExecutionAsync(token, true);
        if (!Execution.IsCurrent(generation) || lease.Epoch != Execution.HardwareEpoch ||
            lease.Scheme != controls.CpuTuning?.WindowsPowerSchemeId || controls.PerformanceMode != PerformanceMode.Turbo)
            return false;
        foreach (var pair in lease.UnverifiedRestores!)
        {
            var pending = pair.Value;
            int separator = pair.Key.IndexOf('.');
            if (!ownedFields.TryGetValue(pair.Key, out var current) || current != pending ||
                pending.State != ApplyState.SubmittedUnverified || pending.OwnerSid != lease.Owner ||
                pending.Epoch != lease.Epoch || pending.Scheme != lease.Scheme ||
                pending.Baseline is not { Intent: SettingIntent.Set } || pending.LastWritten is not { Intent: SettingIntent.Set } ||
                pending.BaselineDetails is not null || pending.LastDetails is not null ||
                !ControlPlanCompiler.Same(pending.Baseline, pending.LastWritten) || separator < 0 ||
                !Enum.TryParse<ControlPageId>(pair.Key[..separator], true, out var page)) return false;
            var value = ControlPlanCompiler.Read(page, controls).GetValueOrDefault(pair.Key[(separator + 1)..]);
            var capability = State.Capabilities.Fields.FirstOrDefault(field => field.Key == pair.Key[(separator + 1)..]);
            if (capability is not { Availability: CapabilityState.Available, Readable: true, VerifiedAtUtc: not null } ||
                capability.HardwareEpoch != lease.Epoch || capability.Source == "uiSuggestion") return false;
            if (!ControlPlanCompiler.Same(value, pending.LastWritten)) return false;
        }
        // Retire only the exact acknowledged restore target; never replay its write or resurrect B as Confirmed.
        foreach (string key in lease.UnverifiedRestores.Keys) ownedFields.Remove(key);
        return true;
    }

    private async Task<bool> CanRetryTurboNativeRestoreAsync(TurboBranchLease lease, long generation, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        bool Fresh(PerformanceModeEvidence? evidence) => evidence is { ParsedMode: PerformanceMode.Turbo, RawValue: byte raw } &&
            MiPerformanceModeCodec.Decode(raw) == PerformanceMode.Turbo && evidence.CapturedAtUtc >= started.AddSeconds(-1) &&
            evidence.CapturedAtUtc <= DateTimeOffset.UtcNow;
        EnsureControlHardwareAccess();
        var first = await provider.ReadNativeModeEvidenceAsync(token);
        if (!Fresh(first)) return false;
        var second = await provider.ReadNativeModeEvidenceAsync(token);
        if (!Fresh(second)) return false;
        var current = await ReadControlBaselineForExecutionAsync(token, true);
        EnsureControlHardwareAccess();
        return Execution.IsCurrent(generation) && lease.Epoch == Execution.HardwareEpoch &&
            lease.Scheme == current.CpuTuning?.WindowsPowerSchemeId && current.PerformanceMode == PerformanceMode.Turbo;
    }

    private static CompiledControlPage BranchPage(ControlPageId page, Dictionary<string, SettingRequest> settings, string[]? skipped = null) =>
        new(new(page, true, PresetKey.Create(ControlModeId.Turbo, 2), 1), settings, skipped ?? []);
    private static ApplyControlRequestCommand BranchRequest(Guid operationId, PerformanceMode mode, CompiledControlPlan plan) =>
        new(operationId, new(operationId, 0, ControlOrigin.TurboBranch, ControlModeId.Turbo, mode, plan.Pages.Select(page => page.Selection).ToArray()));
}
