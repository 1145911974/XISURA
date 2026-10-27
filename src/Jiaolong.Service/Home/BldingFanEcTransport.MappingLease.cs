using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

internal sealed partial class BldingFanEcTransport
{
    private FanEcMappingLease? mappingLease;
    internal bool HasMappingLease => mappingLease is not null;
    internal bool LeaseCompleted => mappingLease?.Restored == true && checkpointSettled;

    internal bool MatchesRecoveryLease(FanEcRecoveryCheckpoint checkpoint) =>
        originalRegisters is not null && originalRegisters.Count == checkpoint.Baseline.Count &&
        originalRegisters.All(pair => checkpoint.Baseline.TryGetValue(pair.Key, out byte value) && value == pair.Value) &&
        (mappingLease is null ? checkpoint.MappingLease is null : checkpoint.MappingLease is { } lease &&
            mappingLease.Original == lease.Original && mappingLease.TargetsCaptured == lease.TargetsCaptured &&
            mappingLease.TargetWritesStarted == lease.TargetWritesStarted && mappingLease.Restored == lease.Restored);

    private byte AcquireMappingLease(byte original)
    {
        if (Checkpoint is null) throw new IOException("bldingFanBaselineMappingUnavailable");
        previousInitialization = original;
        originalRegisters = []; expectedRegisters = [];
        mappingLease = new(original, original, [], false, false) { TargetWritesStarted = false };
        SaveCheckpoint();
        WriteMapping((byte)(original | 0x80));
        VerifyRecoveryMapping();
        if ((mappingLease.Expected & 0x80) == 0) throw new IOException("bldingFanMappingNotConfirmed");
        // Capture promotion must be a separate durable observation before any target write.
        return mappingLease.Expected;
    }

    private void WriteMapping(byte value)
    {
        EnsureAccess();
        var lease = mappingLease ?? throw new IOException("bldingFanOwnershipMissing");
        mappingLease = lease with { Expected = value, Pending = [(byte)((value & 0x7F) | (lease.Expected & 0x80)), value] };
        mappingLease = mappingLease with { Pending = mappingLease.Pending.Distinct().ToArray() };
        if (mappingLease.TargetsCaptured) expectedRegisters![0x1060] = value;
        SaveCheckpoint();
        EnsureAccess();
        ValueWriteAttempts++;
        try { SelectRegister(0x1060); Write(0x4F, value); }
        finally { EnsureAccess(); }
    }

    private void VerifyRecoveryMapping()
    {
        var lease = mappingLease!;
        byte actual = ReadRegister(0x1060);
        if ((actual & 0x80) != (lease.Expected & 0x80) &&
            !lease.Pending.Any(candidate => (candidate & 0x80) == (actual & 0x80)))
            throw new IOException("bldingFanOwnershipChanged");
        bool restored = (actual & 0x80) == (lease.Original & 0x80);
        if (restored && lease.TargetsCaptured && !lease.Restored &&
            !(lease.Pending.Any(candidate => (candidate & 0x80) == 0) && lease.Pending.Length > 0))
            throw new IOException("bldingFanOwnershipChanged");
        mappingLease = lease with { Expected = actual, Pending = [], Restored = restored };
        if (lease.TargetsCaptured) expectedRegisters![0x1060] = actual;
        SaveCheckpoint(released: restored && lease.TargetWritesStarted, unchanged: restored && !lease.TargetWritesStarted);
        if (restored) HasWritten = false;
        else if (lease.TargetsCaptured) VerifyOwnedRegisters();
    }

    private void ClearMappingTargets()
    {
        if (mappingLease!.Restored)
        {
            VerifyRecoveryMapping();
            return;
        }
        if (!mappingLease.TargetsCaptured || mappingLease.Pending.Length > 0)
        {
            VerifyRecoveryMapping();
            if (!mappingLease.TargetsCaptured || mappingLease.Restored) return;
        }
        VerifyOwnedRegisters();
        if (!mappingLease.TargetWritesStarted) return;
        if (expectedRegisters![0xC83D] != 0) WriteRegister(0xC83D, 0);
        if (expectedRegisters[0xC83C] != 0) WriteRegister(0xC83C, 0);
        byte control = ReadRegister(0x0B20);
        if ((control & 0x0A) != 0) WriteRegister(0x0B20, (byte)(control & ~0x0A));
        VerifyOwnedRegisters();
        if (expectedRegisters[0xC83C] != 0 || expectedRegisters[0xC83D] != 0 || (expectedRegisters[0x0B20] & 0x0A) != 0)
            throw new IOException("bldingFanReleaseNotConfirmed");
        HasWritten = false;
    }

    internal void FinishLease()
    {
        if (mappingLease is null) { Clear(); return; }
        EnsureAccess();
        WithPortEcGate(() =>
        {
            ClearMappingTargets();
            if (mappingLease.Restored) return true;
            byte actual = ReadRegister(0x1060);
            if ((actual & 0x80) == 0) throw new IOException("bldingFanOwnershipChanged");
            // Preserve firmware-owned low bits from the last observation immediately before WAL.
            mappingLease = mappingLease with { Expected = actual };
            if (mappingLease.TargetsCaptured) expectedRegisters![0x1060] = actual;
            WriteMapping((byte)((actual & 0x7F) | (mappingLease.Original & 0x80)));
            VerifyRecoveryMapping();
            if (!mappingLease.Restored) throw new IOException("bldingFanMappingNotConfirmed");
            return true;
        });
    }

    internal FanEcMappingState? ReadMappingState()
    {
        EnsureAccess(readOnly: true);
        if (mappingLease is not { } lease) return null;
        if (!signatureVerified) throw new IOException("bldingEcSignatureNotVerified");
        return WithPortEcGate(() =>
        {
            byte actual = ReadRegister(0x1060, true);
            bool released = lease.TargetsCaptured && pendingRegisterValues.Count == 0 && expectedRegisters is not null &&
                expectedRegisters[0xC83C] == 0 && expectedRegisters[0xC83D] == 0 && (expectedRegisters[0x0B20] & 0x0A) == 0;
            return new FanEcMappingState(DateTimeOffset.UtcNow, actual, lease.Original,
                lease.Restored && checkpointSettled && (actual & 0x80) == (lease.Original & 0x80), lease.TargetWritesStarted, released);
        }, readOnly: true);
    }
}
