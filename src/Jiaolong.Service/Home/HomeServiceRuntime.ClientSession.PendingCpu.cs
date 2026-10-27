using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static bool IsDirectCpuScalar(string field) => field is "pptWatts" or "mp1TemperatureC" or "tdcCurrentMilliamps" or "edcCurrentMilliamps";
    private bool CanRestoreCpuScalar(string field) => IsDirectCpuScalar(field) && State.Capabilities.Fields.Any(item =>
        item.Key == field && item.Readable && PresetValidation.IsVerifiedWritable(item, Execution.HardwareEpoch));

    private static WindowsCpuSetting[] CpuWindowsKeys(string field) => field switch
    {
        "acMaxFrequencyMhz" => [WindowsCpuSetting.AcMaxFrequencyMhz], "dcMaxFrequencyMhz" => [WindowsCpuSetting.DcMaxFrequencyMhz],
        "acMinActiveCoresPercent" => [WindowsCpuSetting.AcMinActiveCoresPercent], "dcMinActiveCoresPercent" => [WindowsCpuSetting.DcMinActiveCoresPercent],
        "isBoostEnabled" => [WindowsCpuSetting.AcBoostMode, WindowsCpuSetting.DcBoostMode], _ => []
    };

    private static string[] PriorPendingCpuFields(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending] || PendingCpuCommandKind(saved) is not (nameof(SetCpuTuningCommand) or nameof(SetCpuTuningBatchCommand)) ||
            pending.OwnerSid != session.OwnerSid || pending.Identity != session.Identity ||
            pending.GpuRecovery is not null || pending.GpuAttempt is not null || !CompletedCompoundMode(session, saved) || saved.Current is null ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved.Current), JsonSerializer.SerializeToElement(pending.State)) ||
            pending.State.NativeModeApplyState == ApplyState.Unknown ||
            pending.State.Pages.Any(page => page.Page != ControlPageId.Performance) && CompoundCpuStep(saved) is null ||
            !ValidPendingCpuBatch(saved) ||
            pending.State.Fields.Any(pair => pair.Value.WriteState is not (ApplyState.Confirmed or ApplyState.SubmittedUnverified) &&
                pair.Value.OwnerOperationId != pending.OperationId)) return [];
        var fields = PendingCpuOwnedFields(saved);
        if (fields.Length == 0 || fields.Any(pair => !pair.Key.StartsWith("performance.") || pair.Value.ClientSessionId != session.Handle.SessionId ||
            pair.Value.LastWritten is null || (pair.Key is "performance.negativeCurveOptimizer" or "performance.isBoostEnabled"
                ? pair.Value.WriteBeforeDetails is null || pair.Value.WriteTargetDetails is null && !(pair.Key == "performance.isBoostEnabled" && CrossSchemeCpuBatch(saved)) || pair.Value.BaselineDetails is null
                : CpuWindowsKeys(pair.Key[12..]).Length == 0 && pair.Key != "performance.windowsPowerSchemeId" && !IsDirectCpuScalar(pair.Key[12..]) ||
                  pair.Value.WriteBefore is null || IsDirectCpuScalar(pair.Key[12..]) && pair.Value.Baseline is null))) return [];
        return fields.Select(pair => pair.Key).ToArray();
    }

    private static CpuFieldRestorationValue? PendingCpuFinalDetails(ClientControlSessionSnapshot session, FieldExecutionState field,
        string key, WindowsScopeSession? windows, Guid? scheme)
    {
        if (key == "performance.negativeCurveOptimizer" && field.BaselineDetails?.PerCoreCurveOptimizer is { } cores)
            return session.ExitDefaults?.CurveOptimizerAll is int co ? new(null, null, cores.Keys.ToDictionary(core => core, _ => co)) : field.BaselineDetails;
        if (key == "performance.isBoostEnabled" && scheme is Guid id && windows?.Schemes.GetValueOrDefault(id) is { } scope)
            return new(scope.Baseline.GetValueOrDefault(WindowsCpuSetting.AcBoostMode, -1), scope.Baseline.GetValueOrDefault(WindowsCpuSetting.DcBoostMode, -1), null);
        return field.BaselineDetails;
    }

    private static bool KnownCpuDetails(CpuFieldRestorationValue? actual, CpuFieldRestorationValue? before,
        CpuFieldRestorationValue? target, CpuFieldRestorationValue? final, IEnumerable<CpuFieldRestorationValue?>? intermediate = null)
    {
        if (actual is null || before is null || target is null || final is null) return false;
        CpuFieldRestorationValue?[] candidates = [before, target, final, .. intermediate ?? []];
        if (actual.PerCoreCurveOptimizer is { Count: > 0 } cores)
            return candidates.All(candidate => candidate?.PerCoreCurveOptimizer is { } values && cores.Keys.Order().SequenceEqual(values.Keys.Order())) &&
                cores.All(pair => candidates.Any(candidate => candidate!.PerCoreCurveOptimizer![pair.Key] == pair.Value));
        return actual.AcBoostMode is not null && actual.DcBoostMode is not null &&
            candidates.All(candidate => candidate?.AcBoostMode is not null && candidate.DcBoostMode is not null) &&
            candidates.Any(candidate => candidate!.AcBoostMode == actual.AcBoostMode) && candidates.Any(candidate => candidate!.DcBoostMode == actual.DcBoostMode);
    }

    private static int? CpuMarkerIndex(FieldExecutionState field, WindowsCpuSetting key, bool before)
    {
        var details = before ? field.WriteBeforeDetails : field.WriteTargetDetails;
        if (key == WindowsCpuSetting.AcBoostMode) return details?.AcBoostMode;
        if (key == WindowsCpuSetting.DcBoostMode) return details?.DcBoostMode;
        var value = (before ? field.WriteBefore : field.LastWritten)?.Value;
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out int index) ? index : null;
    }

    private static bool PendingCpuWindowMatches(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, WindowsScopeSession? windows,
        Guid scheme, WindowsCpuSetting key, int actual)
    {
        if (windows?.Schemes.GetValueOrDefault(scheme) is not { } scope || !scope.Writes.ContainsKey(key) || !scope.Baseline.TryGetValue(key, out int final)) return false;
        if (VerifiedCpuWindowsProof(saved, windows) is { } proof)
        {
            var index = proof.Schemes.GetValueOrDefault(scheme)?.GetValueOrDefault(key);
            return index is { Targets.Length: > 0 } && (actual == index.Before || index.Targets.Contains(actual) || actual == final);
        }
        if (CrossSchemeCpuBatch(saved)) return false;
        foreach (string fieldKey in PriorPendingCpuFields(session, saved))
        {
            if (saved.RecoveryScope!.Fields[fieldKey].WindowsPowerSchemeId != scheme || !CpuWindowsKeys(fieldKey[12..]).Contains(key)) continue;
            var field = saved.Current!.Fields[fieldKey];
            return actual == CpuMarkerIndex(field, key, true) || actual == CpuMarkerIndex(field, key, false) || actual == final ||
                PendingCpuBatchIndices(saved, fieldKey, key).Contains(actual);
        }
        return false;
    }

    private static bool PendingCpuLiveMatches(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, WindowsScopeSession? windows, HomeControlState controls)
    {
        var proof = VerifiedCpuWindowsProof(saved, windows);
        if (proof is null && (CrossSchemeCpuBatch(saved) || windows?.PendingWrite is not null &&
            saved.Current?.CpuWriteBatch?.Plans.Any(HasWindowsSettings) == true)) return false;
        if (proof is not null && (controls.CpuTuning?.WindowsPowerSchemeId is null ||
            controls.CpuTuning.WindowsPowerSchemeId != proof.InitialScheme &&
            !saved.Current!.CpuWriteBatch!.Plans.Any(plan => plan.WindowsPowerSchemeId == controls.CpuTuning.WindowsPowerSchemeId))) return false;
        foreach (string key in PriorPendingCpuFields(session, saved))
        {
            var field = saved.Current!.Fields[key];
            if (IsDirectCpuScalar(key[12..]))
            {
                if (!PendingCpuScalarMatches(saved, key, ControlPlanCompiler.Read(ControlPageId.Performance, controls).GetValueOrDefault(key[12..]))) return false;
                continue;
            }
            var scheme = saved.RecoveryScope!.Fields[key].WindowsPowerSchemeId;
            if (proof is null && (scheme is null || controls.CpuTuning?.WindowsPowerSchemeId != scheme)) return false;
            if (proof is not null && key != "performance.negativeCurveOptimizer") continue;
            if (key == "performance.negativeCurveOptimizer")
            {
                if (!KnownCpuDetails(CaptureCpuRestoration(key[12..], controls.CpuTuning), field.WriteBeforeDetails, field.WriteTargetDetails,
                    PendingCpuFinalDetails(session, field, key, windows, scheme), PendingCpuBatchDetails(saved, key))) return false;
            }
            else
                foreach (var index in CpuWindowsKeys(key[12..]))
                    if (scheme is not Guid windowsScheme || !WindowsValues(controls.CpuTuning).TryGetValue(index, out int actual) ||
                        !PendingCpuWindowMatches(session, saved, windows, windowsScheme, index, actual)) return false;
        }
        return true;
    }

    private Task<bool> RestorePriorPendingCpuAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, WindowsScopeSession? windows, CancellationToken token)
    {
        string[] fields = PriorPendingCpuFields(session, saved);
        if (fields.Length == 0) throw new InvalidDataException("priorPendingCpuUnverified");
        var pending = saved.Unresolved.Single();
        return Execution.WithRecoveryAdmissionAsync(pending.OperationId, session.OwnerSid, async () =>
        {
            var controls = await ReadControlBaselineForExecutionAsync(token, true);
            if (!PendingCpuLiveMatches(session, saved, windows, controls)) return false;
            if (CrossSchemeCpuBatch(saved) || fields.Any(key => CpuWindowsKeys(key[12..]).Length > 0))
            {
                if (windows is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(windows),
                    JsonSerializer.SerializeToElement(await ReadWindowsScopesAsync(session, true, token)))) return false;
                foreach (var scheme in windows.Schemes)
                {
                    var values = await ReadWindowsValuesAsync(scheme.Key, token);
                    var proof = VerifiedCpuWindowsProof(saved, windows);
                    var indices = proof is not null ? proof.Schemes.GetValueOrDefault(scheme.Key)?.Where(pair => pair.Value.Targets.Length > 0).Select(pair => pair.Key) ?? [] :
                        fields.Where(key => saved.RecoveryScope!.Fields[key].WindowsPowerSchemeId == scheme.Key).SelectMany(key => CpuWindowsKeys(key[12..])).Distinct();
                    foreach (var index in indices)
                        {
                            if (!values.TryGetValue(index, out int current) || !PendingCpuWindowMatches(session, saved, windows, scheme.Key, index, current)) return false;
                            scheme.Value.Writes[index] = new(current, current == scheme.Value.Baseline[index]);
                        }
                }
                // Keep the first baseline; persist the proven partial state before restoring Windows indices.
                await WindowsScopeStore(session).WriteAsync(windows, token);
                if (!await RestoreWindowsScopesAsync(session, windows, token)) return false;
            }
            if (fields.Contains("performance.negativeCurveOptimizer"))
            {
                controls = await ReadControlBaselineForExecutionAsync(token, true);
                var field = saved.Current!.Fields["performance.negativeCurveOptimizer"];
                var target = PendingCpuFinalDetails(session, field, "performance.negativeCurveOptimizer", windows, controls.CpuTuning?.WindowsPowerSchemeId);
                var actual = CaptureCpuRestoration("negativeCurveOptimizer", controls.CpuTuning);
                if (!KnownCpuDetails(actual, field.WriteBeforeDetails, field.WriteTargetDetails, target,
                    PendingCpuBatchDetails(saved, "performance.negativeCurveOptimizer"))) return false;
                if (!SameRestoration(actual, target))
                {
                    var check = new SetCpuTuningCommand(pending.OperationId, new(null, null, null, null, null, null, null, null)
                        { Advanced = new(PerCoreCurveOptimizer: target!.PerCoreCurveOptimizer) }, true);
                    if (!CanExecute(check, State) || !CanWriteTuningFields(check, State)) return false;
                    var result = await TrackHardwareDispatchAsync(() => provider.RestoreCpuFieldAsync(pending.OperationId,
                        "negativeCurveOptimizer", target!, actual!, token));
                    if (result is not { State: CommandState.Applied, Error: null }) return false;
                }
            }
            foreach (string key in fields.Where(key => IsDirectCpuScalar(key[12..])))
            {
                controls = await ReadControlBaselineForExecutionAsync(token, true);
                var actual = ControlPlanCompiler.Read(ControlPageId.Performance, controls).GetValueOrDefault(key[12..]);
                var target = saved.Current!.Fields[key].Baseline;
                if (!PendingCpuScalarMatches(saved, key, actual)) return false;
                if (!ControlPlanCompiler.Same(actual, target))
                {
                    if (!CanRestoreCpuScalar(key[12..])) return false;
                    var restored = await TrackHardwareDispatchAsync(() => provider.RestoreCpuScalarAsync(pending.OperationId, key[12..], target!, actual!, token));
                    if (restored is not { State: CommandState.Applied, Error: null }) return false;
                }
            }
            controls = await ReadControlBaselineForExecutionAsync(token, true);
            if (!PendingCpuLiveMatches(session, saved, windows, controls)) return false;
            if ((CrossSchemeCpuBatch(saved) || fields.Any(key => CpuWindowsKeys(key[12..]).Length > 0)) && !await WindowsScopesMatchAsync(windows, true, token)) return false;
            foreach (string key in fields)
            {
                if (IsDirectCpuScalar(key[12..]) && !ControlPlanCompiler.Same(
                    ControlPlanCompiler.Read(ControlPageId.Performance, controls).GetValueOrDefault(key[12..]), saved.Current!.Fields[key].Baseline)) return false;
                if (key == "performance.negativeCurveOptimizer" && !SameRestoration(CaptureCpuRestoration(key[12..], controls.CpuTuning),
                    PendingCpuFinalDetails(session, saved.Current!.Fields[key], key, windows, controls.CpuTuning?.WindowsPowerSchemeId))) return false;
            }
            if (!SamePriorJournal(await journal!.ReadAsync(token), await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token)))
                throw new InvalidDataException("priorPendingCpuOriginalJournalChanged");
            var priorState = controlExecutionState;
            var previous = fields.ToDictionary(key => key, key => ownedFields[key]);
            try
            {
                var observed = ControlPlanCompiler.Read(ControlPageId.Performance, controls);
                foreach (string key in fields) ownedFields[key] = ownedFields[key] with { State = ApplyState.Confirmed,
                    LastWritten = observed.GetValueOrDefault(key[12..]), LastDetails = CaptureCpuRestoration(key[12..], controls.CpuTuning) };
                Volatile.Write(ref controlExecutionState, controlExecutionState! with { Fields = SnapshotFields(), Pages = [],
                    NativeMode = controls.PerformanceMode ?? controlExecutionState!.NativeMode, NativeModeApplyState = ApplyState.Confirmed,
                    PendingStep = null, CpuWriteBatch = null, WindowsWriteProofHash = null });
                await PersistExecutionAsync(pending.OperationId, false, token, commandKind: pending.CommandKind);
                Volatile.Write(ref state, State with { Controls = controls });
                return true;
            }
            catch
            {
                foreach (var pair in previous) ownedFields[pair.Key] = pair.Value;
                Volatile.Write(ref controlExecutionState, priorState);
                throw;
            }
        });
    }
}
