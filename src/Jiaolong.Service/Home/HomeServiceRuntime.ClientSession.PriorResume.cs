using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private async Task<ExitPlan> ReadPriorExitPlanAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved,
        WindowsScopeSession? windows, CancellationToken token)
    {
        var store = new AtomicJsonStore(journal!.RootDirectory, "client-session-recovery.json");
        if (!File.Exists(store.FilePath) || new FileInfo(store.FilePath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("priorExitPlanMissingOrTooLarge");
        var plan = await store.ReadAsync<ExitPlan>(token);
        bool idleFanRelease = plan is not null && ValidExitPlan(plan) && plan.SessionId == session.Handle.SessionId &&
            await VerifyIdleFanReleaseAsync(session, plan.OperationId, null, token, saved);
        if (!ValidExitPlan(plan) || plan!.SessionId != session.Handle.SessionId ||
            (plan.WindowsScopeSessionId is not null) != (windows is not null) || !PriorExitTargetsMatch(session, plan) ||
            plan.Steps.Any(step => step.Key == "fan" && step.FanFirmwareOnly != ControlExecutionJournal.IsFanFirmwareOnlyRecovery(session, saved)) ||
            (saved.RecoveryScope?.FanOwnerClientSessionId is not null || saved.Current!.Fields.Keys.Any(key => key.StartsWith("fan."))) &&
                !CanRestorePriorNativeFan(session, saved) &&
                !idleFanRelease ||
            !idleFanRelease && saved.Unresolved.Any(item => item.OperationId != plan.OperationId || item.OwnerSid != session.OwnerSid ||
                item.CommandKind != "EndClientControlSession") ||
            saved.Unresolved.Any(item => item.NativeModeAttempt is not null) && PriorExitModeAttempt(session, plan, saved) is null ||
            saved.Unresolved.Any(item => item.GpuRecovery is not null || item.GpuAttempt is not null) && PriorExitGpuAttempt(session, plan, saved) is null)
            throw new InvalidDataException("priorExitPlanNotVerified");
        var required = new HashSet<string>(StringComparer.Ordinal) { "performance.negativeCurveOptimizer" };
        if (session.Baseline.GpuVf?.CoreOffsetKhz is not null) required.Add("gpu.coreOffsetKhz");
        if (session.Baseline.GpuVf?.MemoryOffsetKhz is not null) required.Add("gpu.memoryOffsetKhz");
        if (session.Baseline.GpuVf?.Nodes.Length == 127) required.Add("gpu.vfOffsetsKhz");
        if (session.Baseline.KeyboardLighting is not null) required.Add("lighting");
        if (saved.Current!.NativeModeOwnerClientSessionId is not null) required.Add("mode");
        foreach (string key in saved.Current.Fields.Keys)
        {
            if (windows is not null && key.StartsWith("performance.") && IsWindowsIndexField(key[12..])) continue;
            required.Add(key.StartsWith("lighting.") ? "lighting" : key.StartsWith("fan.") ? "fan" : key);
        }
        if (required.Any(key => plan.Steps.All(step => step.Key != key))) throw new InvalidDataException("priorExitPlanStepMissing");
        return plan;
    }

    private static bool PriorExitTargetsMatch(ClientControlSessionSnapshot session, ExitPlan plan)
    {
        var baseline = new HomeControlState(session.Baseline.PerformanceMode, session.Baseline.StrongCooling, session.Baseline.QuickSettings)
        { CpuTuning = session.Baseline.CpuTuning, GpuVf = session.Baseline.GpuVf, KeyboardLighting = session.Baseline.KeyboardLighting };
        var cpu = ControlPlanCompiler.Read(ControlPageId.Performance, baseline);
        foreach (var step in plan.Steps)
        {
            if (!ExactCpuExitCheckpoint(step)) return false;
            if (step.Key == "fan")
            {
                if (session.Baseline.StrongCooling is null || step.FanStrongCoolingTarget != session.Baseline.StrongCooling ||
                    !ExitSame(step.Target, SettingRequest.Set("Auto")) || step.TargetDetails is not null || step.Status is not
                        ("Pending" or "Applying" or "Verified" or "Failed" or "Conflict" or "Deferred" or "Unavailable" or "Unsupported" or "BaselineUnreadable")) return false;
                continue;
            }
            SettingRequest? target;
            CpuFieldRestorationValue? details = null;
            if (step.Key.StartsWith("performance."))
            {
                string field = step.Key[12..];
                target = cpu.GetValueOrDefault(field);
                if (field is "negativeCurveOptimizer" or "isBoostEnabled")
                {
                    details = CaptureCpuRestoration(field, baseline.CpuTuning);
                    if (field == "negativeCurveOptimizer" && details?.PerCoreCurveOptimizer is { } cores && session.ExitDefaults?.CurveOptimizerAll is int value)
                        details = new(null, null, cores.Keys.ToDictionary(key => key, _ => value));
                    if (details is null) return false;
                }
            }
            else target = step.Key switch
            {
                "mode" => SettingRequest.Set(session.Baseline.PerformanceMode),
                "lighting" => SettingRequest.Set(session.Baseline.KeyboardLighting),
                "gpu.coreOffsetKhz" or "gpu.memoryOffsetKhz" => SettingRequest.Set(0),
                "gpu.vfOffsetsKhz" => SettingRequest.Set(new int[127]),
                _ => null
            };
            if (target is null && details is null ||
                (details is null ? !ExitSame(target, step.Target) || step.TargetDetails is not null : !SameRestoration(details, step.TargetDetails)) ||
                step.Status is not ("Pending" or "Applying" or "Verified" or "Failed" or "Conflict" or "Deferred" or
                    "Unavailable" or "Unsupported" or "BaselineUnreadable" or "SchemeRecoveryRequired")) return false;
        }
        return true;
    }

    private async Task<string?> VerifyPriorExitLiveStateAsync(ClientControlSessionSnapshot session, ExitPlan plan,
        WindowsScopeSession? windows, CancellationToken token, ControlJournalSnapshot? nativeJournal = null)
    {
        var modeStep = plan.Steps.FirstOrDefault(step => step.Key == "mode");
        var started = DateTimeOffset.UtcNow;
        var interruptedGpu = nativeJournal is null ? null : PriorExitGpuAttempt(session, plan, nativeJournal);
        for (int observation = 0; observation < 2; observation++)
        {
            var mode = await provider.ReadNativeModeEvidenceAsync(token);
            if (mode is not { ParsedMode: not null, RawValue: not null } || MiPerformanceModeCodec.Decode(mode.RawValue.Value) != mode.ParsedMode ||
                mode.CapturedAtUtc < started.AddSeconds(-1) || mode.CapturedAtUtc > DateTimeOffset.UtcNow.AddSeconds(1) ||
                (modeStep is null ? mode.ParsedMode != session.Baseline.PerformanceMode :
                    !ExitSame(SettingRequest.Set(mode.ParsedMode), modeStep.Target) &&
                    (modeStep.Status == "Verified" || !ExitSame(SettingRequest.Set(mode.ParsedMode), modeStep.Expected))))
                return "NativeModeLiveStateUnverified";
            var controls = await provider.ReadControlBaselineAsync(token, includeCpuAdvanced: true);
            if (controls.PerformanceMode != mode.ParsedMode) return "NativeModeLiveStateUnverified";
            var gpuBefore = interruptedGpu is { Step.Status: not "Verified" } && controls.GpuVf is { Nodes.Length: 127 } gpu
                ? controls with { GpuVf = GpuAttemptBefore(gpu, interruptedGpu.Attempt) } : controls;
            bool fanIdleNoMutation = nativeJournal is not null &&
                await VerifyIdleFanReleaseAsync(session, plan.OperationId, controls, token, nativeJournal);
            foreach (var step in plan.Steps)
            {
                var observed = step.Key.StartsWith("gpu.") ? gpuBefore : controls;
                if (!ExitMatches(step, observed, true, fanIdleNoMutation) &&
                    (step.Status == "Verified" || !ExitMatches(step, observed, false) && !ExitCpuPartialMatches(step, observed)))
                    return "PartialExitLiveStateConflict:" + step.Key;
            }
            var baseline = new HomeControlState(session.Baseline.PerformanceMode, session.Baseline.StrongCooling, session.Baseline.QuickSettings)
                { CpuTuning = session.Baseline.CpuTuning, ActiveFanControlPlan = session.Baseline.ActiveFanControlPlan };
            foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Fan })
            {
                if (page == ControlPageId.Fan && plan.Steps.Any(step => step.Key == "fan")) continue;
                var expected = ControlPlanCompiler.Read(page, baseline);
                var actual = ControlPlanCompiler.Read(page, controls);
                string prefix = page.ToString().ToLowerInvariant() + ".";
                foreach (string field in expected.Keys.Union(actual.Keys))
                {
                    if (page == ControlPageId.Performance && windows is not null && IsWindowsIndexField(field) ||
                        plan.Steps.Any(step => step.Key == prefix + field)) continue;
                    if (!ExitSame(expected.GetValueOrDefault(field), actual.GetValueOrDefault(field)))
                        return "PartialExitLiveStateConflict:" + prefix + field;
                }
            }
            if (!await PriorWindowsLiveStateMatchesAsync(windows, token)) return "WindowsScopeLiveStateConflict";
        }
        return null;
    }
}
