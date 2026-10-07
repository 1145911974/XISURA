using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private static readonly TimeSpan GpuVfRefreshInterval = TimeSpan.FromSeconds(2);

    private bool IsVerifiedGpuVfDevice() =>
        compatibilityDecision?.Capabilities.Items.Any(item => item.Key == "gpuVfCurve" &&
            item.State == CapabilityState.Available) == true;

    private GpuVfState ReadGpuVfLocked(CancellationToken cancellationToken, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!force && gpuVfCache is not null && DateTimeOffset.UtcNow - gpuVfCacheAt < GpuVfRefreshInterval)
            return gpuVfCache;
        if (!IsVerifiedGpuVfDevice())
            return gpuVfCache = new GpuVfState([], -200_000, 200_000, "gpuVfDeviceNotVerified");

        try
        {
            using var nvapi = new NvapiVfInterop();
            var (gpu, mask) = nvapi.OpenSingleGpu();
            var curve = nvapi.ReadCurve(gpu, mask);
            if (curve.Length != 127) throw new InvalidDataException($"Unexpected GPU V/F node count {curve.Length}");
            var deltas = nvapi.ReadDeltas(gpu, mask, curve.Length);
            NvapiVfInterop.PstateMemory? memory = null;
            (int MinimumKhz, int MaximumKhz)? coreRange = null;
            try { memory = nvapi.ReadPstate0MemoryOffset(gpu); }
            catch (Exception exception) { logger?.LogDebug(exception, "GPU memory offset read unavailable"); }
            try { coreRange = nvapi.ReadGraphicsDeltaRange(gpu); }
            catch (Exception exception) { logger?.LogDebug(exception, "GPU core offset range read unavailable"); }
            gpuVfCache = new GpuVfState(curve.Select((node, index) =>
                new GpuVfNode(node.VoltageMv, node.BaseMhz, deltas[index])).ToArray(),
                -200_000, 200_000, null)
            {
                MemoryOffsetKhz = memory?.OffsetKhz,
                MemoryMinimumOffsetKhz = memory?.MinimumKhz,
                MemoryMaximumOffsetKhz = memory?.MaximumKhz,
                CoreOffsetKhz = memory?.CorePstateOffsetKhz,
                CoreMinimumOffsetKhz = coreRange?.MinimumKhz,
                CoreMaximumOffsetKhz = coreRange?.MaximumKhz
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "GPU V/F read failed");
            gpuVfCache = new GpuVfState([], -200_000, 200_000, "gpuVfReadFailed");
        }
        gpuVfCacheAt = DateTimeOffset.UtcNow;
        return gpuVfCache;
    }

    private CommandResult ExecuteGpuVfLocked(SetGpuVfCurveCommand command, CancellationToken cancellationToken)
    {
        if (gpuMemoryRecoveryRequired || gpuCoreRecoveryRequired || !IsVerifiedGpuVfDevice() ||
            CommandValidation.Validate(command) is not null)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        if (SystemPowerStatusReader.Read().AcPowerConnected is not true)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        var temperature = ReadTelemetryLocked(cancellationToken).GpuTemperatureC;
        if (temperature is null or >= 75 or < 0)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);

        try
        {
            using var nvapi = new NvapiVfInterop();
            var (gpu, mask) = nvapi.OpenSingleGpu();
            if (nvapi.ReadCurve(gpu, mask).Length != 127)
                return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
            var before = nvapi.ReadDeltas(gpu, mask, 127);
            if (!before.SequenceEqual(command.ExpectedOffsetsKhz))
                return Rejected(command.OperationId, ErrorCode.ReadBackMismatch);
            if (before.SequenceEqual(command.OffsetsKhz))
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);

            try
            {
                // A failed NVAPI call may have partially applied the table; always inspect before deciding recovery.
                nvapi.WriteDeltas(gpu, mask, command.OffsetsKhz);
                if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(command.OffsetsKhz))
                    throw new InvalidDataException("GPU V/F readback mismatch");
                gpuVfCacheAt = DateTimeOffset.MinValue;
                ReadGpuVfLocked(CancellationToken.None, force: true);
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "GPU V/F apply failed; restoring original table");
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before))
                            nvapi.WriteDeltas(gpu, mask, before);
                        if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before)) continue;
                        gpuVfCacheAt = DateTimeOffset.MinValue;
                        ReadGpuVfLocked(CancellationToken.None, force: true);
                        return new CommandResult(command.OperationId, CommandState.RolledBack, null,
                            RequiredUserAction.None, ServiceError.Create(ErrorCode.ReadBackMismatch, command.OperationId, false), false);
                    }
                    catch (Exception recoveryException)
                    {
                        logger?.LogWarning(recoveryException, "GPU V/F recovery attempt {Attempt} failed", attempt + 1);
                    }
                }
                gpuVfCache = new GpuVfState([], -200_000, 200_000, "gpuVfRecoveryRequired");
                gpuVfCacheAt = DateTimeOffset.UtcNow;
                return new CommandResult(command.OperationId, CommandState.RecoveryRequired, null,
                    RequiredUserAction.None, ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false), false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "GPU V/F initial read failed");
            return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
        }
    }

    private CommandResult ExecuteGpuMemoryOffsetLocked(SetGpuMemoryOffsetCommand command, CancellationToken cancellationToken)
        => ExecuteGpuPstateOffsetLocked(command, command.ExpectedOffsetKhz, command.OffsetKhz, core: false, cancellationToken);

    private CommandResult ExecuteGpuCoreOffsetLocked(SetGpuCoreOffsetCommand command, CancellationToken cancellationToken)
        => ExecuteGpuPstateOffsetLocked(command, command.ExpectedOffsetKhz, command.OffsetKhz, core: true, cancellationToken);

    private CommandResult ExecuteGpuPstateOffsetLocked(HardwareCommand command, int expected, int target,
        bool core, CancellationToken cancellationToken)
    {
        if (gpuMemoryRecoveryRequired || gpuCoreRecoveryRequired || !IsVerifiedGpuVfDevice() ||
            CommandValidation.Validate(command) is not null)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        if (SystemPowerStatusReader.Read().AcPowerConnected is not true)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        var temperature = ReadTelemetryLocked(cancellationToken).GpuTemperatureC;
        if (temperature is null or < 0 or >= 75)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);

        try
        {
            using var nvapi = new NvapiVfInterop();
            var (gpu, mask) = nvapi.OpenSingleGpu();
            if (nvapi.ReadCurve(gpu, mask).Length != 127)
                return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
            var before = nvapi.ReadPstate0MemoryOffset(gpu);
            var coreRange = core ? nvapi.ReadGraphicsDeltaRange(gpu) : null;
            int current = core ? before.CorePstateOffsetKhz : before.OffsetKhz;
            int minimum = core ? coreRange?.MinimumKhz ?? int.MaxValue : before.MinimumKhz;
            int maximum = core ? coreRange?.MaximumKhz ?? int.MinValue : before.MaximumKhz;
            if (current != expected)
                return Rejected(command.OperationId, ErrorCode.ReadBackMismatch);
            if (minimum > target || maximum < target || minimum > maximum)
                return Rejected(command.OperationId, ErrorCode.ValidationFailed);
            var originalCurve = nvapi.ReadDeltas(gpu, mask, 127);
            // Validate the table under the same gate as the write, including a no-op core reset.
            if (command is SetGpuCoreOffsetCommand { ExpectedVfOffsetsKhz: { } expectedCurveOffsets } &&
                !originalCurve.SequenceEqual(expectedCurveOffsets))
                return Rejected(command.OperationId, ErrorCode.ReadBackMismatch);
            if (current == target)
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
            var expectedState = core ? before with { CorePstateOffsetKhz = target } : before with { OffsetKhz = target };
            int delta = core ? target - current : 0;
            var expectedCurve = originalCurve.Select((value, index) => index == 0 ? value : checked(value + delta)).ToArray();
            if (expectedCurve.Skip(1).Any(value => value is < -200_000 or > 200_000))
                return Rejected(command.OperationId, ErrorCode.ValidationFailed);

            try
            {
                nvapi.WritePstate0Offsets(gpu, before,
                    core ? target : before.CorePstateOffsetKhz, core ? before.OffsetKhz : target);
                // This driver rebuilds the V/F table on a P0 write, even for memory-only edits.
                // Restore the requested curve before validating the complete transaction.
                if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(expectedCurve))
                    nvapi.WriteDeltas(gpu, mask, expectedCurve);
                var actual = nvapi.ReadPstate0MemoryOffset(gpu);
                if (actual != expectedState ||
                    !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(expectedCurve) ||
                    SystemPowerStatusReader.Read().AcPowerConnected is not true ||
                    ReadTelemetryLocked(CancellationToken.None).GpuTemperatureC is not double currentTemperature ||
                    currentTemperature is < 0 or >= 75)
                    throw new InvalidDataException("GPU P0 offset or adjacent state readback mismatch");
                gpuVfCacheAt = DateTimeOffset.MinValue;
                var refreshed = ReadGpuVfLocked(CancellationToken.None, force: true);
                if ((core ? refreshed.CoreOffsetKhz : refreshed.MemoryOffsetKhz) != target)
                    throw new InvalidDataException("GPU P0 offset state refresh mismatch");
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "GPU P0 offset apply failed; restoring original P0 state");
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (nvapi.ReadPstate0MemoryOffset(gpu) != before)
                            nvapi.WritePstate0Offsets(gpu, before, before.CorePstateOffsetKhz, before.OffsetKhz);
                        if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(originalCurve))
                            nvapi.WriteDeltas(gpu, mask, originalCurve);
                        if (nvapi.ReadPstate0MemoryOffset(gpu) != before ||
                            !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(originalCurve)) continue;
                        gpuVfCacheAt = DateTimeOffset.MinValue;
                        ReadGpuVfLocked(CancellationToken.None, force: true);
                        return new CommandResult(command.OperationId, CommandState.RolledBack, null,
                            RequiredUserAction.None, ServiceError.Create(ErrorCode.ReadBackMismatch, command.OperationId, false), false);
                    }
                    catch (Exception recoveryException)
                    {
                        logger?.LogWarning(recoveryException, "GPU P0 offset recovery attempt {Attempt} failed", attempt + 1);
                    }
                }
                gpuMemoryRecoveryRequired = true;
                gpuCoreRecoveryRequired = true;
                gpuVfCache = new GpuVfState([], -200_000, 200_000, "gpuPstateRecoveryRequired");
                gpuVfCacheAt = DateTimeOffset.UtcNow;
                return new CommandResult(command.OperationId, CommandState.RecoveryRequired, null,
                    RequiredUserAction.None, ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false), false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "GPU P0 offset initial read failed");
            return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
        }
    }
}
