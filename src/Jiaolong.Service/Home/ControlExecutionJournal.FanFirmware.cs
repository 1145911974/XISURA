using System.Text.Json;

namespace Jiaolong.Service.Home;

public sealed record FanFirmwareRecoverySnapshot(FanEcRecoveryScope Scope, byte OriginalBefore, byte Before,
    byte Target, byte? Observed, bool Restoring);

public sealed partial class ControlExecutionJournal
{
    internal static bool FanFirmwareComplete(FanFirmwareRecoverySnapshot record) => record.Restoring && record.Observed == record.Target;
    private static bool ValidFanFirmware(FanFirmwareRecoverySnapshot record) =>
        record.Scope is { ServiceSessionId: var service, HardwareEpoch: > 0, ClientSessionId: var client, Identity: not null } scope &&
        service != Guid.Empty && client != Guid.Empty && !string.IsNullOrWhiteSpace(scope.OwnerSid) &&
        // This provider only writes the firmware flag to disable a pre-read 1, then restores that 1.
        record.OriginalBefore == 1 && record.Before <= 1 && record.Target == (record.Restoring ? 1 : 0) &&
        (record.Observed is null || record.Observed == record.Target);

    internal async Task RecordFanFirmwareAsync(FanEcRecoveryScope scope, byte before, byte target, byte? observed,
        bool restoring, CancellationToken token)
    {
        var capture = await ValidateFanCaptureAsync(scope.Identity, token);
        if (scope != new FanEcRecoveryScope(capture.ServiceSessionId, capture.Handle.HardwareEpoch,
            capture.Handle.SessionId, capture.OwnerSid, capture.Identity)) throw new InvalidDataException("fanFirmwareCaptureMismatch");
        var record = new FanFirmwareRecoverySnapshot(scope, 1, before, target, observed, restoring);
        if (!ValidFanFirmware(record)) throw new InvalidDataException("fanFirmwareCheckpointInvalid");
        await gate.WaitAsync(token);
        try
        {
            var saved = await LoadAsync(token);
            var fresh = await new ControlExecutionJournal(RootDirectory).ReadAsync(token);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved), JsonSerializer.SerializeToElement(fresh)))
                throw new InvalidDataException("fanFirmwareJournalChangedOrMissing");
            var prior = saved.FanFirmwareRecovery;
            if (prior is not null && prior.Scope != scope)
            {
                if (prior.Scope.Identity != scope.Identity) throw new InvalidDataException("fanFirmwareDeviceChanged");
                await VerifyArchivedFanFirmwareAsync(prior, token);
                prior = null;
            }
            if (prior is null && (restoring || before != 1 || observed is not null) || prior is not null && observed is not null &&
                (prior.Before != before || prior.Target != target || prior.Restoring != restoring))
                throw new InvalidDataException("fanFirmwareAttemptChanged");
            var next = Clone(saved with { FanFirmwareRecovery = record });
            await WriteSnapshotAsync(next, token);
            snapshot = next;
        }
        finally { gate.Release(); }
    }
}
