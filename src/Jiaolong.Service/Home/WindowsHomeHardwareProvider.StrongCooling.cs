using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private long fanFirmwareWriteAttempts;
    private byte? fanExpectedStrongCooling;
    private byte? fanPendingStrongCoolingBefore;
    public Task RetainClientNativeCoolingAsync(CancellationToken token)
    {
        lock (commandGate) lock (gate)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (fanPreviousStrongCooling is null || fanTransport is not null || activeFanPlan is not null || fanTouched)
                return Task.CompletedTask;
            if (!TryReadControlByte(StrongCoolingBinding(), out var current, token) || current != fanExpectedStrongCooling)
                throw new IOException("strongCoolingRetainedOwnershipChanged");
            fanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            strongCoolingActive = false;
            fanPreviousStrongCooling = fanExpectedStrongCooling = fanPendingStrongCoolingBefore = null;
            return Task.CompletedTask;
        }
    }
    private bool? ReadStrongCoolingStateLocked(bool? firmwareEnabled) => firmwareEnabled;

    private VerifiedWmiBinding StrongCoolingBinding()
    {
        if (compatibilityDecision is null || !MiHomeControlBindingFactory.TryCreate(compatibilityDecision, MiHomeControlKind.StrongCooling, out var binding))
            throw new IOException("strongCoolingBindingUnavailable");
        return binding!;
    }

    private void WriteStrongCoolingLocked(byte value, CancellationToken token, byte before, bool restoring)
    {
        var scope = FanFirmwareScopeLocked(token);
        if (scope is not null) controlJournal!.RecordFanFirmwareAsync(scope, before, value, null, restoring, token).GetAwaiter().GetResult();
        fanExpectedStrongCooling = value;
        fanPendingStrongCoolingBefore = before;
        var binding = StrongCoolingBinding();
        EnsureFanRecoveryAccess();
        MiReadResult write;
        try { write = miInterface.WriteOnceAsync(binding, new byte[] { value }, token).GetAwaiter().GetResult(); }
        catch (Exception error) { if (error.Data["miWriteAttempted"] is true) fanFirmwareWriteAttempts++; throw; }
        if (write.Reason != "writeTransportUnavailable") fanFirmwareWriteAttempts++;
        EnsureFanRecoveryAccess();
        if (write.Quality != DataQuality.Good || !TryReadControlByte(binding, out var after, token) || after != value)
            throw new IOException("strongCoolingWriteNotConfirmed");
        if (scope is not null) controlJournal!.RecordFanFirmwareAsync(scope, before, value, value, restoring, token).GetAwaiter().GetResult();
        fanPendingStrongCoolingBefore = null;
    }

    private FanEcRecoveryScope? FanFirmwareScopeLocked(CancellationToken token)
    {
        if (controlJournal is null) return null;
        var capture = controlJournal.ValidateFanCaptureAsync(identity, token).GetAwaiter().GetResult();
        return new(capture.ServiceSessionId, capture.Handle.HardwareEpoch, capture.Handle.SessionId, capture.OwnerSid, identity);
    }

    private void PrepareFanStrongCoolingLocked(CancellationToken token, bool captureUnchanged = true)
    {
        if (!TryReadControlByte(StrongCoolingBinding(), out var before, token) || before > 1)
            throw new IOException("strongCoolingReadFailed");
        if (before != 0 || captureUnchanged) fanPreviousStrongCooling ??= before;
        if (before != 0) WriteStrongCoolingLocked(0, token, before, restoring: false);
    }

    private void RestoreStrongCoolingLocked()
    {
        EnsureFanRecoveryAccess();
        if (fanPreviousStrongCooling is not byte previous) return;
        if (!TryReadControlByte(StrongCoolingBinding(), out var current, CancellationToken.None) || current > 1)
            throw new IOException("strongCoolingRestoreReadFailed");
        if (current != previous)
        {
            var record = controlJournal?.ReadAsync(CancellationToken.None).GetAwaiter().GetResult().FanFirmwareRecovery;
            byte? expected = record is not null && record.Scope == FanFirmwareScopeLocked(CancellationToken.None) ? record.Target : fanExpectedStrongCooling;
            bool pendingBefore = record is { Observed: null } ? current == record.Before :
                record is null && current == fanPendingStrongCoolingBefore;
            if (current != expected && !pendingBefore) throw new IOException("strongCoolingOwnershipChanged");
            WriteStrongCoolingLocked(previous, CancellationToken.None, current, restoring: true);
        }
        else if (controlJournal is not null)
        {
            var prior = controlJournal.ReadAsync(CancellationToken.None).GetAwaiter().GetResult().FanFirmwareRecovery;
            if (prior is not null && !ControlExecutionJournal.FanFirmwareComplete(prior))
            {
                var scope = FanFirmwareScopeLocked(CancellationToken.None)!;
                if (prior.Scope != scope || previous != prior.OriginalBefore) throw new IOException("strongCoolingRecoveryScopeChanged");
                controlJournal.RecordFanFirmwareAsync(scope, current, previous, null, true, CancellationToken.None).GetAwaiter().GetResult();
                controlJournal.RecordFanFirmwareAsync(scope, current, previous, previous, true, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        fanPreviousStrongCooling = null;
        fanExpectedStrongCooling = null;
        fanPendingStrongCoolingBefore = null;
    }

    private CommandResult ExecuteStrongCoolingLocked(SetStrongCoolingCommand command, CancellationToken token)
    {
        if (BackgroundWorkPaused) return Rejected(command.OperationId, ErrorCode.CancelledBeforeApply) with { Mutation = HardwareMutation.NoMutation };
        if (!string.Equals(identity.BoardProduct, "MRID6-23", StringComparison.OrdinalIgnoreCase) ||
            compatibilityDecision?.Mode != Jiaolong.Hardware.Abstractions.Compatibility.CompatibilityMode.Writable ||
            !MiHomeControlBindingFactory.TryCreate(compatibilityDecision, MiHomeControlKind.StrongCooling, out _))
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
        if (ThirdPartyFanWriterRunning()) return Rejected(command.OperationId, ErrorCode.ConflictDetected) with { Mutation = HardwareMutation.NoMutation };
        try
        {
            if (!TryReadControlByte(StrongCoolingBinding(), out var before, token) || before > 1)
                return Rejected(command.OperationId, ErrorCode.HardwareReadFailed) with { Mutation = HardwareMutation.NoMutation };
        }
        catch (Exception error)
        {
            var code = error is OperationCanceledException || error.Message == "hardwareAccessPaused" ? ErrorCode.CancelledBeforeApply : ErrorCode.HardwareReadFailed;
            return Rejected(command.OperationId, code) with
            {
                Mutation = HardwareMutation.NoMutation,
                Error = ServiceError.Create(code, command.OperationId, false) with
                { Details = new() { ["reason"] = error.Message, ["writeAttempted"] = bool.FalseString } }
            };
        }
        try
        {
            // Release an owned curve before handing control to the firmware's mode-dependent boost.
            DiscardCompletedFanTransport();
            if (activeFanPlan is not null || fanTouched || fanTransport is not null) FinishFanLocked();
            if (!TryReadControlByte(StrongCoolingBinding(), out byte current, token) || current > 1)
                throw new IOException("strongCoolingReadFailed");
            if (fanExpectedStrongCooling is byte expected && current != expected)
                throw new IOException("strongCoolingOwnershipChanged");
            byte target = command.Enabled ? (byte)1 : (byte)0;
            if (current == target) return FanSubmitted(command.OperationId) with { Mutation = HardwareMutation.NoMutation };
            fanPreviousStrongCooling ??= current;
            WriteStrongCoolingLocked(target, token, current, restoring: false);
            strongCoolingActive = command.Enabled;
            fanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return FanSubmitted(command.OperationId);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Firmware strong cooling failed; restoring prior state");
            fanTouched |= fanTransport?.HasWritten == true;
            try { ReleaseFanLocked(); }
            catch { return FanRecoveryRequired(command.OperationId); }
            return Rejected(command.OperationId, ErrorCode.HardwareWriteFailed);
        }
    }
}
