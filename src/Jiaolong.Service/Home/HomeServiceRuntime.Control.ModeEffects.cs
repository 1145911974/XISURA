using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private async Task RecordNativeModeEffectsAsync(Guid operationId, PerformanceMode mode, HomeControlState before,
        CommandResult result, CancellationToken token)
    {
        var session = Volatile.Read(ref activeClientSession);
        // This acknowledges the OEM request byte; the CPU/lighting observations below prove only their readback.
        bool acknowledged = result.ModeEvidence is { VerificationLevel: "InterfaceAcknowledged" } evidence &&
            evidence.RequestedMode == mode && evidence.ParsedMode == mode;
        if (session is null || session.Handle.SessionId != AdmittedClientSessionId ||
            result is not { State: CommandState.Applied, Error: null } || result.Mutation == HardwareMutation.NoMutation ||
            result.VerifiedPerformanceMode != mode && !acknowledged) return;

        var after = await ReadControlBaselineForExecutionAsync(token, true);
        var confirmed = await ReadControlBaselineForExecutionAsync(token, true);
        if (after.PerformanceMode != mode || confirmed.PerformanceMode != mode) return;
        var baseline = new HomeControlState(session.Baseline.PerformanceMode, session.Baseline.StrongCooling, session.Baseline.QuickSettings)
        { CpuTuning = session.Baseline.CpuTuning, KeyboardLighting = session.Baseline.KeyboardLighting };
        var admission = Execution.CurrentAdmission!;
        foreach (var page in new[] { ControlPageId.Performance, ControlPageId.Lighting })
        {
            if (page == ControlPageId.Lighting && (before.KeyboardLighting is not { } oldLight || !oldLight.IsValid() ||
                after.KeyboardLighting is not { } newLight || !newLight.IsValid() || before.KeyboardLightingPreviewActive ||
                after.KeyboardLightingPreviewActive || confirmed.KeyboardLightingPreviewActive)) continue;
            var original = ControlPlanCompiler.Read(page, baseline);
            var prior = ControlPlanCompiler.Read(page, before);
            var observed = ControlPlanCompiler.Read(page, after);
            var stable = ControlPlanCompiler.Read(page, confirmed);
            foreach (var pair in observed)
            {
                string field = pair.Key;
                // Windows indices and scheme changes require their existing durable scope, not scalar ownership.
                if (page == ControlPageId.Performance && (IsWindowsIndexField(field) || field == "windowsPowerSchemeId")) continue;
                if (!FieldReadable(field) || !prior.TryGetValue(field, out var previousValue) ||
                    !stable.TryGetValue(field, out var confirmedValue)) continue;
                var beforeDetails = page == ControlPageId.Performance ? CaptureCpuRestoration(field, before.CpuTuning) : null;
                var afterDetails = page == ControlPageId.Performance ? CaptureCpuRestoration(field, after.CpuTuning) : null;
                if (field == "negativeCurveOptimizer" && (beforeDetails is null || afterDetails is null)) continue;
                bool Same(SettingRequest? left, SettingRequest? right, CpuFieldRestorationValue? leftDetails, CpuFieldRestorationValue? rightDetails) =>
                    leftDetails is null && rightDetails is null ? ExitSame(left, right) : SameRestoration(leftDetails, rightDetails);
                if (Same(previousValue, pair.Value, beforeDetails, afterDetails) ||
                    !Same(pair.Value, confirmedValue, afterDetails,
                        page == ControlPageId.Performance ? CaptureCpuRestoration(field, confirmed.CpuTuning) : null)) continue;
                string key = FieldKey(page, field);
                ownedFields.TryGetValue(key, out var owner);
                if (owner is not null)
                {
                    if (!SameClientSession(owner) || owner.OwnerSid != admission.Owner || owner.Epoch != Execution.HardwareEpoch ||
                        owner.State != ApplyState.Confirmed || !MatchesOwnedValue(owner, previousValue, before.CpuTuning) ||
                        !IsDirectCpuScalar(field) && owner.Scheme != before.CpuTuning?.WindowsPowerSchemeId) continue;
                }
                else if (!Same(previousValue, original.GetValueOrDefault(field), beforeDetails,
                    page == ControlPageId.Performance ? CaptureCpuRestoration(field, baseline.CpuTuning) : null)) continue;
                ownedFields[key] = new(operationId, owner?.Origin ?? ControlOrigin.ManualLive, owner is null ? previousValue : owner.Baseline, pair.Value,
                    ApplyState.Confirmed, Execution.HardwareEpoch, after.CpuTuning?.WindowsPowerSchemeId, admission.Generation,
                    owner is null ? beforeDetails : owner.BaselineDetails, afterDetails)
                {
                    OwnerSid = admission.Owner, ClientSessionId = AdmittedClientSessionId,
                    WriteBefore = previousValue, WriteBeforeDetails = beforeDetails, WriteTargetDetails = afterDetails
                };
            }
        }
        Volatile.Write(ref controlExecutionState, controlExecutionState! with { Fields = SnapshotFields() });
        await PersistExecutionAsync(operationId, true, token);
    }
}
