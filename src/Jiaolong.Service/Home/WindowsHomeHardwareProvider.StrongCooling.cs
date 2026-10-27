using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private long fanFirmwareWriteAttempts;
    private bool? ReadStrongCoolingStateLocked(bool? firmwareEnabled)
    {
        if (!strongCoolingActive) return firmwareEnabled;
        var ec = ReadFanEcControlLocked();
        return ec is null ? null : IsStrongCoolingEcConfirmed(ec);
    }

    private static bool IsStrongCoolingEcConfirmed(FanEcControlState ec) =>
        // Firmware consumes the control request bits after accepting the targets.
        ec.CpuTarget == 68 && ec.GpuTarget == 68 &&
        DateTimeOffset.UtcNow - ec.CapturedAtUtc is var age &&
        age >= TimeSpan.FromSeconds(-1) && age <= TimeSpan.FromSeconds(3);

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
            if (current != 0) throw new IOException("strongCoolingOwnershipChanged");
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
    }

    private CommandResult ExecuteStrongCoolingLocked(SetStrongCoolingCommand command, CancellationToken token)
    {
        if (BackgroundWorkPaused) return Rejected(command.OperationId, ErrorCode.CancelledBeforeApply) with { Mutation = HardwareMutation.NoMutation };
        if (command.Enabled && !HasFanEcProtocol())
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
        if (command.Enabled)
        {
            if (!HasSafeFanTelemetry(ReadTelemetryLocked(token)))
                return Rejected(command.OperationId, ErrorCode.ValidationFailed) with { Mutation = HardwareMutation.NoMutation };
            // Open modifies the EC initialization bit; dependency preflight must remain read-only.
            if (fanTransport is null && !PawnIoLpcEcPorts.HasTrustedDependency())
                return Rejected(command.OperationId, ErrorCode.DependencyMissing) with { Mutation = HardwareMutation.NoMutation };
        }
        try
        {
            // Replace an earlier fan owner before claiming both EC channels for full cooling.
            DiscardCompletedFanTransport();
            if (activeFanPlan is not null || fanTouched || strongCoolingActive || fanTransport is not null) ReleaseFanLocked();
            if (command.Enabled)
            {
                // The firmware strong flag clamps Quiet mode near 3500 RPM on MRID6-23.
                // Use the verified EC targets independently of the performance preset.
                PrepareFanStrongCoolingLocked(token);
                fanTransport = RetainOrCreateFanTransport();
                fanTransport.Open();
                fanTouched |= fanTransport.HasWritten;
                fanTransport.SetTargets(68, 68);
                fanTouched = true;
                activeFanPlan = null;
            }
            else PrepareFanStrongCoolingLocked(token, captureUnchanged: false);
            strongCoolingActive = command.Enabled;
            fanTimer ??= new Timer(_ => FanTick(), null, Timeout.Infinite, Timeout.Infinite);
            fanTimer.Change(command.Enabled ? 1000 : Timeout.Infinite, command.Enabled ? 1000 : Timeout.Infinite);
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
