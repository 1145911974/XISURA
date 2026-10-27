using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static PendingControlStep? PriorPendingLighting(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        var step = CompoundPageStep(saved, ControlPageId.Lighting);
        if (step is null || saved.Unresolved is not [var pending] || pending.OwnerSid != session.OwnerSid || pending.Identity != session.Identity ||
            !CompletedCompoundMode(session, saved) || !CompletedCompoundGpu(session, saved) ||
            step.Fields.Any(key => saved.Current!.Fields[key].WriteBefore is null || saved.Current.Fields[key].LastWritten is null ||
                saved.Current.Fields[key].ClientSessionId != session.Handle.SessionId ||
                saved.RecoveryScope?.Fields.GetValueOrDefault(key) is not { } owner ||
                owner.OwnerSid != session.OwnerSid || owner.HardwareEpoch != session.Handle.HardwareEpoch)) return null;
        return step;
    }

    private static byte[]? LightRgb(SettingRequest? request)
    {
        if (request?.Value is not { ValueKind: JsonValueKind.String } value || value.GetString() is not { Length: 7 } text || text[0] != '#') return null;
        try { return Convert.FromHexString(text.AsSpan(1)); }
        catch (FormatException) { return null; }
    }

    private static bool PendingLightingLiveMatches(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, HomeControlState actual)
    {
        var step = PriorPendingLighting(session, saved);
        if (step is null || actual.KeyboardLighting is null || actual.KeyboardLightingPreviewActive || session.Baseline.KeyboardLighting is null) return false;
        var observed = ControlPlanCompiler.Read(ControlPageId.Lighting, actual);
        var original = ControlPlanCompiler.Read(ControlPageId.Lighting, new(null, null, []) { KeyboardLighting = session.Baseline.KeyboardLighting });
        foreach (string key in step.Fields)
        {
            var field = saved.Current!.Fields[key];
            var current = observed.GetValueOrDefault(key[9..]);
            var baseline = original.GetValueOrDefault(key[9..]);
            if (key == "lighting.hex")
            {
                var colors = new[] { LightRgb(field.WriteBefore), LightRgb(field.LastWritten), LightRgb(baseline) };
                if (LightRgb(current) is not { } rgb || colors.Any(color => color is null) ||
                    Enumerable.Range(0, 3).Any(index => !colors.Any(color => color![index] == rgb[index]))) return false;
            }
            else if (baseline is null || !ExitSame(current, field.WriteBefore) && !ExitSame(current, field.LastWritten) && !ExitSame(current, baseline)) return false;
        }
        return true;
    }

    private Task<bool> ReconcilePriorPendingLightingAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, CancellationToken token) =>
        Execution.WithRecoveryAdmissionAsync(saved.Unresolved.Single().OperationId, session.OwnerSid, async () =>
        {
            var step = PriorPendingLighting(session, saved);
            if (step is null) return false;
            HomeControlState controls = State.Controls;
            for (int observation = 0; observation < 2; observation++)
            {
                controls = await ReadControlBaselineForExecutionAsync(token, true);
                if (!PendingLightingLiveMatches(session, saved, controls)) return false;
            }
            await VerifyPriorRestoreWriteAsync();
            if (!SamePriorJournal(saved, await new ControlExecutionJournal(journal!.RootDirectory).ReadAsync(token))) return false;
            var priorState = controlExecutionState;
            var owners = step.Fields.ToDictionary(key => key, key => ownedFields[key]);
            try
            {
                var observed = ControlPlanCompiler.Read(ControlPageId.Lighting, controls);
                foreach (string key in step.Fields) ownedFields[key] = ownedFields[key] with
                    { LastWritten = observed[key[9..]], State = ApplyState.Confirmed };
                Volatile.Write(ref controlExecutionState, controlExecutionState! with { Fields = SnapshotFields(), Pages = [], PendingStep = null });
                var pending = saved.Unresolved.Single();
                await PersistExecutionAsync(pending.OperationId, false, token, commandKind: pending.CommandKind);
                Volatile.Write(ref state, State with { Controls = controls });
                return true;
            }
            catch
            {
                foreach (var pair in owners) ownedFields[pair.Key] = pair.Value;
                Volatile.Write(ref controlExecutionState, priorState);
                throw;
            }
        });
}
