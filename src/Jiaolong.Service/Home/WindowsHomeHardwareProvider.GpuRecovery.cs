using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    public Task<bool> VerifyPendingGpuTargetAsync(Guid operationId, CancellationToken token)
    {
        lock (gate)
        {
            try
            {
                ThrowIfDisposed();
                EnsureGpuWriteAccess(token);
                var operation = controlJournal?.CurrentGpuOperation;
                if (operation is null || operation.OperationId != operationId || operationId == Guid.Empty) return Task.FromResult(false);
                var attempt = controlJournal!.ReadGpuAttemptFromDiskAsync(operation, token).GetAwaiter().GetResult();
                if (identity != attempt.Before.Identity) return Task.FromResult(false);
                using var nvapi = CreateGpuVfInterop();
                var (gpu, mask) = nvapi.OpenSingleGpu();
                bool Matches() => nvapi.ReadAdapterIdentity(gpu) == attempt.Before.Adapter &&
                    nvapi.ReadPstate0MemoryOffset(gpu) == RecoveryMemory(attempt.Pstate) &&
                    nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(attempt.VfOffsetsKhz);
                if (!Matches() || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(attempt), JsonSerializer.SerializeToElement(
                    controlJournal.ReadGpuAttemptFromDiskAsync(operation, token).GetAwaiter().GetResult())) || !Matches())
                    return Task.FromResult(false);
                EnsureGpuWriteAccess(token);
                if (controlJournal.CurrentGpuOperation != operation || identity != attempt.Before.Identity) return Task.FromResult(false);
                gpuVfRecoveryRequired = gpuMemoryRecoveryRequired = gpuCoreRecoveryRequired = false;
                ClearGpuRecoverySnapshots();
                return Task.FromResult(true);
            }
            catch (Exception error) when (error is not OperationCanceledException) { return Task.FromResult(false); }
        }
    }

    public Task<CommandResult> RecoverPendingGpuAsync(Guid operationId, CancellationToken token)
    {
        lock (gate) return Task.FromResult(RecoverPendingGpuLocked(operationId, token));
    }

    private CommandResult RecoverPendingGpuLocked(Guid operationId, CancellationToken token)
    {
        NvapiVfInterop? transport = null;
        bool changed = false;
        try
        {
            ThrowIfDisposed();
            EnsureGpuWriteAccess(token);
            var operation = controlJournal?.CurrentGpuOperation;
            if (operation is null || operation.OperationId != operationId || operationId == Guid.Empty)
                throw new InvalidDataException("gpuRecoveryAdmissionMissing");
            var attempt = controlJournal!.ReadGpuAttemptFromDiskAsync(operation, token).GetAwaiter().GetResult();
            var before = attempt.Before;
            var target = RecoveryMemory(before.Pstate);
            if (identity != before.Identity ||
                before.VfOffsetsKhz.Any(value => value is < -200000 or > 200000))
                throw new InvalidDataException("gpuRecoverySafetyCheckFailed");
            EnsurePendingGpuSafety(token);
            using (var nvapi = transport = CreateGpuVfInterop())
            {
                var (gpu, mask) = nvapi.OpenSingleGpu();
                if (nvapi.ReadAdapterIdentity(gpu) != before.Adapter) throw new InvalidDataException("gpuRecoveryAdapterChanged");
                var observed = nvapi.ReadPstate0MemoryOffset(gpu);
                var curve = nvapi.ReadDeltas(gpu, mask, 127);
                if (!KnownGpuIntermediate(attempt, observed, curve)) throw new InvalidDataException("gpuRecoveryExternalState");
                if (observed.CorePstateOffsetKhz != target.CorePstateOffsetKhz &&
                    (nvapi.ReadGraphicsDeltaRange(gpu) is not { } range || target.CorePstateOffsetKhz < range.MinimumKhz || target.CorePstateOffsetKhz > range.MaximumKhz))
                    throw new InvalidDataException("gpuRecoveryCoreRangeUnavailable");
                var latest = controlJournal.ReadGpuAttemptFromDiskAsync(operation, token).GetAwaiter().GetResult();
                if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(attempt), JsonSerializer.SerializeToElement(latest)) ||
                    controlJournal.CurrentGpuOperation != operation || identity != before.Identity ||
                    nvapi.ReadAdapterIdentity(gpu) != before.Adapter || nvapi.ReadPstate0MemoryOffset(gpu) != observed ||
                    !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(curve))
                    throw new InvalidDataException("gpuRecoveryStateChanged");
                EnsurePendingGpuSafety(token);
                gpuRecoveryOperation = operation; gpuRecoveryAdapter = before.Adapter; gpuRecoveryIdentity = before.Identity;
                gpuRecoveryStepId = attempt.StepId;
                gpuVfRecoveryOffsets = before.VfOffsetsKhz.ToArray(); gpuPstateRecoverySnapshot = target;
                gpuVfRecoveryRequired = gpuMemoryRecoveryRequired = gpuCoreRecoveryRequired = true;
                if (observed != target)
                {
                    nvapi.WritePstate0Offsets(gpu, observed, target.CorePstateOffsetKhz, target.OffsetKhz);
                    EnsureGpuWriteAccess(token);
                }
                if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before.VfOffsetsKhz))
                {
                    nvapi.WriteDeltas(gpu, mask, before.VfOffsetsKhz);
                    EnsureGpuWriteAccess(token);
                }
                if (nvapi.ReadPstate0MemoryOffset(gpu) != target || !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before.VfOffsetsKhz))
                    throw new InvalidDataException("gpuRecoveryReadbackMismatch");
                var fresh = ReadGpuVfLocked(token, force: true);
                if (fresh is not { Error: null, Nodes.Length: 127 } || fresh.CoreOffsetKhz != target.CorePstateOffsetKhz ||
                    fresh.MemoryOffsetKhz != target.OffsetKhz || !fresh.Nodes.Select(node => node.OffsetKhz).SequenceEqual(before.VfOffsetsKhz) ||
                    nvapi.ReadAdapterIdentity(gpu) != before.Adapter)
                    throw new InvalidDataException("gpuRecoveryRefreshFailed");
                changed = nvapi.WriteAttempted;
            }
            EnsureGpuWriteAccess(token);
            if (controlJournal.CurrentGpuOperation != operation || identity != before.Identity)
                throw new InvalidDataException("gpuRecoveryAdmissionRevoked");
            gpuVfRecoveryRequired = gpuMemoryRecoveryRequired = gpuCoreRecoveryRequired = false;
            ClearGpuRecoverySnapshots();
            return new(operationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = changed ? HardwareMutation.Changed : HardwareMutation.NoMutation };
        }
        catch (Exception error)
        {
            changed |= transport?.WriteAttempted == true;
            return new(operationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.RollbackFailed, operationId, false) with
                { Details = new() { ["reason"] = error.Message, ["writeAttempted"] = changed.ToString() } }, false)
                { Mutation = changed ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
        }
    }

    private static NvapiVfInterop.PstateMemory RecoveryMemory(GpuPstateBaseline value) => new(value.MemoryOffsetKhz,
        value.MemoryMinimumKhz, value.MemoryMaximumKhz, value.CoreOffsetKhz, value.CoreVoltageDeltaUv, value.StateCount, value.ClockCount);

    private void EnsurePendingGpuSafety(CancellationToken token)
    {
        EnsureGpuWriteAccess(token);
        if (!IsVerifiedGpuVfDevice())
            throw new InvalidDataException("gpuRecoverySafetyCheckFailed");
        EnsureGpuTuningConditions(token);
    }

    private static bool KnownGpuIntermediate(GpuRecoveryAttempt attempt, NvapiVfInterop.PstateMemory observed, int[] curve)
    {
        var before = RecoveryMemory(attempt.Before.Pstate);
        var target = RecoveryMemory(attempt.Pstate);
        if (curve.Length != 127 || observed with { OffsetKhz = before.OffsetKhz, CorePstateOffsetKhz = before.CorePstateOffsetKhz } != before ||
            observed.OffsetKhz != before.OffsetKhz && observed.OffsetKhz != target.OffsetKhz ||
            observed.CorePstateOffsetKhz != before.CorePstateOffsetKhz && observed.CorePstateOffsetKhz != target.CorePstateOffsetKhz)
            return false;
        // P0 writes rebuild the table to zero; V/F writes can only contain before/target entries.
        return curve.Select((value, index) => value == attempt.Before.VfOffsetsKhz[index] || value == attempt.VfOffsetsKhz[index] ||
            attempt.Kind != "vf" && value == 0).All(known => known);
    }
}
