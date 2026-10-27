using Jiaolong.Contracts.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiaolong.Service.Home;

public sealed record FanEcRecoveryCheckpoint(Dictionary<ushort, byte> Baseline, Dictionary<ushort, byte> Expected,
    Dictionary<ushort, byte[]> Pending, bool Released)
{
    public bool Unchanged { get; init; }
    public FanEcMappingLease? MappingLease { get; init; }
}
public sealed record FanEcMappingLease([property: JsonRequired] byte Original, [property: JsonRequired] byte Expected,
    [property: JsonRequired] byte[] Pending, [property: JsonRequired] bool TargetsCaptured, [property: JsonRequired] bool Restored)
{
    [JsonRequired] public bool TargetWritesStarted { get; init; }
}
public sealed record FanEcRecoveryScope(Guid ServiceSessionId, long HardwareEpoch, Guid ClientSessionId,
    string OwnerSid, HardwareIdentity Identity);
public sealed record FanEcRecoverySnapshot(FanEcRecoveryScope Scope, FanEcRecoveryCheckpoint Checkpoint);

public sealed partial class ControlExecutionJournal
{
    internal static bool IsFanFirmwareOnlyRecovery(ClientControlSessionSnapshot capture, ControlJournalSnapshot saved) =>
        capture.Baseline.StrongCooling == true && (saved.FanEcRecovery is null ||
            saved.FanEcRecovery.Scope.ClientSessionId != capture.Handle.SessionId &&
            saved.FanEcRecovery.Scope.Identity == capture.Identity && FanCheckpointComplete(saved.FanEcRecovery.Checkpoint)) &&
        saved.FanFirmwareRecovery is { OriginalBefore: 1 } firmware && firmware.Scope ==
            new FanEcRecoveryScope(capture.ServiceSessionId, capture.Handle.HardwareEpoch, capture.Handle.SessionId, capture.OwnerSid, capture.Identity);

    internal async Task RecordFanEcCheckpointAsync(FanEcRecoveryScope scope, FanEcRecoveryCheckpoint checkpoint, CancellationToken token)
    {
        var record = new FanEcRecoverySnapshot(scope, checkpoint);
        if (!ValidFanEcRecovery(record)) throw new InvalidDataException("fanEcCheckpointInvalid");
        await gate.WaitAsync(token);
        try
        {
            var saved = await LoadAsync(token);
            var fresh = await new ControlExecutionJournal(RootDirectory).ReadAsync(token);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved), JsonSerializer.SerializeToElement(fresh)))
                throw new InvalidDataException("fanEcJournalChangedOrMissing");
            var fields = saved.Current?.Fields.Where(pair => pair.Key.StartsWith("fan.", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (saved.Identity != scope.Identity || saved.Current?.HardwareEpoch != scope.HardwareEpoch ||
                saved.RecoveryScope?.ServiceSessionId != scope.ServiceSessionId || fields is not { Length: > 0 } ||
                fields.Any(pair => pair.Value.ClientSessionId != scope.ClientSessionId ||
                    !saved.RecoveryScope.Fields.TryGetValue(pair.Key, out var owner) || owner.OwnerSid != scope.OwnerSid ||
                    owner.HardwareEpoch != scope.HardwareEpoch))
                throw new InvalidDataException("fanEcCheckpointAuthorityMismatch");
            if (saved.FanEcRecovery is { } prior)
            {
                if (prior.Scope != scope)
                {
                    if (prior.Scope.Identity != scope.Identity) throw new InvalidDataException("fanEcDeviceChanged");
                    await VerifyArchivedFanEcAsync(prior, token);
                }
                else if (!SameFanCheckpointBaseline(prior.Checkpoint, checkpoint))
                    throw new InvalidDataException("fanEcBaselineChanged");
            }
            var next = Clone(saved with { FanEcRecovery = record });
            await WriteSnapshotAsync(next, token);
            snapshot = next;
        }
        finally { gate.Release(); }
    }

    private static bool ValidFanEcRecovery(FanEcRecoverySnapshot record)
    {
        if (record.Scope is not { ServiceSessionId: var service, HardwareEpoch: > 0, ClientSessionId: var client, Identity: not null } scope ||
            service == Guid.Empty || client == Guid.Empty || string.IsNullOrWhiteSpace(scope.OwnerSid) ||
            !ValidFanCheckpoint(record.Checkpoint))
            return false;
        return true;
    }

    internal static bool ValidFanCheckpoint(FanEcRecoveryCheckpoint? checkpoint)
    {
        if (checkpoint is not { Baseline: not null, Expected: not null, Pending: not null }) return false;
        if (checkpoint.MappingLease is { } mapping)
        {
            if ((mapping.Original & 0x80) != 0 || mapping.Pending is not { Length: <= 2 } ||
                mapping.Pending.Distinct().Count() != mapping.Pending.Length ||
                mapping.Pending.Any(value => (value & 0x7F) != (mapping.Expected & 0x7F)) ||
                mapping.Pending.Length > 0 && !mapping.Pending.Contains(mapping.Expected) ||
                mapping.Restored && (mapping.Pending.Length != 0 || (mapping.Expected & 0x80) != (mapping.Original & 0x80)) ||
                checkpoint.Released && checkpoint.Unchanged)
                return false;
            if (!mapping.TargetsCaptured)
                return checkpoint.Baseline.Count == 0 && checkpoint.Expected.Count == 0 && checkpoint.Pending.Count == 0 &&
                    !mapping.TargetWritesStarted && !checkpoint.Released && checkpoint.Unchanged == mapping.Restored;
            if (!checkpoint.Baseline.TryGetValue(0x1060, out byte baselineMapping) || (baselineMapping & 0x80) == 0 ||
                !checkpoint.Expected.TryGetValue(0x1060, out byte expectedMapping) || expectedMapping != mapping.Expected ||
                checkpoint.Pending.ContainsKey(0x1060) || (checkpoint.Released || checkpoint.Unchanged) != mapping.Restored)
                return false;
            ushort[] targets = [0x0B20, 0xC83C, 0xC83D];
            if (targets.Any(key => !checkpoint.Baseline.ContainsKey(key) || !checkpoint.Expected.ContainsKey(key))) return false;
            if (!mapping.TargetWritesStarted && (checkpoint.Released || checkpoint.Pending.Count != 0 || targets.Any(key =>
                    (checkpoint.Expected[key] & FanOwnedMask(key)) != (checkpoint.Baseline[key] & FanOwnedMask(key)))) ||
                mapping.TargetWritesStarted && checkpoint.Unchanged)
                return false;
            // Once mapping restoration starts, no target write or unconfirmed release may remain.
            if ((mapping.Pending.Length > 0 || (mapping.Expected & 0x80) == 0) &&
                (checkpoint.Pending.Count != 0 || mapping.Pending.Length > 0 && (mapping.Expected & 0x80) != 0 ||
                 mapping.TargetWritesStarted && !FanTargetsReleased(checkpoint.Expected)))
                return false;
        }
        ushort[] keys = [0x1060, 0x0B20, 0xC83C, 0xC83D];
        if (checkpoint.Baseline.Count != 4 || checkpoint.Expected.Count != 4 ||
            keys.Any(key => !checkpoint.Baseline.ContainsKey(key) || !checkpoint.Expected.ContainsKey(key)) ||
            checkpoint.Pending.Any(pair => !keys.Contains(pair.Key) || pair.Value is not { Length: > 0 and <= 256 } ||
                pair.Value.Distinct().Count() != pair.Value.Length || !pair.Value.Contains(checkpoint.Expected[pair.Key])))
            return false;
        if (checkpoint.Unchanged)
            return !checkpoint.Released && checkpoint.Pending.Count == 0 && keys.All(key =>
                (checkpoint.Expected[key] & FanOwnedMask(key)) ==
                ((key == 0x1060 ? checkpoint.MappingLease?.Original ?? checkpoint.Baseline[key] : checkpoint.Baseline[key]) & FanOwnedMask(key)));
        return !checkpoint.Released || checkpoint.Pending.Count == 0 && FanTargetsReleased(checkpoint.Expected) &&
            (checkpoint.Expected[0x1060] & 0x80) == ((checkpoint.MappingLease?.Original ?? checkpoint.Baseline[0x1060]) & 0x80);
    }

    private static bool SameFanCheckpointBaseline(FanEcRecoveryCheckpoint prior, FanEcRecoveryCheckpoint next, bool allowPromotion = true)
    {
        if (prior.MappingLease is { } before)
        {
            if (next.MappingLease is not { } after || before.Original != after.Original ||
                before.TargetWritesStarted && !after.TargetWritesStarted ||
                !allowPromotion && before.TargetWritesStarted != after.TargetWritesStarted) return false;
            if (before.TargetsCaptured != after.TargetsCaptured)
                return allowPromotion && !before.TargetsCaptured && after.TargetsCaptured && !before.Restored &&
                    !after.TargetWritesStarted && before.Pending.Length == 0 && (before.Expected & 0x80) != 0 && after.Pending.Length == 0 &&
                    next.Baseline.TryGetValue(0x1060, out byte mapping) && mapping == before.Expected && after.Expected == mapping;
        }
        else if (next.MappingLease is not null) return false;
        return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(prior.Baseline), JsonSerializer.SerializeToElement(next.Baseline));
    }

    private static int FanOwnedMask(ushort address) => address == 0x1060 ? 0x80 : address == 0x0B20 ? 0x0A : 0xFF;
    private static bool FanTargetsReleased(Dictionary<ushort, byte> values) =>
        values[0xC83C] == 0 && values[0xC83D] == 0 && (values[0x0B20] & 0x0A) == 0;
}
