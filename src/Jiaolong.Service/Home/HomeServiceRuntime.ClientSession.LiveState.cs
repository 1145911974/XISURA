using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private async Task<string?> VerifyPriorLiveStateAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved,
        WindowsScopeSession? windows, CancellationToken token, bool nativeFanRecovery = false, bool nativeGpuRecovery = false,
        NativeModeRecoveryAttempt? pendingMode = null, bool nativeCpuRecovery = false, bool lightingRecovery = false,
        bool ignoreArchivedPlan = false, ExitPlan? satisfiedPlan = null)
    {
        // A partly executed original plan needs its own checkpoints, not an inferred fresh plan.
        if (!ignoreArchivedPlan && File.Exists(Path.Combine(journal!.RootDirectory, "client-session-recovery.json")))
            return "PartialExitRecoveryVerificationRequired";
        if (!nativeFanRecovery && (saved.RecoveryScope!.FanOwnerClientSessionId is not null || saved.Current!.Fields.Keys.Any(key => key.StartsWith("fan."))))
            return "NativeFanRecoveryVerificationRequired";
        bool pendingFan = nativeFanRecovery && PriorPendingFan(session, saved) is not null;
        string[] pendingCpu = nativeCpuRecovery ? PriorPendingCpuFields(session, saved) : [];
        string[] pendingLight = lightingRecovery ? PriorPendingLighting(session, saved)?.Fields ?? [] : [];
        if (saved.Current!.Fields.Any(pair => pair.Value.WriteState is not (ApplyState.Confirmed or ApplyState.SubmittedUnverified) &&
            !(pendingFan && pair.Key.StartsWith("fan.")) && !(nativeGpuRecovery && pair.Key.StartsWith("gpu.")) && !pendingCpu.Contains(pair.Key) && !pendingLight.Contains(pair.Key)))
            return "UnconfirmedFieldVerificationRequired";
        if (saved.Current!.Fields.Keys.Any(key => !new[] { "performance.", "gpu.", "lighting.", "fan." }.Any(key.StartsWith)))
            return "UnsupportedFieldVerificationRequired";

        var expectedMode = saved.Current.NativeModeOwnerClientSessionId is not null
            ? saved.RecoveryScope!.NativeModeTarget : session.Baseline.PerformanceMode;
        var started = DateTimeOffset.UtcNow;
        PerformanceMode? observedMode = null;
        bool ModeMatches(PerformanceModeEvidence? evidence) => evidence is { ParsedMode: not null, RawValue: not null } &&
            (pendingMode is null ? evidence.ParsedMode == expectedMode :
                (evidence.ParsedMode == pendingMode.Before || evidence.ParsedMode == pendingMode.Target) &&
                (observedMode is null || observedMode == evidence.ParsedMode)) &&
            MiPerformanceModeCodec.Decode(evidence.RawValue.Value) == evidence.ParsedMode &&
            evidence.CapturedAtUtc >= started.AddSeconds(-1) && evidence.CapturedAtUtc <= DateTimeOffset.UtcNow.AddSeconds(1);
        for (int observation = 0; observation < 2; observation++)
        {
            token.ThrowIfCancellationRequested();
            var mode = await provider.ReadNativeModeEvidenceAsync(token);
            if (!ModeMatches(mode)) return "NativeModeLiveStateUnverified";
            observedMode ??= mode!.ParsedMode;
            var actual = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
            if (actual.PerformanceMode != observedMode) return "NativeModeLiveStateUnverified";
            if (nativeCpuRecovery && !PendingCpuLiveMatches(session, saved, windows, actual)) return "CpuPendingStateUnverified";
            if (lightingRecovery && !PendingLightingLiveMatches(session, saved, actual)) return "LightingPendingStateUnverified";
            if (actual.GpuVf is not { Error: null, Nodes.Length: 127, CoreOffsetKhz: not null, MemoryOffsetKhz: not null } ||
                actual.GpuVf.Nodes.Any(node => node is null)) return "GpuLiveStateUnreadable";
            if (actual.KeyboardLighting is null || actual.KeyboardLightingPreviewActive || session.Baseline.KeyboardLighting is null)
                return "LightingLiveStateUnreadable";
            if (satisfiedPlan is not null && (satisfiedPlan.Steps.Any(step => !ExitMatches(step, actual, true)) ||
                !System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(actual.QuickSettings),
                    System.Text.Json.JsonSerializer.SerializeToElement(session.Baseline.QuickSettings))))
                return "LegacyEmptyExitTargetUnmet";
            var baseline = new HomeControlState(session.Baseline.PerformanceMode, session.Baseline.StrongCooling, session.Baseline.QuickSettings)
            { CpuTuning = session.Baseline.CpuTuning, GpuVf = session.Baseline.GpuVf,
                KeyboardLighting = session.Baseline.KeyboardLighting, ActiveFanControlPlan = session.Baseline.ActiveFanControlPlan };
            foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Gpu, ControlPageId.Lighting, ControlPageId.Fan })
            {
                if (nativeFanRecovery && page == ControlPageId.Fan || nativeGpuRecovery && page == ControlPageId.Gpu) continue;
                string prefix = page.ToString().ToLowerInvariant() + ".";
                var expected = ControlPlanCompiler.Read(page, baseline);
                var observed = ControlPlanCompiler.Read(page, actual);
                foreach (var pair in saved.Current.Fields.Where(pair => pair.Key.StartsWith(prefix)))
                {
                    string field = pair.Key[prefix.Length..];
                    if (pair.Value.LastWritten is { } value) expected[field] = value;
                    else expected.Remove(field);
                }
                if (satisfiedPlan is not null)
                    foreach (var step in satisfiedPlan.Steps.Where(step => step.Key.StartsWith(prefix) && step.Target is not null))
                        expected[step.Key[prefix.Length..]] = step.Target!;
                foreach (var field in expected.Keys.Union(observed.Keys))
                {
                    if (page == ControlPageId.Performance && windows is not null && IsWindowsIndexField(field)) continue;
                    string key = prefix + field;
                    if (pendingCpu.Contains(key) || pendingLight.Contains(key)) continue;
                    bool matches;
                    if (page == ControlPageId.Performance && field is "negativeCurveOptimizer" or "isBoostEnabled")
                    {
                        var owner = saved.Current.Fields.GetValueOrDefault(key);
                        var details = owner is not null ? owner.LastWrittenDetails :
                            satisfiedPlan?.Steps.FirstOrDefault(step => step.Key == key)?.TargetDetails ?? CaptureCpuRestoration(field, baseline.CpuTuning);
                        matches = details is not null && SameRestoration(details, CaptureCpuRestoration(field, actual.CpuTuning));
                    }
                    else matches = ExitSame(expected.GetValueOrDefault(field), observed.GetValueOrDefault(field));
                    if (!matches) return "LiveStateConflict:" + key;
                }
            }
            if (actual.CpuTuning is null || session.Baseline.CpuTuning is null) return "CpuLiveStateUnreadable";
            foreach (string field in new[] { "negativeCurveOptimizer", "isBoostEnabled" })
            {
                if (pendingCpu.Contains("performance." + field)) continue;
                if (field == "isBoostEnabled" && windows is not null) continue;
                var owner = saved.Current.Fields.GetValueOrDefault("performance." + field);
                var expected = owner is not null ? owner.LastWrittenDetails :
                    satisfiedPlan?.Steps.FirstOrDefault(step => step.Key == "performance." + field)?.TargetDetails ?? CaptureCpuRestoration(field, baseline.CpuTuning);
                if (expected is null || !SameRestoration(expected, CaptureCpuRestoration(field, actual.CpuTuning)))
                    return "CpuLiveStateUnreadable";
            }
            if (!await PriorWindowsLiveStateMatchesAsync(windows, token, nativeCpuRecovery
                ? (scheme, key, value) => PendingCpuWindowMatches(session, saved, windows, scheme, key, value) : null)) return "WindowsScopeLiveStateConflict";
        }
        return null;
    }

    private static bool CanRestorePriorNativeFan(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        var scope = new FanEcRecoveryScope(session.ServiceSessionId, session.Handle.HardwareEpoch, session.Handle.SessionId, session.OwnerSid, session.Identity);
        return saved.Current!.Fields.Keys.Any(key => key.StartsWith("fan.")) &&
            (saved.FanEcRecovery?.Scope == scope || ControlExecutionJournal.IsFanFirmwareOnlyRecovery(session, saved)) &&
            session.Baseline.StrongCooling is bool original && (saved.FanFirmwareRecovery?.Scope == scope
                ? saved.FanFirmwareRecovery.OriginalBefore == (original ? 1 : 0) : !original);
    }

    private async Task<bool> PriorWindowsLiveStateMatchesAsync(WindowsScopeSession? windows, CancellationToken token,
        Func<Guid, WindowsCpuSetting, int, bool>? allowed = null)
    {
        if (windows is null) return true;
        foreach (var scheme in windows.Schemes)
        {
            var values = await ReadWindowsValuesAsync(scheme.Key, token);
            foreach (var field in scheme.Value.Baseline)
            {
                if (!values.TryGetValue(field.Key, out int value)) return false;
                var written = scheme.Value.Writes.GetValueOrDefault(field.Key);
                if ((written is null || written.Restored ? value != field.Value : value != field.Value && value != written.LastWritten) &&
                    allowed?.Invoke(scheme.Key, field.Key, value) != true) return false;
            }
        }
        return true;
    }
}
