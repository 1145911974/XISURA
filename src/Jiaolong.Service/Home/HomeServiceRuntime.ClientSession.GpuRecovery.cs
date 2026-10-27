using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static string[] PriorPendingGpuFields(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        var step = CompoundPageStep(saved, ControlPageId.Gpu);
        if (saved.Unresolved is not [var pending] || pending.GpuAttempt is not { } attempt || pending.GpuRecovery is not { } first ||
            attempt.StepId != (step?.Command.OperationId ?? pending.OperationId) || first.SessionId != session.ServiceSessionId || attempt.Before.SessionId != session.ServiceSessionId ||
            first.HardwareEpoch != session.Handle.HardwareEpoch || attempt.Before.HardwareEpoch != session.Handle.HardwareEpoch ||
            first.OwnerSid != session.OwnerSid || attempt.Before.OwnerSid != session.OwnerSid ||
            first.Identity != session.Identity || attempt.Before.Identity != session.Identity || first.Adapter != attempt.Before.Adapter ||
            pending.OwnerSid != session.OwnerSid || pending.Identity != session.Identity || pending.State.HardwareEpoch != session.Handle.HardwareEpoch ||
            saved.Current is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved.Current), JsonSerializer.SerializeToElement(pending.State)) ||
            pending.State.NativeModeApplyState == ApplyState.Unknown || !CompletedCompoundMode(session, saved) ||
            step is null && pending.State.Pages.Any(page => page.Page != ControlPageId.Gpu || page.Key is not null || page.Revision != 0) ||
            pending.State.Fields.Any(pair => pair.Value.WriteState == ApplyState.Unknown && pair.Value.OwnerOperationId != pending.OperationId))
            return [];
        string[] fields = (step?.Command.GetType().Name ?? pending.CommandKind) switch
        {
            nameof(SetGpuCoreOffsetCommand) when attempt.Kind == "core" => ["coreOffsetKhz", "vfOffsetsKhz"],
            nameof(SetGpuMemoryOffsetCommand) when attempt.Kind == "memory" => ["memoryOffsetKhz"],
            nameof(SetGpuVfCurveCommand) when attempt.Kind == "vf" => ["vfOffsetsKhz"],
            _ => []
        };
        if (fields.Length == 0 || (step?.Fields.Length ?? pending.State.Fields.Count(pair => pair.Value.OwnerOperationId == pending.OperationId)) != fields.Length)
            return [];
        SettingRequest Target(string field) => field switch
        {
            "coreOffsetKhz" => SettingRequest.Set(attempt.Pstate.CoreOffsetKhz),
            "memoryOffsetKhz" => SettingRequest.Set(attempt.Pstate.MemoryOffsetKhz),
            _ => SettingRequest.Set(attempt.VfOffsetsKhz)
        };
        foreach (string field in fields)
        {
            string key = "gpu." + field;
            if (!pending.State.Fields.TryGetValue(key, out var persisted) || persisted.ClientSessionId != session.Handle.SessionId ||
                persisted.OwnerOperationId != pending.OperationId || !ExitSame(persisted.LastWritten, Target(field)))
                return [];
        }
        return fields;
    }

    private async Task<bool> RecoverPriorManualGpuAsync(ClientControlSessionSnapshot session, ControlJournalSnapshot saved, CancellationToken token)
    {
        string[] fields = PriorPendingGpuFields(session, saved);
        if (fields.Length == 0) return false;
        var pending = saved.Unresolved.Single();
        var attempt = pending.GpuAttempt!;
        foreach (string field in fields)
        {
            string key = "gpu." + field;
            var persisted = pending.State.Fields[key];
            if (
                !ownedFields.TryGetValue(key, out var owner) || owner.OperationId != pending.OperationId ||
                owner.ClientSessionId != session.Handle.SessionId || owner.OwnerSid != session.OwnerSid || owner.Epoch != session.Handle.HardwareEpoch ||
                !ExitSame(owner.LastWritten, persisted.LastWritten))
                return false;
        }
        return await Execution.WithRecoveryAdmissionAsync(pending.OperationId, session.OwnerSid, async () =>
        {
            using var scope = journal!.BindGpuOperation(new(pending.OperationId, JournalServiceSessionId, JournalHardwareEpoch, session.OwnerSid));
            bool previouslyDispatched = hardwareDispatched.Value?.Started == true;
            var recovered = await TrackHardwareDispatchAsync(() => provider.RecoverPendingGpuAsync(pending.OperationId, token));
            if (!previouslyDispatched && recovered.Mutation == HardwareMutation.NoMutation && hardwareDispatched.Value is { } dispatch)
                dispatch.Started = false;
            if (recovered is not { State: CommandState.Applied, Error: null } || !await provider.ReconcileGpuRecoveryAsync(token)) return false;
            var controls = await ReadControlBaselineForExecutionAsync(token, true);
            var before = attempt.Before;
            if (controls.GpuVf is not { Error: null, Nodes.Length: 127 } gpu || gpu.CoreOffsetKhz != before.Pstate.CoreOffsetKhz ||
                gpu.MemoryOffsetKhz != before.Pstate.MemoryOffsetKhz || !gpu.Nodes.Select(node => node.OffsetKhz).SequenceEqual(before.VfOffsetsKhz))
                return false;
            bool admitted = CheckedPriorRestore() is { } prior ? ClientControlSessionStore.SameCapture(session, prior.Session) : SessionEpochMatches(session);
            if (!admitted || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved),
                JsonSerializer.SerializeToElement(await new ControlExecutionJournal(journal.RootDirectory).ReadAsync(token))))
                return false;
            var priorState = controlExecutionState;
            var priorOwners = fields.ToDictionary(field => "gpu." + field, field => ownedFields["gpu." + field]);
            try
            {
                var observed = ControlPlanCompiler.Read(ControlPageId.Gpu, controls);
                foreach (string field in fields)
                {
                    string key = "gpu." + field;
                    ownedFields[key] = ownedFields[key] with { LastWritten = observed[field], State = ApplyState.Confirmed };
                }
                Volatile.Write(ref controlExecutionState, pending.State with { Fields = SnapshotFields(), Pages = [], PendingStep = null });
                await PersistExecutionAsync(pending.OperationId, false, token, commandKind: pending.CommandKind);
                Volatile.Write(ref state, State with { Controls = controls });
                return true;
            }
            catch
            {
                foreach (var pair in priorOwners) ownedFields[pair.Key] = pair.Value;
                Volatile.Write(ref controlExecutionState, priorState);
                throw;
            }
        });
    }
}
