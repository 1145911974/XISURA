using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
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

    private void WriteStrongCoolingLocked(byte value, CancellationToken token)
    {
        var binding = StrongCoolingBinding();
        var write = miInterface.WriteOnceAsync(binding, new byte[] { value }, token).GetAwaiter().GetResult();
        if (write.Quality != DataQuality.Good || !TryReadControlByte(binding, out var after, token) || after != value)
            throw new IOException("strongCoolingWriteNotConfirmed");
    }

    private void PrepareFanStrongCoolingLocked(CancellationToken token)
    {
        if (!TryReadControlByte(StrongCoolingBinding(), out var before, token) || before > 1)
            throw new IOException("strongCoolingReadFailed");
        fanPreviousStrongCooling ??= before;
        if (before != 0) WriteStrongCoolingLocked(0, token);
    }

    private void RestoreStrongCoolingLocked()
    {
        if (fanPreviousStrongCooling is not byte previous) return;
        if (!TryReadControlByte(StrongCoolingBinding(), out var current, CancellationToken.None) || current > 1)
            throw new IOException("strongCoolingRestoreReadFailed");
        if (current != previous) WriteStrongCoolingLocked(previous, CancellationToken.None);
        fanPreviousStrongCooling = null;
    }

    private CommandResult ExecuteStrongCoolingLocked(SetStrongCoolingCommand command, CancellationToken token)
    {
        if (ThirdPartyFanWriterRunning()) return Rejected(command.OperationId, ErrorCode.ConflictDetected);
        try
        {
            // Replace an earlier fan owner before claiming both EC channels for full cooling.
            if (activeFanPlan is not null || fanTouched || strongCoolingActive || fanTransport is not null) ReleaseFanLocked();
            if (!TryReadControlByte(StrongCoolingBinding(), out var before, token) || before > 1)
                return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
            if (command.Enabled)
            {
                var telemetry = ReadTelemetryLocked(token);
                if (!HasSafeFanTelemetry(telemetry))
                    return Rejected(command.OperationId, ErrorCode.ValidationFailed);
                // The firmware strong flag clamps Quiet mode near 3500 RPM on MRID6-23.
                // Use the verified EC targets independently of the performance preset.
                PrepareFanStrongCoolingLocked(token);
                fanTransport ??= new BldingFanEcTransport();
                fanTransport.Open();
                fanTouched |= fanTransport.HasWritten;
                fanTransport.SetTargets(68, 68);
                fanTouched = true;
                activeFanPlan = null;
            }
            else WriteStrongCoolingLocked(0, token);
            strongCoolingActive = command.Enabled;
            if (!command.Enabled) fanPreviousStrongCooling = null;
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
