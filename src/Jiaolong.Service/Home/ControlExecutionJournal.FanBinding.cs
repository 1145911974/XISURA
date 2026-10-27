using Jiaolong.Contracts.Models;
using Jiaolong.Service.Storage;
using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed partial class ControlExecutionJournal
{
    internal async Task<(FanEcRecoverySnapshot Recovery, Action<FanEcRecoveryCheckpoint> Writer)> CreateFanEcRecoveryCheckpointWriterAsync(
        HardwareIdentity identity, CancellationToken token)
    {
        var capture = await ValidateFanCaptureAsync(identity, token);
        var scope = new FanEcRecoveryScope(capture.ServiceSessionId, capture.Handle.HardwareEpoch,
            capture.Handle.SessionId, capture.OwnerSid, identity);
        var prior = (await ReadAsync(token)).FanEcRecovery;
        if (capture.Phase != ClientControlSessionPhase.Closing || prior is null || prior.Scope != scope)
            throw new InvalidDataException("fanRecoveryCaptureOrCheckpointMissing");
        bool first = true;
        void Writer(FanEcRecoveryCheckpoint checkpoint)
        {
            var current = ValidateFanCaptureAsync(identity, CancellationToken.None).GetAwaiter().GetResult();
            if (current.Phase != ClientControlSessionPhase.Closing || !ClientControlSessionStore.SameCapture(capture, current) ||
                !ValidFanCheckpoint(checkpoint) || !SameFanCheckpointBaseline(prior.Checkpoint, checkpoint, allowPromotion: false) ||
                !FanCheckpointWritesOnlyRelease(checkpoint) || first && !FanRecoveryObservationMatches(prior.Checkpoint, checkpoint))
                throw new InvalidDataException("fanRecoveryCheckpointAuthorityChanged");
            RecordFanEcCheckpointAsync(scope, checkpoint, CancellationToken.None).GetAwaiter().GetResult();
            first = false;
        }
        // Returned dictionaries may be reused by a transport; keep the authority snapshot private.
        var recovery = prior with { Checkpoint = prior.Checkpoint with { Baseline = new(prior.Checkpoint.Baseline),
            Expected = new(prior.Checkpoint.Expected), Pending = prior.Checkpoint.Pending.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
            MappingLease = prior.Checkpoint.MappingLease is { } mapping ? mapping with { Pending = mapping.Pending.ToArray() } : null } };
        return (recovery, Writer);
    }

    private static bool FanCheckpointWritesOnlyRelease(FanEcRecoveryCheckpoint checkpoint) =>
        (checkpoint.MappingLease is not { Pending.Length: > 0 } mapping || (mapping.Expected & 0x80) == (mapping.Original & 0x80)) &&
        !checkpoint.Pending.Keys.Any(key =>
        key is 0xC83C or 0xC83D ? checkpoint.Expected[key] != 0 :
        key == 0x0B20 ? (checkpoint.Expected[key] & 0x0A) != 0 :
        key == 0x1060 && (checkpoint.Expected[key] & 0x80) != (checkpoint.Baseline[key] & 0x80));

    private static bool FanRecoveryObservationMatches(FanEcRecoveryCheckpoint prior, FanEcRecoveryCheckpoint observation)
    {
        if (prior.MappingLease is { } mapping)
        {
            if (observation.MappingLease is not { } actual || mapping.Original != actual.Original ||
                mapping.TargetsCaptured != actual.TargetsCaptured || mapping.TargetWritesStarted != actual.TargetWritesStarted ||
                (actual.Expected & 0x80) != (mapping.Expected & 0x80) &&
                !mapping.Pending.Any(value => (value & 0x80) == (actual.Expected & 0x80))) return false;
            if (!mapping.TargetsCaptured) return true;
        }
        else if (observation.MappingLease is not null) return false;
        var observed = observation.Expected;
        return observed[0xC83C] == 0 && observed[0xC83D] == 0 && (observed[0x0B20] & 0x0A) == 0 &&
        (observed[0x1060] & 0x80) == ((prior.MappingLease?.Original ?? prior.Baseline[0x1060]) & 0x80) ||
        observed.All(pair => (pair.Value & FanOwnedMask(pair.Key)) == (prior.Expected[pair.Key] & FanOwnedMask(pair.Key)) ||
            pair.Key == 0x0B20 && (pair.Value & 0x0A & ~prior.Expected[pair.Key]) == 0 ||
            prior.Pending.TryGetValue(pair.Key, out var values) && values.Any(value =>
                (value & FanOwnedMask(pair.Key)) == (pair.Value & FanOwnedMask(pair.Key))));
    }

    internal async Task<ClientControlSessionSnapshot> ValidateFanCaptureAsync(HardwareIdentity identity, CancellationToken token)
    {
        var saved = await ReadAsync(token);
        var fresh = await new ControlExecutionJournal(RootDirectory).ReadAsync(token);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved), JsonSerializer.SerializeToElement(fresh)))
            throw new InvalidDataException("fanJournalChangedOrMissing");
        var capture = await new ClientControlSessionStore(RootDirectory).ReadAsync(token);
        var fields = saved.Current?.Fields.Where(pair => pair.Key.StartsWith("fan.", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (capture is null || capture.Phase is not (ClientControlSessionPhase.Active or ClientControlSessionPhase.Closing) ||
            capture.Identity != identity || saved.Identity != identity || saved.Current?.HardwareEpoch != capture.Handle.HardwareEpoch ||
            saved.RecoveryScope?.ServiceSessionId != capture.ServiceSessionId || fields is not { Length: > 0 } ||
            fields.Any(pair => pair.Value.ClientSessionId != capture.Handle.SessionId ||
                !saved.RecoveryScope.Fields.TryGetValue(pair.Key, out var owner) || owner.OwnerSid != capture.OwnerSid ||
                owner.HardwareEpoch != capture.Handle.HardwareEpoch))
            throw new InvalidDataException("fanCaptureAuthorityMismatch");
        return capture;
    }

    internal async Task<Action<FanEcRecoveryCheckpoint>> CreateFanEcCheckpointWriterAsync(HardwareIdentity identity, CancellationToken token)
    {
        var capture = await ValidateFanCaptureAsync(identity, token);
        var scope = new FanEcRecoveryScope(capture.ServiceSessionId, capture.Handle.HardwareEpoch,
            capture.Handle.SessionId, capture.OwnerSid, identity);
        var saved = await ReadAsync(token);
        var prior = saved.FanEcRecovery;
        if (prior is not null)
        {
            if (!FanCheckpointComplete(prior.Checkpoint)) throw new InvalidDataException("fanPriorLeaseNotResolved");
            if (prior.Scope != scope) await VerifyArchivedFanEcAsync(prior, token);
        }
        bool first = true;
        return checkpoint =>
        {
            var current = ValidateFanCaptureAsync(identity, CancellationToken.None).GetAwaiter().GetResult();
            if (!ClientControlSessionStore.SameCapture(capture, current)) throw new InvalidDataException("fanCaptureChanged");
            if (!ValidFanCheckpoint(checkpoint)) throw new InvalidDataException("fanEcCheckpointInvalid");
            if (current.Phase == ClientControlSessionPhase.Closing)
            {
                var closing = ReadAsync(CancellationToken.None).GetAwaiter().GetResult().FanEcRecovery;
                bool mappingLease = closing?.Checkpoint.MappingLease is not null || checkpoint.MappingLease is not null;
                if (!FanCheckpointWritesOnlyRelease(checkpoint) || mappingLease && (closing?.Scope != scope ||
                    !SameFanCheckpointBaseline(closing.Checkpoint, checkpoint, allowPromotion: false)))
                    throw new InvalidDataException("fanSessionClosing");
            }
            var normalized = checkpoint;
            if (prior?.Scope == scope)
            {
                if (prior.Checkpoint.MappingLease is not null || checkpoint.MappingLease is not null)
                {
                    if (first && (!SameFanCheckpointBaseline(prior.Checkpoint, checkpoint, allowPromotion: false) ||
                        !FanRecoveryObservationMatches(prior.Checkpoint, checkpoint)))
                        throw new InvalidDataException("fanReacquisitionOwnershipChanged");
                }
                else
                {
                    if (first && !SameFanOwnedValues(prior.Checkpoint.Expected, checkpoint.Baseline))
                        throw new InvalidDataException("fanReacquisitionOwnershipChanged");
                    normalized = checkpoint with { Baseline = new(prior.Checkpoint.Baseline) };
                    if (checkpoint.Unchanged && prior.Checkpoint.Released)
                        normalized = normalized with { Unchanged = false, Released = true };
                }
            }
            RecordFanEcCheckpointAsync(scope, normalized, CancellationToken.None).GetAwaiter().GetResult();
            first = false;
        };
    }

    internal static bool FanCheckpointComplete(FanEcRecoveryCheckpoint checkpoint) =>
        ValidFanCheckpoint(checkpoint) && (checkpoint.Released || checkpoint.Unchanged) && checkpoint.Pending.Count == 0 &&
        (checkpoint.MappingLease is null || checkpoint.MappingLease.Restored && checkpoint.MappingLease.Pending.Length == 0);
    private static bool SameFanOwnedValues(Dictionary<ushort, byte> left, Dictionary<ushort, byte> right) =>
        left.Count == 4 && right.Count == 4 && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            (pair.Value & FanOwnedMask(pair.Key)) == (value & FanOwnedMask(pair.Key)));

    private sealed record FanArchive(ClientControlSessionSnapshot Session, JsonElement Recovery, FanEcRecoverySnapshot? FanEcRecovery,
        FanFirmwareRecoverySnapshot? FanFirmwareRecovery);
    internal async Task VerifyFanFirmwareOnlyHistoryAsync(ClientControlSessionSnapshot capture, ControlJournalSnapshot saved, CancellationToken token)
    {
        if (IsFanFirmwareOnlyRecovery(capture, saved) && saved.FanEcRecovery is { } prior)
            await VerifyArchivedFanEcAsync(prior, token);
    }

    internal async Task VerifyIdleFanReleaseHistoryAsync(ClientControlSessionSnapshot capture, ControlJournalSnapshot saved, CancellationToken token)
    {
        var current = await new ClientControlSessionStore(RootDirectory).ReadAsync(token);
        if (current?.Phase != ClientControlSessionPhase.Closing || !ClientControlSessionStore.SameCapture(capture, current) ||
            saved.FanEcRecovery?.Scope.ClientSessionId == capture.Handle.SessionId ||
            saved.FanFirmwareRecovery?.Scope.ClientSessionId == capture.Handle.SessionId ||
            saved.FanEcRecovery is { } otherEc && otherEc.Scope.Identity != capture.Identity ||
            saved.FanFirmwareRecovery is { } otherFirmware && otherFirmware.Scope.Identity != capture.Identity)
            throw new InvalidDataException("idleFanReleaseHasWriteAuthority");
        if (saved.FanEcRecovery is { } ec) await VerifyArchivedFanEcAsync(ec, token);
        if (saved.FanFirmwareRecovery is { } firmware) await VerifyArchivedFanFirmwareAsync(firmware, token);
        current = await new ClientControlSessionStore(RootDirectory).ReadAsync(token);
        if (current?.Phase != ClientControlSessionPhase.Closing || !ClientControlSessionStore.SameCapture(capture, current))
            throw new InvalidDataException("idleFanReleaseCaptureChanged");
    }

    private async Task VerifyArchivedFanEcAsync(FanEcRecoverySnapshot prior, CancellationToken token)
    {
        if (!FanCheckpointComplete(prior.Checkpoint)) throw new InvalidDataException("fanPriorLeaseNotResolved");
        var saved = await ReadVerifiedFanArchiveAsync(prior.Scope, token);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved.FanEcRecovery), JsonSerializer.SerializeToElement(prior)))
            throw new InvalidDataException("fanPriorArchiveMismatch");
    }

    private async Task VerifyArchivedFanFirmwareAsync(FanFirmwareRecoverySnapshot prior, CancellationToken token)
    {
        if (!FanFirmwareComplete(prior)) throw new InvalidDataException("fanFirmwarePriorLeaseNotResolved");
        var saved = await ReadVerifiedFanArchiveAsync(prior.Scope, token);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved.FanFirmwareRecovery), JsonSerializer.SerializeToElement(prior)))
            throw new InvalidDataException("fanFirmwarePriorArchiveMismatch");
    }

    private async Task<FanArchive> ReadVerifiedFanArchiveAsync(FanEcRecoveryScope scope, CancellationToken token)
    {
        var archive = new AtomicJsonStore(Path.Combine(RootDirectory, "completed-client-sessions"), scope.ClientSessionId.ToString("N") + ".json");
        if (!File.Exists(archive.FilePath) || new FileInfo(archive.FilePath).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("fanPriorSessionNotArchived");
        var saved = await archive.ReadAsync<FanArchive>(token);
        var current = await new ClientControlSessionStore(RootDirectory).ReadAsync(token);
        if (saved?.Session is not { SchemaVersion: 1, Phase: ClientControlSessionPhase.Completed, Handle: not null, Identity: not null } session ||
            session.Handle.SessionId != scope.ClientSessionId || session.ServiceSessionId != scope.ServiceSessionId ||
            session.Handle.HardwareEpoch != scope.HardwareEpoch || session.OwnerSid != scope.OwnerSid || session.Identity != scope.Identity ||
            current is null || session.Handle.DeviceKey != current.Handle.DeviceKey ||
            saved.Recovery.ValueKind != JsonValueKind.Object ||
            !saved.Recovery.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var value) || value != scope.ClientSessionId ||
            !saved.Recovery.TryGetProperty("gpuRecoveryReady", out var gpu) || gpu.ValueKind != JsonValueKind.True ||
            !saved.Recovery.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 64 ||
            steps.EnumerateArray().Any(step => step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() != "Verified"))
            throw new InvalidDataException("fanPriorArchiveMismatch");
        return saved;
    }
}
