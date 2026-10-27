using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private void PersistNativeModeRecovery(HardwareCommand command, byte before, byte target, byte? observed, CancellationToken token)
    {
        if (command is not SetPerformanceModeCommand || controlJournal is null) return;
        if (MiPerformanceModeCodec.Decode(before) is not { } original || MiPerformanceModeCodec.Decode(target) is not { } requested ||
            observed is { } actual && MiPerformanceModeCodec.Decode(actual) is null)
            throw new InvalidDataException("nativeModeRecoveryUnreadable");
        controlJournal.RecordNativeModeAsync(command.OperationId, identity, original, requested,
            observed is byte value ? MiPerformanceModeCodec.Decode(value) : null, token).GetAwaiter().GetResult();
    }
}
