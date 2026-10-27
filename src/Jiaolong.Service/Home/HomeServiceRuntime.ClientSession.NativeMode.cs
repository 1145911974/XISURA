using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static NativeModeRecoveryAttempt? PriorPendingMode(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending] || pending.CommandKind != nameof(SetPerformanceModeCommand) ||
            pending.NativeModeAttempt is not { } attempt || attempt.StepId != pending.OperationId ||
            attempt.ClientSessionId != session.Handle.SessionId || attempt.ServiceSessionId != session.ServiceSessionId ||
            attempt.HardwareEpoch != session.Handle.HardwareEpoch || attempt.OwnerSid != session.OwnerSid || attempt.Identity != session.Identity ||
            pending.GpuRecovery is not null || pending.GpuAttempt is not null || pending.State.Pages.Length != 0 ||
            saved.Current is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved.Current), JsonSerializer.SerializeToElement(pending.State)) ||
            pending.State.Fields.Values.Any(field => field.OwnerOperationId == pending.OperationId || field.WriteState == ApplyState.Unknown))
            return null;
        return attempt;
    }

    private async Task<bool> ReconcilePriorNativeModeAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, CancellationToken token)
    {
        var attempt = PriorPendingMode(session, saved);
        if (attempt is null) return false;
        var pending = saved.Unresolved.Single();
        var started = DateTimeOffset.UtcNow;
        bool Fresh(PerformanceModeEvidence? evidence) => evidence?.ParsedMode is PerformanceMode mode &&
            mode is PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo && evidence.RawValue is byte raw &&
            MiPerformanceModeCodec.Decode(raw) == mode && evidence.CapturedAtUtc >= started.AddSeconds(-1) &&
            evidence.CapturedAtUtc <= DateTimeOffset.UtcNow.AddSeconds(1);
        EnsureControlHardwareAccess();
        var first = await provider.ReadNativeModeEvidenceAsync(token);
        EnsureControlHardwareAccess();
        if (!Fresh(first) || first!.ParsedMode != attempt.Before && first.ParsedMode != attempt.Target) return false;
        var controls = await ReadControlBaselineForExecutionAsync(token, true);
        if (controls.PerformanceMode != first.ParsedMode || !NativeModeAdjacentStateMatches(session, controls)) return false;
        var second = await provider.ReadNativeModeEvidenceAsync(token);
        EnsureControlHardwareAccess();
        if (!Fresh(second) || second!.ParsedMode != first.ParsedMode) return false;
        controls = await ReadControlBaselineForExecutionAsync(token, true);
        bool admitted = CheckedPriorRestore() is { } prior ? ClientControlSessionStore.SameCapture(session, prior.Session) : SessionEpochMatches(session);
        if (controls.PerformanceMode != second.ParsedMode || !NativeModeAdjacentStateMatches(session, controls) || !admitted ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved),
                JsonSerializer.SerializeToElement(await new ControlExecutionJournal(journal!.RootDirectory).ReadAsync(token))))
            return false;
        return await Execution.WithRecoveryAdmissionAsync(pending.OperationId, session.OwnerSid, async () =>
        {
            var priorState = controlExecutionState;
            var priorMode = lastSubmittedMode;
            var priorOwner = lastSubmittedModeClientSessionId;
            long priorEpoch = lastModeEpoch;
            try
            {
                if (second.ParsedMode == attempt.Target && attempt.Before != attempt.Target)
                { lastSubmittedMode = attempt.Target; lastSubmittedModeClientSessionId = session.Handle.SessionId; lastModeEpoch = JournalHardwareEpoch; }
                else if (lastSubmittedMode != second.ParsedMode)
                { lastSubmittedMode = null; lastSubmittedModeClientSessionId = null; }
                // Interface state is known; performance effect is still not independently verified.
                Volatile.Write(ref controlExecutionState, pending.State with { NativeMode = second.ParsedMode!.Value,
                    NativeModeApplyState = ApplyState.SubmittedUnverified, Fields = SnapshotFields() });
                await PersistExecutionAsync(pending.OperationId, false, token, commandKind: pending.CommandKind);
                Volatile.Write(ref state, State with { Controls = controls with { ModeEvidence = second } });
                return true;
            }
            catch
            {
                lastSubmittedMode = priorMode; lastSubmittedModeClientSessionId = priorOwner; lastModeEpoch = priorEpoch;
                Volatile.Write(ref controlExecutionState, priorState);
                throw;
            }
        });
    }

    private bool NativeModeAdjacentStateMatches(ClientControlSessionSnapshot session, HomeControlState controls)
    {
        if (session.Baseline.CpuTuning is null || controls.CpuTuning is null || session.Baseline.KeyboardLighting is null ||
            controls.KeyboardLighting is null) return false;
        var baseline = new HomeControlState(session.Baseline.PerformanceMode, session.Baseline.StrongCooling, session.Baseline.QuickSettings)
        { CpuTuning = session.Baseline.CpuTuning, GpuVf = session.Baseline.GpuVf, KeyboardLighting = session.Baseline.KeyboardLighting,
            ActiveFanControlPlan = session.Baseline.ActiveFanControlPlan };
        foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Gpu, ControlPageId.Lighting, ControlPageId.Fan })
        {
            var expected = ControlPlanCompiler.Read(page, baseline);
            foreach (var pair in ownedFields.Where(pair => pair.Key.StartsWith(page.ToString().ToLowerInvariant() + ".")))
            {
                if (pair.Value.ClientSessionId != session.Handle.SessionId || pair.Value.OwnerSid != session.OwnerSid ||
                    pair.Value.Epoch != session.Handle.HardwareEpoch || pair.Value.State == ApplyState.Unknown || pair.Value.LastWritten is null)
                    return false;
                expected[pair.Key[(pair.Key.IndexOf('.') + 1)..]] = pair.Value.LastWritten;
            }
            var actual = ControlPlanCompiler.Read(page, controls);
            if (expected.Count != actual.Count || expected.Any(pair =>
                pair.Key is "isBoostEnabled" or "negativeCurveOptimizer"
                    ? !FieldMatches(pair.Key, actual.GetValueOrDefault(pair.Key), pair.Value, controls.CpuTuning)
                    : !ExitSame(pair.Value, actual.GetValueOrDefault(pair.Key)))) return false;
        }
        foreach (string field in new[] { "isBoostEnabled", "negativeCurveOptimizer" })
        {
            var expected = ownedFields.GetValueOrDefault("performance." + field)?.LastDetails ?? CaptureCpuRestoration(field, baseline.CpuTuning);
            if (expected is null || !SameRestoration(expected, CaptureCpuRestoration(field, controls.CpuTuning))) return false;
        }
        return !controls.KeyboardLightingPreviewActive;
    }
}
